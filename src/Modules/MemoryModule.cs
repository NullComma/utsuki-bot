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

    public MemoryModule(MemoryService memory)
    {
        _memory = memory;
    }

    [SlashCommand("anteriormente", "Resumo do que rolou no servidor (últimos 15 dias)")]
    public async Task Anteriormente([Summary("publico", "Postar no chat para todos verem?")] bool publico = false)
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var result = await _memory.GenerateWeeklyAsync(guildId);
        var title = $"Anteriormente em {Context.Guild.Name}";
        var embed = BuildEmbed(title, result.Summary, Color.Blue);

        if (publico && !IsPlaceholder(result.Summary))
            await FollowupAsync(embed: embed.Build(), ephemeral: false);
        else
        {
            var embedPrivate = BuildEmbed(title, DeLinkify(result.Summary), Color.Blue);
            await FollowupAsync(embed: embedPrivate.Build(), ephemeral: true);
        }
    }

    [SlashCommand("resumomes", "Resumo das conversas de um mês específico (formato MM/AAAA)")]
    public async Task ResumoMes([Summary("mes", "Mês no formato MM/AAAA (ex.: 07/2026). Padrão: mês atual.")] string mes = "")
    {
        await DeferAsync(ephemeral: true);
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

        var result = await _memory.GenerateMonthAsync(guildId, month, year);
        var monthName = new DateTime(year, month, 1).ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        var title = $"Resumo de {monthName} de {year}";
        var embed = BuildEmbed(title, DeLinkify(result.Summary), Color.Gold);
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("recap", "Resumo do que você perdeu desde sua última mensagem")]
    public async Task Recap()
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var result = await _memory.GenerateRecapAsync(guildId, Context.User.Id);
        var embed = BuildEmbed("Você Perdeu", DeLinkify(result.Summary), Color.Purple);
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    string DeLinkify(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary) || Context.Guild == null) return summary;

        summary = Regex.Replace(summary, @"<@(\d+)>", m =>
        {
            var user = Context.Guild.GetUser(ulong.Parse(m.Groups[1].Value));
            return user != null ? $"@{user.DisplayName}" : m.Value;
        });
        summary = Regex.Replace(summary, @"<#(\d+)>", m =>
        {
            var channel = Context.Guild.GetChannel(ulong.Parse(m.Groups[1].Value));
            return channel != null ? $"#{channel.Name}" : m.Value;
        });
        return summary;
    }

    static EmbedBuilder BuildEmbed(string title, string description, Color color) => new()
    {
        Title = title,
        Description = description,
        Color = color,
        Footer = new EmbedFooterBuilder { Text = "Gerado por IA" }
    };

    static bool IsPlaceholder(string summary) =>
        summary.StartsWith("Nenhuma") || summary.StartsWith("Erro");
}
