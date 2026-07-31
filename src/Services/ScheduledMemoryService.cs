using App.Attributes;
using App.Models;
using Discord;
using Discord.WebSocket;
using Timer = System.Timers.Timer;

namespace App.Services;

[Service]
public class ScheduledMemoryService
{
    const int WeeklyIntervalDays = 15;
    const int LastMessagesCheck = 3;

    readonly DiscordSocketClient _discord;
    readonly GuildSettingsService _guildSettings;
    readonly MemoryService _memory;
    readonly LoggingService _log;
    readonly Timer _timer;

    public ScheduledMemoryService(DiscordSocketClient discord, GuildSettingsService guildSettings, MemoryService memory, LoggingService log)
    {
        _discord = discord;
        _guildSettings = guildSettings;
        _memory = memory;
        _log = log;

        _timer = new Timer(TimeSpan.FromHours(1).TotalMilliseconds);
        _timer.Elapsed += async (_, _) => await CheckAllGuildsAsync();
        _timer.Start();

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            await CheckAllGuildsAsync();
        });
    }

    async Task CheckAllGuildsAsync()
    {
        try
        {
            foreach (var guild in _discord.Guilds)
                await CheckGuildAsync(guild);
        }
        catch (Exception e)
        {
            _log.Error($"ScheduledMemoryService failed: {e}");
        }
    }

    async Task CheckGuildAsync(SocketGuild guild)
    {
        var settings = _guildSettings.GetGuildSettings(guild.Id);
        if (settings.MainTextChannelId == null) return;

        var channel = guild.GetTextChannel(settings.MainTextChannelId.Value);
        if (channel == null)
        {
            _log.Warning($"Main channel {settings.MainTextChannelId} not found in {guild.Name}.");
            return;
        }

        var now = DateTime.UtcNow;

        var lastWeekly = await _memory.GetLastAutoPostAsync(guild.Id, "weekly");
        if ((lastWeekly == null || now - lastWeekly.Value >= TimeSpan.FromDays(WeeklyIntervalDays)) &&
            !await IsLastMessagesFromBotAsync(channel))
        {
            _log.Info($"Posting biweekly summary in #{channel.Name} ({guild.Name})");
            var result = await _memory.GenerateWeeklyAsync(guild.Id);
            if (!IsPlaceholder(result.Summary))
            {
                var embed = BuildEmbed($"Anteriormente em {guild.Name}", result.Summary, Color.Blue);
                var msg = await channel.SendMessageAsync(embed: embed.Build());
                _memory.RegisterPublicMessage(guild.Id, "weekly", channel.Id, msg.Id);
                await _memory.RecordAutoPostAsync(guild.Id, "weekly", now);
            }
        }
    }

    async Task<bool> IsLastMessagesFromBotAsync(SocketTextChannel channel)
    {
        try
        {
            var messages = (await channel.GetMessagesAsync(LastMessagesCheck).FlattenAsync()).ToList();
            return messages.Count > 0 && messages.All(m => m.Author.IsBot);
        }
        catch (Exception e)
        {
            _log.Warning($"Could not fetch last messages of #{channel.Name}: {e.Message}");
            return true;
        }
    }

    static EmbedBuilder BuildEmbed(string title, string description, Color color) => new()
    {
        Title = title,
        Description = description,
        Color = color,
        Footer = new EmbedFooterBuilder { Text = "Gerado por IA" }
    };

    static bool IsPlaceholder(string summary) =>
        string.IsNullOrWhiteSpace(summary) || summary.StartsWith("Nenhuma") || summary.StartsWith("Erro");
}
