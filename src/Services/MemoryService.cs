using App.Attributes;
using App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace App.Services;

public class MemoryResult
{
    public string Summary { get; set; } = string.Empty;
}

[Service]
public class MemoryService
{
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

    const int WeeklyLookbackDays = 15;
    const int MinMessagesForSummary = 100;

    public async Task<MemoryResult> GenerateWeeklyAsync(ulong guildId)
    {
        var since = DateTime.UtcNow.AddDays(-WeeklyLookbackDays);
        var messages = await QueryMessagesAsync(guildId, since);
        if (messages.Count < MinMessagesForSummary)
            messages = await QueryMessagesAsync(guildId, null, take: MinMessagesForSummary);
        if (messages.Count == 0) return new MemoryResult { Summary = "Nenhuma mensagem arquivada." };

        var content = FormatMessages(messages);
        var range = messages.Count >= MinMessagesForSummary
            ? $"últimos {WeeklyLookbackDays} dias, de {messages.Min(m => m.Timestamp):dd/MM/yyyy} até {messages.Max(m => m.Timestamp):dd/MM/yyyy}"
            : $"as últimas {messages.Count} mensagens, de {messages.Min(m => m.Timestamp):dd/MM/yyyy} até {messages.Max(m => m.Timestamp):dd/MM/yyyy}";
        var systemPrompt = BuildSummaryPrompt(range, 200, 8);
        var summary = await CallAIAsync(systemPrompt, content);
        summary = Linkify(summary, messages);
        return new MemoryResult { Summary = summary };
    }

    public async Task<MemoryResult> GenerateMonthAsync(ulong guildId, int month, int year)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var messages = await QueryMessagesAsync(guildId, start, end);
        if (messages.Count == 0) return new MemoryResult { Summary = "Nenhuma mensagem arquivada neste mês." };

        var content = FormatMessages(messages);
        var monthName = start.ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        var systemPrompt = BuildSummaryPrompt($"{monthName} de {year} ({start:dd/MM/yyyy} até {end.AddDays(-1):dd/MM/yyyy})", 250, 10);
        var summary = await CallAIAsync(systemPrompt, content);
        summary = Linkify(summary, messages);
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
        var systemPrompt = BuildSummaryPrompt($"desde {since:dd/MM/yyyy HH:mm}", 150, 6);
        var summary = await CallAIAsync(systemPrompt, content);
        return new MemoryResult { Summary = Linkify(summary, messages) };
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

    async Task<List<MessageRecord>> QueryMessagesAsync(ulong guildId, DateTime? since = null, DateTime? until = null, int take = 500, ulong? excludeUserId = null)
    {
        using var ctx = CreateContext();
        var query = ctx.Messages
            .Where(m => m.GuildId == guildId);

        if (since.HasValue)
            query = query.Where(m => m.Timestamp >= since.Value);
        if (until.HasValue)
            query = query.Where(m => m.Timestamp < until.Value);
        if (excludeUserId.HasValue)
            query = query.Where(m => m.AuthorId != excludeUserId.Value);

        var result = await query
            .OrderByDescending(m => m.Timestamp)
            .Take(take)
            .ToListAsync();
        result.Reverse();
        return result;
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

    static string Linkify(string summary, IEnumerable<MessageRecord> messages)
    {
        if (string.IsNullOrWhiteSpace(summary)) return summary;

        summary = NormalizeInvisible(summary);

        var channels = messages
            .Where(m => m.ChannelId != 0)
            .GroupBy(m => m.ChannelId)
            .Select(g => new { Name = g.First().ChannelName, Id = g.Key })
            .OrderByDescending(c => c.Name?.Length ?? 0)
            .ToList();

        foreach (var channel in channels)
        {
            summary = Regex.Replace(summary, $@"(?<![A-Za-z0-9_])ch:{channel.Id}(?!\d)", $"<#{channel.Id}>");
            summary = Regex.Replace(summary, $@"(?<![\d<])#{channel.Id}(?!\d)", $"<#{channel.Id}>");
        }

        summary = AddUserMentions(summary, messages);
        summary = AddChannelMentions(summary, messages);

        if (summary.Length > 4000)
            summary = summary[..3997] + "...";
        return summary;
    }

    static string NormalizeInvisible(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFE0F' or '\uFEFF' or '\u00AD')
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static string AddUserMentions(string summary, IEnumerable<MessageRecord> messages)
    {
        if (string.IsNullOrWhiteSpace(summary)) return summary;

        var authors = messages
            .Where(m => !string.IsNullOrWhiteSpace(m.AuthorName))
            .GroupBy(m => m.AuthorId)
            .Select(g => new { Name = NormalizeInvisible(g.First().AuthorName!), Id = g.Key })
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && a.Name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-'))
            .OrderByDescending(a => a.Name.Length)
            .ToList();

        foreach (var author in authors)
        {
            summary = Regex.Replace(
                summary,
                $@"(?<![\p{{L}}\p{{Nd}}_])@{Regex.Escape(author.Name)}(?![\p{{L}}\p{{Nd}}@#_])",
                $"<@{author.Id}>",
                RegexOptions.IgnoreCase
            );

            summary = Regex.Replace(
                summary,
                $@"(?<![\p{{L}}\p{{Nd}}@]){Regex.Escape(author.Name)}(?![\p{{L}}\p{{Nd}}@#_])",
                $"<@{author.Id}>",
                RegexOptions.IgnoreCase
            );
        }

        return summary;
    }

    static string AddChannelMentions(string summary, IEnumerable<MessageRecord> messages)
    {
        if (string.IsNullOrWhiteSpace(summary)) return summary;

        var channels = messages
            .Where(m => !string.IsNullOrWhiteSpace(m.ChannelName))
            .GroupBy(m => m.ChannelId)
            .Select(g => new { Name = NormalizeInvisible(g.First().ChannelName!), Id = g.Key })
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .OrderByDescending(c => c.Name.Length)
            .ToList();

        foreach (var channel in channels)
        {
            summary = Regex.Replace(
                summary,
                $@"(?<![\p{{L}}\p{{Nd}}_])#{Regex.Escape(channel.Name)}(?![\p{{L}}\p{{Nd}}@#_])",
                $"<#{channel.Id}>",
                RegexOptions.IgnoreCase
            );

            summary = Regex.Replace(
                summary,
                $@"(?<![\p{{L}}\p{{Nd}}#]){Regex.Escape(channel.Name)}(?![\p{{L}}\p{{Nd}}@#_])",
                $"<#{channel.Id}>",
                RegexOptions.IgnoreCase
            );
        }

        return summary;
    }

    static string BuildSummaryPrompt(string range, int maxWords, int maxBullets)
    {
        return $"Resuma as conversas do servidor Discord referentes a {range}, em português. " +
               $"Formato: apenas bullets curtos e diretos, no estilo recapitulação de série, sem cabeçalhos, sem subtítulos, sem listas aninhadas. " +
               $"Regras: máximo {maxBullets} bullets e {maxWords} palavras no total; frases curtas e objetivas; " +
               $"sem floreios, sem adjetivos, sem emojis, sem markdown além dos bullets. " +
               $"Cite sempre os nomes de usuários exatamente como aparecem no histórico (ex.: Fulano, Beltrano) " +
               $"e os canais como aparecem (ex.: geral, off-topic), para que os links de menção funcionem.";
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
