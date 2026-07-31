using App.Attributes;
using App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace App.Services;

public class MemoryResult
{
    public string Summary { get; set; } = string.Empty;
    public ulong? PublicMessageChannelId { get; set; }
    public ulong? PublicMessageId { get; set; }
}

[Service]
public class MemoryService
{
    record CacheEntry(string Summary, string CacheDate, ulong? PublicChannelId, ulong? PublicMessageId);

    static readonly ConcurrentDictionary<(ulong GuildId, string PeriodType), CacheEntry> _cache = new();

    readonly LoggingService _log;
    readonly BotSettings _settings;
    readonly IHttpClientFactory _httpClientFactory;
    readonly MessageArchiveService _archive;

    public MemoryService(LoggingService log, IOptionsSnapshot<BotSettings> settings, IHttpClientFactory httpClientFactory, MessageArchiveService archive)
    {
        _log = log;
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _archive = archive;
    }

    public async Task<MemoryResult> GenerateWeeklyAsync(ulong guildId)
    {
        var cached = GetCached(guildId, "weekly");
        if (cached != null)
            return ToResult(cached);

        var since = DateTime.UtcNow.AddDays(-14);
        var messages = await QueryMessagesAsync(guildId, since);
        if (messages.Count == 0) return new MemoryResult { Summary = "Nenhuma mensagem arquivada na quinzena." };

        var content = FormatMessages(messages);
        var systemPrompt = $"Resuma as conversas dos últimos 14 dias (de {since:dd/MM/yyyy} até {DateTime.UtcNow:dd/MM/yyyy}) no servidor Discord em português. Destaque tópicos principais, decisões, discussões importantes. Seja conciso (máx 500 palavras).";
        var summary = await CallAIAsync(systemPrompt, content);
        summary = AddUserMentions(summary, messages);
        SaveCache(guildId, "weekly", summary);
        return new MemoryResult { Summary = summary };
    }

    public async Task<MemoryResult> GenerateMonthlyAsync(ulong guildId)
    {
        var cached = GetCached(guildId, "monthly");
        if (cached != null)
            return ToResult(cached);

        var since = DateTime.UtcNow.AddDays(-30);
        var messages = await QueryMessagesAsync(guildId, since);
        if (messages.Count == 0) return new MemoryResult { Summary = "Nenhuma mensagem arquivada este mês." };

        var content = FormatMessages(messages);
        var systemPrompt = $"Resuma as conversas do mês (últimos 30 dias, de {since:dd/MM/yyyy} até {DateTime.UtcNow:dd/MM/yyyy}) no servidor Discord em português. Destaque tópicos principais, decisões, discussões importantes. Seja conciso (máx 500 palavras).";
        var summary = await CallAIAsync(systemPrompt, content);
        summary = AddUserMentions(summary, messages);
        SaveCache(guildId, "monthly", summary);
        return new MemoryResult { Summary = summary };
    }

    public async Task<MemoryResult> GenerateRecapAsync(ulong guildId, ulong userId)
    {
        var lastMsg = await _archive.GetLastUserMessageAsync(guildId, userId);
        var since = lastMsg ?? DateTime.UtcNow.AddDays(-7);
        var maxAge = DateTime.UtcNow.AddDays(-14);
        if (since < maxAge) since = maxAge;

        var messages = await QueryMessagesAsync(guildId, since, excludeUserId: userId);
        if (messages.Count == 0) return new MemoryResult { Summary = "Nenhuma mensagem nova desde sua última mensagem." };

        var content = FormatMessages(messages);
        var systemPrompt = $"Resuma as conversas que o usuário perdeu desde {since:dd/MM/yyyy HH:mm} no servidor Discord em português. Destaque tópicos principais, discussões. Seja conciso (máx 400 palavras).";
        var summary = await CallAIAsync(systemPrompt, content);
        return new MemoryResult { Summary = AddUserMentions(summary, messages) };
    }

    public void RegisterPublicMessage(ulong guildId, string periodType, ulong channelId, ulong messageId)
    {
        if (_cache.TryGetValue((guildId, periodType), out var entry))
            _cache[(guildId, periodType)] = entry with { PublicChannelId = channelId, PublicMessageId = messageId };
    }

    public async Task<DateTime?> GetLastAutoPostAsync(ulong guildId, string postType)
    {
        using var ctx = CreateContext();
        var post = await ctx.MemoryPosts
            .FirstOrDefaultAsync(p => p.GuildId == guildId && p.PostType == postType);
        return post?.LastPostedAt;
    }

    public async Task RecordAutoPostAsync(ulong guildId, string postType, DateTime when)
    {
        using var ctx = CreateContext();
        var post = await ctx.MemoryPosts
            .FirstOrDefaultAsync(p => p.GuildId == guildId && p.PostType == postType);
        if (post == null)
        {
            ctx.MemoryPosts.Add(new MemoryPost { GuildId = guildId, PostType = postType, LastPostedAt = when });
        }
        else
        {
            post.LastPostedAt = when;
        }
        await ctx.SaveChangesAsync();
    }

    static MemoryResult ToResult(CacheEntry entry) => new()
    {
        Summary = entry.Summary,
        PublicMessageChannelId = entry.PublicChannelId,
        PublicMessageId = entry.PublicMessageId
    };

    static CacheEntry? GetCached(ulong guildId, string periodType)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (_cache.TryGetValue((guildId, periodType), out var entry) && entry.CacheDate == today)
            return entry;
        return null;
    }

    static void SaveCache(ulong guildId, string periodType, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.StartsWith("Erro")) return;
        _cache[(guildId, periodType)] = new CacheEntry(summary, DateTime.UtcNow.ToString("yyyy-MM-dd"), null, null);
    }

    async Task<List<MessageRecord>> QueryMessagesAsync(ulong guildId, DateTime since, ulong? excludeUserId = null)
    {
        using var ctx = CreateContext();
        var query = ctx.Messages
            .Where(m => m.GuildId == guildId && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp);

        if (excludeUserId.HasValue)
            query = (IOrderedQueryable<MessageRecord>)query.Where(m => m.AuthorId != excludeUserId.Value);

        return await query.Take(500).ToListAsync();
    }

    static string FormatMessages(List<MessageRecord> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Histórico de mensagens:\n");
        foreach (var m in messages)
        {
            var ch = !string.IsNullOrEmpty(m.ChannelName) ? $"#{m.ChannelName}" : $"ch:{m.ChannelId}";
            sb.AppendLine($"[{m.Timestamp:yyyy-MM-dd HH:mm}] <{m.AuthorName}> em {ch}: {m.Content}");
        }
        return sb.ToString();
    }

    static string AddUserMentions(string summary, IEnumerable<MessageRecord> messages)
    {
        if (string.IsNullOrWhiteSpace(summary)) return summary;

        var authors = messages
            .Where(m => !string.IsNullOrWhiteSpace(m.AuthorName))
            .GroupBy(m => m.AuthorId)
            .Select(g => new { Name = g.First().AuthorName!, Id = g.Key })
            .Where(a => a.Name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-'))
            .OrderByDescending(a => a.Name.Length)
            .ToList();

        foreach (var author in authors)
        {
            summary = Regex.Replace(
                summary,
                $@"\b{Regex.Escape(author.Name)}\b",
                $"<@{author.Id}>",
                RegexOptions.IgnoreCase
            );
        }

        if (summary.Length > 4000)
            summary = summary[..3997] + "...";
        return summary;
    }

    async Task<string> CallAIAsync(string systemPrompt, string userContent)
    {
        var endpoint = _settings.AiEndpoint;
        if (string.IsNullOrEmpty(endpoint))
        {
            _log.Error("No AI endpoint configured for MemoryService.");
            return "IA não configurada.";
        }

        var payload = new
        {
            stream = false,
            model = _settings.AiModel,
            temperature = 0.5,
            max_tokens = 1024,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        };

        var httpClient = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/v1/chat/completions");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(_settings.AiToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.AiToken);

        try
        {
            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _log.Error($"Memory AI request failed: {response.StatusCode}");
                return "Erro ao gerar resumo.";
            }
            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex)
        {
            _log.Error($"Memory AI call exception: {ex.Message}");
            return "Erro ao gerar resumo.";
        }
    }

    ArchiveDbContext CreateContext()
    {
        return new ArchiveDbContext(
            new DbContextOptionsBuilder<ArchiveDbContext>()
                .UseSqlite("Data Source=db/messages.db")
                .Options
        );
    }
}
