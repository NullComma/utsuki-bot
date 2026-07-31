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

    [SlashCommand("weekly", "Resumo das conversas da semana")]
    public async Task Weekly([Summary("publico", "Postar no chat para todos verem?")] bool publico = false)
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var result = await _memory.GenerateWeeklyAsync(guildId);

        if (result.PublicMessageId.HasValue)
        {
            await ReplyWithPublicLinkAsync("Resumo da Semana", "Este resumo já foi postado no canal hoje:", result, guildId, Color.Blue);
            return;
        }

        var embed = BuildEmbed("Resumo da Semana", result.Summary, Color.Blue);
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

    [SlashCommand("monthly", "Resumo das conversas do mês")]
    public async Task Monthly([Summary("publico", "Postar no chat para todos verem?")] bool publico = false)
    {
        await DeferAsync(ephemeral: true);
        var guildId = Context.Guild?.Id ?? 0;
        if (guildId == 0) { await FollowupAsync("Comando apenas em servidor.", ephemeral: true); return; }

        var result = await _memory.GenerateMonthlyAsync(guildId);

        if (result.PublicMessageId.HasValue)
        {
            await ReplyWithPublicLinkAsync("Resumo do Mês", "Este resumo já foi postado no canal hoje:", result, guildId, Color.Green);
            return;
        }

        var embed = BuildEmbed("Resumo do Mês", result.Summary, Color.Green);
        if (publico && !IsPlaceholder(result.Summary))
        {
            var message = await FollowupAsync(embed: embed.Build(), ephemeral: false);
            _memory.RegisterPublicMessage(guildId, "monthly", Context.Channel.Id, message.Id);
        }
        else
        {
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }
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
