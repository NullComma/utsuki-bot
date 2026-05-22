# Message Archive System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add automatic message archiving to SQLite for all text channels, with full backfill on first boot and real-time capture thereafter.

**Architecture:** A `MessageArchiveService` (activated singleton, auto-discovered) subscribes to `MessageReceived`, saves every user message to a SQLite DB via EF Core. On first boot, it backfills all accessible text channels with conservative delays to respect API limits.

**Tech Stack:** .NET 8, Discord.Net 3.18.0, EF Core 9.0.1 + SQLite

---

### Task 1: Add SQLite Package

**Files:**
- Modify: `utsuki-bot.csproj`

- [ ] **Step 1: Add package reference**

Edit `utsuki-bot.csproj` to add the SQLite provider:

After line 15 (`Discord.Net` reference), add:
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.1" />
```

- [ ] **Step 2: Restore and verify**

Run: `dotnet restore`
Expected: No errors. SQLite package resolved.

- [ ] **Step 3: Commit**

```bash
git add utsuki-bot.csproj
git commit -m "chore: add Microsoft.EntityFrameworkCore.Sqlite"
```

---

### Task 2: Create MessageRecord Entity

**Files:**
- Create: `src/Models/MessageRecord.cs`

- [ ] **Step 1: Write the entity class**

Create `src/Models/MessageRecord.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace App.Models;

public class MessageRecord
{
    [Key]
    public int Id { get; set; }

    public ulong MessageId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong GuildId { get; set; }
    public ulong AuthorId { get; set; }

    public string AuthorName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? Attachments { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build --no-restore`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add src/Models/MessageRecord.cs
git commit -m "feat: add MessageRecord entity"
```

---

### Task 3: Create ArchiveDbContext

**Files:**
- Create: `src/Models/ArchiveDbContext.cs`

- [ ] **Step 1: Write the DbContext**

Create `src/Models/ArchiveDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace App.Models;

public class ArchiveDbContext : DbContext
{
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();

    public ArchiveDbContext() { }

    public ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MessageRecord>(entity =>
        {
            entity.ToTable("Messages");

            entity.HasIndex(e => e.AuthorId);
            entity.HasIndex(e => e.ChannelId);
            entity.HasIndex(e => e.GuildId);
            entity.HasIndex(e => e.MessageId).IsUnique();
        });
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build --no-restore`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Models/ArchiveDbContext.cs
git commit -m "feat: add ArchiveDbContext"
```

---

### Task 4: Create MessageArchiveService

**Files:**
- Create: `src/Services/MessageArchiveService.cs`

- [ ] **Step 1: Write the service class**

Create `src/Services/MessageArchiveService.cs`:

```csharp
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
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build --no-restore`
Expected: Build succeeds.

- [ ] **Step 3: Wire up Start() call**

The `[Service]` attribute auto-registers `MessageArchiveService` as an activated singleton. We need `Start()` called after the client is ready. Edit `Program.cs` to hook it:

After line 78 (`await host.Services.GetRequiredService<InteractionHandler>().InitializeAsync();`), add:
```csharp
host.Services.GetRequiredService<MessageArchiveService>().Start();
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build --no-restore`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Services/MessageArchiveService.cs src/Program.cs
git commit -m "feat: add MessageArchiveService with backfill and real-time capture"
```

---

### Task 5: Build and Smoke Test

- [ ] **Step 1: Full build**

Run: `dotnet build`
Expected: Build succeeds with no errors or warnings.

- [ ] **Step 2: Review final diff**

Run: `git diff --stat`
Verify the expected files changed:
- `utsuki-bot.csproj` (package ref)
- `src/Models/MessageRecord.cs` (new)
- `src/Models/ArchiveDbContext.cs` (new)
- `src/Services/MessageArchiveService.cs` (new)
- `src/Program.cs` (3 lines added)
