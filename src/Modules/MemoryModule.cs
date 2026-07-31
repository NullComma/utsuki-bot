using App.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

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
        if (result.PublicMessageId.HasValue)
        {
            await ReplyWithPublicLinkAsync(title, "Este resumo já foi postado no canal hoje:", result, guildId, Color.Blue);
            return;
        }

        var embed = BuildEmbed(title, result.Summary, Color.Blue);
        if (publico && !IsPlaceholder(result.Summary))
        {
            var message = await FollowupAsync(embed: embed.Build(), ephemeral: false);
            _memory.RegisterPublicMessage(guildId, "weekly", Context.Channel.Id, message.Id);
        }
        else
        {
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
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
            var match = System.Text.RegularExpressions.Regex.Match(mes.Trim(), @"^(0[1-9]|1[0-2])/(\d{4})$");
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
        var embed = BuildEmbed(title, result.Summary, Color.Gold);
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("recap", "Resumo do que você perdeu desde sua última mensagem")]
    public async Task Recap()
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var result = await _memory.GenerateRecapAsync(guildId, Context.User.Id);
        var embed = BuildEmbed("Você Perdeu", result.Summary, Color.Purple);
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    async Task ReplyWithPublicLinkAsync(string title, string message, MemoryResult result, ulong guildId, Color color)
    {
        var embed = BuildEmbed(title, message, color);
        embed.Description += $"\n\nVocê pode ver o resumo na mensagem enviada no canal:";
        embed.Footer = new EmbedFooterBuilder { Text = "Gerado por IA" };

        var jumpUrl = $"https://discord.com/channels/{guildId}/{result.PublicMessageChannelId}/{result.PublicMessageId}";
        var button = new ButtonBuilder()
            .WithLabel("Ver no canal")
            .WithStyle(ButtonStyle.Link)
            .WithUrl(jumpUrl);
        var components = new ComponentBuilder().WithButton(button).Build();

        await FollowupAsync(embed: embed.Build(), components: components, ephemeral: true);
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
