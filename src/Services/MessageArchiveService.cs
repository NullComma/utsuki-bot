using App.Attributes;
using App.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

[Service]
public class MessageArchiveService
{
    const string DB_PATH = "../JsonData/Archive/messages.db";

    readonly DiscordSocketClient _discord;
    readonly LoggingService _log;
    readonly SemaphoreSlim _dbLock = new(1, 1);
    bool _backfillStarted;
    bool _backfillComplete;

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
            _ = BackfillAllChannelsAsync();
        }
    }

    async Task OnMessageReceived(SocketMessage socketMessage)
    {
        if (socketMessage is not SocketUserMessage userMessage) return;
        if (userMessage.Author.IsBot) return;
        if (string.IsNullOrEmpty(userMessage.Content)) return;

        try
        {
            using var ctx = CreateContext();

            if (await ctx.Messages.AnyAsync(m => m.MessageId == userMessage.Id))
                return;

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
        var oldestTimestamp = DateTime.MaxValue;

        _log.Info($"Backfilling #{channel.Name}...");

        while (true)
        {
            IReadOnlyCollection<IMessage> messages;
            if (oldestTimestamp == DateTime.MaxValue)
            {
                messages = await channel.GetMessagesAsync(100).FlattenAsync();
            }
            else
            {
                messages = await channel.GetMessagesAsync(oldestTimestamp, Direction.Before, 100).FlattenAsync();
            }

            if (!messages.Any()) break;

            using var ctx = CreateContext();
            var newRecords = new List<MessageRecord>();

            foreach (var msg in messages)
            {
                if (msg is not IUserMessage userMsg) continue;
                if (userMsg.Author.IsBot) continue;
                if (string.IsNullOrEmpty(userMsg.Content)) continue;

                var exists = await ctx.Messages.AnyAsync(m => m.MessageId == msg.Id);
                if (exists) continue;

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

                oldestTimestamp = msg.Timestamp.UtcDateTime;
            }

            if (newRecords.Count > 0)
            {
                await _dbLock.WaitAsync();
                try
                {
                    ctx.Messages.AddRange(newRecords);
                    await ctx.SaveChangesAsync();
                }
                finally
                {
                    _dbLock.Release();
                }
                totalSaved += newRecords.Count;
            }

            if (messages.Count < 100) break;

            await Task.Delay(200);
        }

        _log.Info($"Backfill for #{channel.Name} complete. Saved {totalSaved} messages.");
    }

    ArchiveDbContext CreateContext()
    {
        var connection = new SqliteConnection($"Data Source={DB_PATH}");
        connection.Open();
        return new ArchiveDbContext(
            new DbContextOptionsBuilder<ArchiveDbContext>()
                .UseSqlite(connection)
                .Options
        );
    }
}
