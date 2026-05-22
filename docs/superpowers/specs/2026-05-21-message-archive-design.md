# Message Archive System — Design Spec

## Overview

Add an automatic message archiving system to Utsuki Bot that saves all messages from all text channels to a SQLite database for future analytics and per-user statistics on a 10-year-old Discord server.

## Motivation

The server has ~10 years of history. The bot runs 24/7 on a single production guild (plus one test guild). We want to collect every message into a queryable store so we can later run statistics per user (message count, activity patterns, word usage, etc.).

## Design Decisions

### Storage: SQLite via EF Core

- EF Core 9.0.1 already referenced in the project (unused)
- Add `Microsoft.EntityFrameworkCore.Sqlite` package
- Database file: `../JsonData/Archive/messages.db` — sits inside the existing Docker volume, no extra mount needed
- SQLite is zero-admin, single-file, perfect for a single-server bot

### Initialization: Automatic on boot

No slash commands to start/stop. The `MessageArchiveService` (registered as an activated singleton via `[Service]` attribute) starts on bot launch and:

1. Ensures the SQLite database and schema exist (EF Core `EnsureCreated`)
2. Subscribes to `DiscordSocketClient.MessageReceived` for real-time capture
3. Spawns a background backfill task for all accessible `SocketTextChannel`s

### Rate Limiting

- Discord.Net's `GetMessagesAsync()` handles rate limits internally (429 retry with `Retry-After`)
- Conservative 200ms delay between each 100-message page (~5 req/s, well below Discord's 50 req/s per-route limit)
- Bot continues normal operation during backfill (fire-and-forget background task with error isolation per channel)

### Backfill Strategy

- On first boot (or when `messages.db` is empty), iterate every `SocketTextChannel` in every connected guild
- For each channel, paginate backwards via `GetMessagesAsync` from the newest message to the oldest available (Discord API limit varies per channel)
- Insert batches of 100 messages into SQLite, commit after each batch
- Skip messages already in the database (check by `MessageId`)
- Log progress via `LoggingService` every 500 messages
- On subsequent boots, only capture new messages in real-time (no re-scan)

### Real-time Capture

- `MessageReceived` handler saves every `SocketUserMessage` to SQLite
- Author is bot → skip
- Message already exists (by `MessageId`) → skip
- Non-user messages → skip
- Attachments stored as semicolon-separated URLs

## Schema

```sql
CREATE TABLE MessageRecords (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    MessageId   INTEGER NOT NULL,       -- Discord snowflake
    ChannelId   INTEGER NOT NULL,
    GuildId     INTEGER NOT NULL,
    AuthorId    INTEGER NOT NULL,
    AuthorName  TEXT NOT NULL,
    Content     TEXT NOT NULL,
    Timestamp   TEXT NOT NULL,           -- ISO 8601
    Attachments TEXT NULL                -- semicolon-separated URLs
);

CREATE INDEX IX_MessageRecords_AuthorId ON MessageRecords(AuthorId);
CREATE INDEX IX_MessageRecords_ChannelId ON MessageRecords(ChannelId);
CREATE INDEX IX_MessageRecords_GuildId ON MessageRecords(GuildId);
CREATE UNIQUE INDEX IX_MessageRecords_MessageId ON MessageRecords(MessageId);
```

## Files

### New files

| File | Purpose |
|---|---|
| `src/Models/MessageRecord.cs` | EF Core entity |
| `src/Models/ArchiveDbContext.cs` | DbContext, connection string, index setup |
| `src/Services/MessageArchiveService.cs` | Backfill + real-time capture |

### Modified files

| File | Change |
|---|---|
| `utsuki-bot.csproj` | Add `Microsoft.EntityFrameworkCore.Sqlite` package reference |

### Not modified

- `Program.cs` — new service is auto-discovered via `AddAnnotatedServices` + `[Service]` attribute
- `docker-compose.yml` — database lives inside existing `JsonData` volume
- No new slash commands needed

## Error Handling

- Per-channel backfill errors are caught and logged individually — one failing channel doesn't stop others
- Real-time capture errors are caught and logged — never crash the bot over a single message
- Database write errors are non-fatal; missed messages are acceptable for this analytics use case

## Future Queries Enabled

Once data is collected, users can run ad-hoc statistics:

- Total messages per user per channel/per guild
- Messages per day/hour by user
- Most active users
- Word frequency per user
- Activity heatmaps over the server's lifetime
- etc.

## Out of Scope

- Slash commands to query statistics (future feature)
- Message edit/delete tracking
- Voice channel logging
- Database cleanup/rotation
