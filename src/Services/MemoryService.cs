using App.Attributes;
using App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace App.Services;

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

    public async Task<string> GenerateWeeklyAsync(ulong guildId)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        var messages = await QueryMessagesAsync(guildId, since);
        if (messages.Count == 0) return "Nenhuma mensagem arquivada esta semana.";

        var content = FormatMessages(messages);
        var systemPrompt = $"Resuma as conversas da semana (últimos 7 dias, de {since:dd/MM/yyyy} até {DateTime.UtcNow:dd/MM/yyyy}) no servidor Discord em português. Destaque tópicos principais, decisões, discussões importantes. Seja conciso (máx 500 palavras).";
        return await CallAIAsync(systemPrompt, content);
    }

    public async Task<string> GenerateMonthlyAsync(ulong guildId)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var messages = await QueryMessagesAsync(guildId, since);
        if (messages.Count == 0) return "Nenhuma mensagem arquivada este mês.";

        var content = FormatMessages(messages);
        var systemPrompt = $"Resuma as conversas do mês (últimos 30 dias, de {since:dd/MM/yyyy} até {DateTime.UtcNow:dd/MM/yyyy}) no servidor Discord em português. Destaque tópicos principais, decisões, discussões importantes. Seja conciso (máx 500 palavras).";
        return await CallAIAsync(systemPrompt, content);
    }

    public async Task<string> GenerateRecapAsync(ulong guildId, ulong userId)
    {
        var lastMsg = await _archive.GetLastUserMessageAsync(guildId, userId);
        var since = lastMsg ?? DateTime.UtcNow.AddDays(-7);
        var maxAge = DateTime.UtcNow.AddDays(-14);
        if (since < maxAge) since = maxAge;

        var messages = await QueryMessagesAsync(guildId, since, excludeUserId: userId);
        if (messages.Count == 0) return "Nenhuma mensagem nova desde sua última mensagem.";

        var content = FormatMessages(messages);
        var systemPrompt = $"Resuma as conversas que o usuário perdeu desde {since:dd/MM/yyyy HH:mm} no servidor Discord em português. Destaque tópicos principais, discussões. Seja conciso (máx 400 palavras).";
        return await CallAIAsync(systemPrompt, content);
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
