namespace App.Models;

public class BotSettings
{
    public string DiscordToken { get; set; } = string.Empty;
    public string AiToken { get; set; } = string.Empty;
    public string AiEndpoint { get; set; } = string.Empty;
    public string AiModel { get; set; } = string.Empty;
    public string WeatherApiKey { get; set; } = string.Empty;
    public ulong MainGuildId { get; set; }
    public ulong GgjGuildId { get; set; }
    public List<ulong> ArchiveGuildIds { get; set; } = new();
}
