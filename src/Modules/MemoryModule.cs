using App.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace App.Modules;

[CommandContextType(InteractionContextType.Guild)]
public class MemoryModule : InteractionModuleBase<SocketInteractionContext>
{
    readonly MemoryService _memory;
    readonly MessageArchiveService _archive;

    public MemoryModule(MemoryService memory, MessageArchiveService archive)
    {
        _memory = memory;
        _archive = archive;
    }

    [SlashCommand("weekly", "Resumo das conversas da semana")]
    public async Task Weekly()
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var summary = await _memory.GenerateWeeklyAsync(guildId);
        var embed = new EmbedBuilder
        {
            Title = "Resumo da Semana",
            Description = summary,
            Color = Color.Blue,
            Footer = new EmbedFooterBuilder { Text = "Gerado por IA" }
        };
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("monthly", "Resumo das conversas do mês")]
    public async Task Monthly()
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var summary = await _memory.GenerateMonthlyAsync(guildId);
        var embed = new EmbedBuilder
        {
            Title = "Resumo do Mês",
            Description = summary,
            Color = Color.Green,
            Footer = new EmbedFooterBuilder { Text = "Gerado por IA" }
        };
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("recap", "Resumo do que você perdeu desde sua última mensagem")]
    public async Task Recap()
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var summary = await _memory.GenerateRecapAsync(guildId, Context.User.Id);
        var embed = new EmbedBuilder
        {
            Title = "Você Perdeu",
            Description = summary,
            Color = Color.Purple,
            Footer = new EmbedFooterBuilder { Text = "Gerado por IA" }
        };
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }
}
