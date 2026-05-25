using App.Attributes;
using App.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

public class ArchiveStats
{
    public long TotalMessages { get; set; }
    public int TotalChannels { get; set; }
    public DateTime? OldestMessage { get; set; }
    public DateTime? NewestMessage { get; set; }
    public bool IsBackfilling { get; set; }
    public long DbSizeBytes { get; set; }
    public int TotalUsers { get; set; }
}

public class UserStats
{
    public ulong UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public long MessageCount { get; set; }
    public DateTime? FirstMessage { get; set; }
    public DateTime? LastMessage { get; set; }
    public int ChannelCount { get; set; }
}

enum BackfillResult { Complete, NeedRetry, Skipped }

class RetryState
{
    public SocketTextChannel Channel;
    public ulong OldestMessageId;
    public DateTime NextRetryAt;
    public int DelaySec;
    public const int MaxDelaySec = 1800;
}

[Service]
public class MessageArchiveService
{
    const string DB_PATH = "ArchiveData/messages.db";

    readonly DiscordSocketClient _discord;
    readonly LoggingService _log;
    readonly SemaphoreSlim _dbLock = new(1, 1);
    readonly List<RetryState> _retryQueue = new();
    volatile bool _backfillStarted;

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

        _log.Info($"Archive database ready at {DB_PATH}");
    }

    public void Start()
    {
        _discord.MessageReceived += OnMessageReceived;
        _discord.Ready += OnReady;
    }

    Task OnReady()
    {
        _discord.Ready -= OnReady;
        _ = Task.Run(BackfillAllChannelsAsync);
        return Task.CompletedTask;
    }

    async Task OnMessageReceived(SocketMessage socketMessage)
    {
        if (socketMessage is not SocketUserMessage userMessage) return;
        if (userMessage.Author.IsBot) return;
        if (!ShouldArchiveContent(userMessage.Content)) return;

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

        var guilds = _discord.Guilds.OrderBy(g => g.Id).ToList();
        foreach (var guild in guilds)
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

        if (_retryQueue.Count == 0)
        {
            _log.Info("Full backfill complete. All channels archived.");
        }
        else
        {
            _log.Info($"Initial pass done. {_retryQueue.Count} channels queued for ongoing deep archive.");
            _ = RunRetryLoopAsync();
        }
    }

    async Task RunRetryLoopAsync()
    {
        while (true)
        {
            List<RetryState> due;
            lock (_retryQueue)
            {
                due = _retryQueue.Where(r => DateTime.UtcNow >= r.NextRetryAt).ToList();
            }

            foreach (var state in due)
            {
                try
                {
                    var result = await BackfillChannelAsync(state.Channel, state.OldestMessageId);
                    if (result == BackfillResult.NeedRetry)
                    {
                        state.DelaySec = Math.Min(state.DelaySec * 2, RetryState.MaxDelaySec);
                        state.NextRetryAt = DateTime.UtcNow.AddSeconds(state.DelaySec);
                    }
                    else if (result == BackfillResult.Complete)
                    {
                        lock (_retryQueue) _retryQueue.Remove(state);
                        _log.Info($"Deep archive complete for #{state.Channel.Name}.");
                    }
                }
                catch (Exception e)
                {
                    state.DelaySec = Math.Min(state.DelaySec * 2, RetryState.MaxDelaySec);
                    state.NextRetryAt = DateTime.UtcNow.AddSeconds(state.DelaySec);
                    _log.Warning($"Retry failed #{state.Channel.Name}: {e.Message}. Next in {state.DelaySec}s.");
                }
            }

            await Task.Delay(5000);
        }
    }

    async Task<BackfillResult> BackfillChannelAsync(SocketTextChannel channel, ulong? resumeFromId = null)
    {
        if (resumeFromId == null)
        {
            var firstPage = await channel.GetMessagesAsync(1).FlattenAsync();
            if (!firstPage.Any())
                return BackfillResult.Skipped;
            resumeFromId = firstPage.First().Id;
            _log.Info($"Backfilling #{channel.Name}...");
        }

        var totalSaved = 0;
        var oldestMessageId = resumeFromId;
        var fetchDelay = 5;

        while (true)
        {
            List<IMessage> messages;
            try
            {
                messages = (await channel.GetMessagesAsync(oldestMessageId.Value, Direction.Before, 100).FlattenAsync()).ToList();
            }
            catch (Exception e)
            {
                _log.Warning($"Fetch error #{channel.Name}: {e.Message}. Retry in {fetchDelay}s.");
                await Task.Delay(TimeSpan.FromSeconds(fetchDelay));
                fetchDelay = Math.Min(fetchDelay * 2, 60);
                continue;
            }

            if (messages.Count == 0)
            {
                QueueRetry(channel, oldestMessageId.Value);
                return BackfillResult.NeedRetry;
            }

            fetchDelay = 5;

            var newRecords = new List<MessageRecord>();

            foreach (var msg in messages)
            {
                if (msg is not IUserMessage userMsg) continue;
                if (userMsg.Author.IsBot) continue;
                if (!ShouldArchiveContent(userMsg.Content)) continue;

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

            if (messages.Count < 100)
            {
                QueueRetry(channel, oldestMessageId!.Value);
                return BackfillResult.NeedRetry;
            }

            await Task.Delay(200);
        }
    }

    void QueueRetry(SocketTextChannel channel, ulong oldestMessageId)
    {
        lock (_retryQueue)
        {
            var existing = _retryQueue.FirstOrDefault(r => r.Channel.Id == channel.Id);
            if (existing != null)
            {
                existing.OldestMessageId = oldestMessageId;
            }
            else
            {
                _retryQueue.Add(new RetryState
                {
                    Channel = channel,
                    OldestMessageId = oldestMessageId,
                    NextRetryAt = DateTime.UtcNow.AddSeconds(60),
                    DelaySec = 60
                });
            }
        }
    }

    ArchiveDbContext CreateContext()
    {
        return new ArchiveDbContext(
            new DbContextOptionsBuilder<ArchiveDbContext>()
                .UseSqlite($"Data Source={DB_PATH}")
                .Options
        );
    }

    public async Task<ArchiveStats> GetArchiveStatsAsync()
    {
        using var ctx = CreateContext();

        var totalMessages = await ctx.Messages.LongCountAsync();
        var totalChannels = await ctx.Messages.Select(m => m.ChannelId).Distinct().CountAsync();
        var totalUsers = await ctx.Messages.Select(m => m.AuthorId).Distinct().CountAsync();
        var oldest = await ctx.Messages.OrderBy(m => m.Timestamp).FirstOrDefaultAsync();
        var newest = await ctx.Messages.OrderByDescending(m => m.Timestamp).FirstOrDefaultAsync();

        long dbSize = 0;
        try
        {
            var fi = new FileInfo(DB_PATH);
            if (fi.Exists) dbSize = fi.Length;
        }
        catch { }

        return new ArchiveStats
        {
            TotalMessages = totalMessages,
            TotalChannels = totalChannels,
            TotalUsers = totalUsers,
            OldestMessage = oldest?.Timestamp,
            NewestMessage = newest?.Timestamp,
            IsBackfilling = _backfillStarted,
            DbSizeBytes = dbSize
        };
    }

    public async Task<UserStats?> GetUserStatsAsync(ulong userId)
    {
        using var ctx = CreateContext();

        var msgs = ctx.Messages.Where(m => m.AuthorId == userId);

        var count = await msgs.LongCountAsync();
        if (count == 0) return null;

        var first = await msgs.OrderBy(m => m.Timestamp).FirstAsync();
        var last = await msgs.OrderByDescending(m => m.Timestamp).FirstAsync();
        var channels = await msgs.Select(m => m.ChannelId).Distinct().CountAsync();

        return new UserStats
        {
            UserId = userId,
            UserName = first.AuthorName,
            MessageCount = count,
            FirstMessage = first.Timestamp,
            LastMessage = last.Timestamp,
            ChannelCount = channels
        };
    }

    public async Task<int> PurgeBadEntriesAsync()
    {
        await _dbLock.WaitAsync();
        try
        {
            using var ctx = CreateContext();
            var bad = await ctx.Messages
                .Where(m => m.Content.Length <= 2 || !char.IsLetter(m.Content[0]))
                .ToListAsync();
            var count = bad.Count;
            if (count > 0)
            {
                ctx.Messages.RemoveRange(bad);
                await ctx.SaveChangesAsync();
            }
            return count;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    static bool ShouldArchiveContent(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        if (content.Length <= 2) return false;
        if (!char.IsLetter(content[0])) return false;
        return true;
    }
}
