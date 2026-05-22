using App.Attributes;
using App.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

[Service]
public class MessageArchiveService
{
    const string DB_PATH = "../JsonData/Archive/messages.db";

    readonly DiscordSocketClient _discord;
    readonly LoggingService _log;
    readonly SemaphoreSlim _dbLock = new(1, 1);
    volatile bool _backfillStarted;
    volatile bool _backfillComplete;

    public MessageArchiveService(DiscordSocketClient discord, LoggingService log)
    {
        _discord = discord;
        _log = log;

        EnsureDatabase();
    }

    void EnsureDatabase()
    {
        var dir = Path.GetDirectoryName(DB_PATH);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();

        var hasMessages = ctx.Messages.Any();
        _backfillComplete = hasMessages;

        _log.Info($"Archive database ready at {DB_PATH} (has data: {hasMessages})");
    }

    public void Start()
    {
        _discord.MessageReceived += OnMessageReceived;
        if (!_backfillComplete)
        {
            if (_discord.ConnectionState == ConnectionState.Connected)
                _ = Task.Run(BackfillAllChannelsAsync);
            else
                _discord.Ready += OnReady;
        }
    }

    async Task OnReady()
    {
        _discord.Ready -= OnReady;
        await Task.Run(BackfillAllChannelsAsync);
    }

    async Task OnMessageReceived(SocketMessage socketMessage)
    {
        if (socketMessage is not SocketUserMessage userMessage) return;
        if (userMessage.Author.IsBot) return;
        if (string.IsNullOrEmpty(userMessage.Content)) return;

        try
        {
            var record = new MessageRecord
            {
                MessageId = userMessage.Id,
                ChannelId = userMessage.Channel.Id,
                GuildId = (userMessage.Channel as IGuildChannel)?.GuildId ?? 0,
                AuthorId = userMessage.Author.Id,
                AuthorName = userMessage.Author.Username,
                Content = userMessage.Content,
                Timestamp = userMessage.Timestamp.UtcDateTime,
                Attachments = userMessage.Attachments.Count > 0
                    ? string.Join(";", userMessage.Attachments.Select(a => a.Url))
                    : null
            };

            await _dbLock.WaitAsync();
            try
            {
                using var ctx = CreateContext();
                if (await ctx.Messages.AnyAsync(m => m.MessageId == userMessage.Id))
                    return;
                ctx.Messages.Add(record);
                await ctx.SaveChangesAsync();
            }
            finally
            {
                _dbLock.Release();
            }
        }
        catch (Exception e)
        {
            _log.Error($"Failed to archive message: {e.Message}");
        }
    }

    async Task BackfillAllChannelsAsync()
    {
        if (_backfillStarted) return;
        _backfillStarted = true;

        _log.Info("Starting full channel backfill...");

        foreach (var guild in _discord.Guilds)
        {
            foreach (var channel in guild.TextChannels)
            {
                try
                {
                    await BackfillChannelAsync(channel);
                }
                catch (Exception e)
                {
                    _log.Error($"Backfill failed for #{channel.Name} ({channel.Id}): {e.Message}");
                }
            }
        }

        _backfillComplete = true;
        _log.Info("Full backfill complete. All channels archived.");
    }

    async Task BackfillChannelAsync(SocketTextChannel channel)
    {
        var totalSaved = 0;
        ulong? oldestMessageId = null;

        _log.Info($"Backfilling #{channel.Name}...");

        while (true)
        {
            IReadOnlyCollection<IMessage> messages;
            if (oldestMessageId == null)
            {
                messages = await channel.GetMessagesAsync(100).FlattenAsync();
            }
            else
            {
                messages = await channel.GetMessagesAsync(oldestMessageId.Value, Direction.Before, 100).FlattenAsync();
            }

            if (!messages.Any()) break;

            var newRecords = new List<MessageRecord>();

            foreach (var msg in messages)
            {
                if (msg is not IUserMessage userMsg) continue;
                if (userMsg.Author.IsBot) continue;
                if (string.IsNullOrEmpty(userMsg.Content)) continue;

                newRecords.Add(new MessageRecord
                {
                    MessageId = msg.Id,
                    ChannelId = channel.Id,
                    GuildId = channel.Guild.Id,
                    AuthorId = msg.Author.Id,
                    AuthorName = msg.Author.Username,
                    Content = userMsg.Content,
                    Timestamp = msg.Timestamp.UtcDateTime,
                    Attachments = msg.Attachments.Count > 0
                        ? string.Join(";", msg.Attachments.Select(a => a.Url))
                        : null
                });
            }

            if (newRecords.Count > 0)
            {
                await _dbLock.WaitAsync();
                try
                {
                    using var ctx = CreateContext();
                    ctx.Messages.AddRange(newRecords);
                    await ctx.SaveChangesAsync();
                }
                catch (DbUpdateException) when (newRecords.Count > 0)
                {
                    using var ctx = CreateContext();
                    foreach (var record in newRecords)
                    {
                        if (!await ctx.Messages.AnyAsync(m => m.MessageId == record.MessageId))
                        {
                            ctx.Messages.Add(record);
                            await ctx.SaveChangesAsync();
                        }
                    }
                }
                finally
                {
                    _dbLock.Release();
                }
                totalSaved += newRecords.Count;
            }

            oldestMessageId = messages.Last().Id;

            if (messages.Count < 100) break;

            await Task.Delay(200);
        }

        _log.Info($"Backfill for #{channel.Name} complete. Saved {totalSaved} messages.");
    }

    ArchiveDbContext CreateContext()
    {
        return new ArchiveDbContext(
            new DbContextOptionsBuilder<ArchiveDbContext>()
                .UseSqlite($"Data Source={DB_PATH}")
                .Options
        );
    }
}
