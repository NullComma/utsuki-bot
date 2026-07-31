using App.Extensions;
using App.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System.Text.RegularExpressions;

namespace App.Modules;

[CommandContextType(InteractionContextType.Guild)]
public class MemoryModule : InteractionModuleBase<SocketInteractionContext>
{
    readonly MemoryService _memory;
    readonly GuildSettingsService _guildSettings;

    public MemoryModule(MemoryService memory, GuildSettingsService guildSettings)
    {
        _memory = memory;
        _guildSettings = guildSettings;
    }

    [SlashCommand("anteriormente", "Resumo do que rolou no servidor (últimos 15 dias)")]
    public async Task Anteriormente()
    {
        await DeferAsync();
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var omit = BuildOmitChannels(guildId);
        var result = await _memory.GenerateWeeklyAsync(guildId, visibleChannelIds: GetEveryoneVisibleChannels(), omitChannelIds: omit);
        var title = $"Anteriormente em {Context.Guild.Name}";

        var embed = BuildEmbed(title, result.Summary, Color.Blue);
        embed.Footer = new EmbedFooterBuilder { Text = $"Resumo dos assuntos desde {result.SummarySince:dd/MM/yyyy}" };
        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("resumomes", "Resumo das conversas de um mês específico (formato MM/AAAA)")]
    public async Task ResumoMes([Summary("mes", "Mês no formato MM/AAAA (ex.: 07/2026). Padrão: mês atual.")] string mes = "")
    {
        await DeferAsync();
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var now = DateTime.UtcNow;
        int month = now.Month, year = now.Year;

        if (!string.IsNullOrWhiteSpace(mes))
        {
            var match = Regex.Match(mes.Trim(), @"^(0[1-9]|1[0-2])/(\d{4})$");
            if (!match.Success)
            {
                await FollowupAsync("Formato inválido. Use MM/AAAA (ex.: 07/2026).", ephemeral: true);
                return;
            }
            month = int.Parse(match.Groups[1].Value);
            year = int.Parse(match.Groups[2].Value);
        }

        var omit = BuildOmitChannels(guildId);
        var result = await _memory.GenerateMonthAsync(guildId, month, year, visibleChannelIds: GetEveryoneVisibleChannels(), omitChannelIds: omit);
        var monthName = new DateTime(year, month, 1).ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        var title = $"Resumo de {monthName} de {year}";

        var embed = BuildEmbed(title, result.Summary, GetChannelColor(Context.Channel.Id));
        if (result.SummarySince.HasValue)
            embed.Footer = new EmbedFooterBuilder { Text = $"Resumo dos assuntos desde {result.SummarySince:dd/MM/yyyy}" };
        await FollowupAsync(embed: embed.Build());
    }

    static readonly Color[] _palette =
    {
        Color.Blue, Color.Gold, Color.Green, Color.Purple, Color.Red,
        Color.Orange, Color.Magenta, Color.Teal, Color.DarkBlue, Color.DarkGreen
    };

    static Color GetChannelColor(ulong channelId) => _palette[(int)(channelId % (ulong)_palette.Length)];

    [SlashCommand("recap", "Resumo do que você perdeu desde sua última mensagem")]
    public async Task Recap()
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var omit = BuildOmitChannels(guildId);
        var result = await _memory.GenerateRecapAsync(guildId, Context.User.Id, visibleChannelIds: GetEveryoneVisibleChannels(), omitChannelIds: omit);
        var embed = BuildEmbed("Você Perdeu", DeLinkify(result.Summary), Color.Purple);
        if (result.SummarySince.HasValue)
            embed.Footer = new EmbedFooterBuilder { Text = $"Resumo dos assuntos desde {result.SummarySince:dd/MM/yyyy HH:mm}" };
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    HashSet<ulong>? GetEveryoneVisibleChannels()
    {
        if (Context.Guild == null) return null;

        var everyoneRole = Context.Guild.EveryoneRole;
        var visible = new HashSet<ulong>();
        foreach (var channel in Context.Guild.Channels)
        {
            if (channel is not SocketTextChannel textChannel) continue;
            if (CanEveryoneView(textChannel, everyoneRole))
                visible.Add(channel.Id);
        }
        return visible;
    }

    static bool CanEveryoneView(SocketTextChannel channel, SocketRole everyoneRole)
    {
        var overwrite = channel.GetPermissionOverwrite(everyoneRole);
        if (overwrite.HasValue && overwrite.Value.ViewChannel == PermValue.Deny)
            return false;
        if (channel.Category != null)
        {
            var categoryOverwrite = channel.Category.GetPermissionOverwrite(everyoneRole);
            if (categoryOverwrite.HasValue && categoryOverwrite.Value.ViewChannel == PermValue.Deny)
                return false;
        }
        return true;
    }

    HashSet<ulong> BuildOmitChannels(ulong guildId)
    {
        var omit = new HashSet<ulong> { Context.Channel.Id };
        var settings = _guildSettings.GetGuildSettings(guildId);
        if (settings.MainTextChannelId.HasValue)
            omit.Add(settings.MainTextChannelId.Value);
        return omit;
    }

    readonly Dictionary<ulong, string> _nameCache = new();

    string DeLinkify(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary) || Context.Guild == null) return summary;

        summary = Regex.Replace(summary, @"<@(\d+)>", m =>
        {
            var id = ulong.Parse(m.Groups[1].Value);
            if (_nameCache.TryGetValue(id, out var cached)) return cached;
            var user = Context.Guild.GetUser(id);
            var name = user != null ? user.GetNameSafe() : null;
            _nameCache[id] = name ?? m.Value;
            return _nameCache[id];
        });
        summary = Regex.Replace(summary, @"<#(\d+)>", m =>
        {
            var id = ulong.Parse(m.Groups[1].Value);
            if (_nameCache.TryGetValue(id, out var cached)) return cached;
            var channel = Context.Guild.GetChannel(id);
            var name = channel != null ? $"#{channel.Name}" : null;
            _nameCache[id] = name ?? m.Value;
            return _nameCache[id];
        });
        return summary;
    }

    static EmbedBuilder BuildEmbed(string title, string description, Color color) => new()
    {
        Title = title,
        Description = description,
        Color = color,
        Footer = new EmbedFooterBuilder { Text = "Resumo por IA" }
    };

    static bool IsPlaceholder(string summary) =>
        summary.StartsWith("Nenhuma") || summary.StartsWith("Erro");
}
