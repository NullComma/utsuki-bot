using App.Services;
using Discord;
using Discord.Interactions;

namespace App.Modules;

public class ArchiveModule : InteractionModuleBase<SocketInteractionContext>
{
    readonly MessageArchiveService _archive;

    public ArchiveModule(MessageArchiveService archive)
    {
        _archive = archive;
    }

    [SlashCommand("archivestatus", "Show archive database status")]
    public async Task ShowArchiveStatus()
    {
        await DeferAsync();

        var stats = await _archive.GetArchiveStatsAsync();

        var embed = new EmbedBuilder
        {
            Title = "Archive Status",
            Color = stats.IsBackfilling ? Color.Gold : Color.Green
        };

        embed.AddField("Total Messages", $"{stats.TotalMessages:N0}", true);
        embed.AddField("Total Channels", stats.TotalChannels, true);
        embed.AddField("Total Users", stats.TotalUsers, true);
        embed.AddField("DB Size", FormatFileSize(stats.DbSizeBytes), true);

        if (stats.OldestMessage.HasValue)
            embed.AddField("Oldest Message", $"<t:{ToUnix(stats.OldestMessage.Value)}:R>", true);
        if (stats.NewestMessage.HasValue)
            embed.AddField("Newest Message", $"<t:{ToUnix(stats.NewestMessage.Value)}:R>", true);

        embed.AddField("Status", stats.IsBackfilling ? "Initial backfilling..." : "Ongoing archive", true);

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("archivestats", "Show archive stats for a user")]
    public async Task ShowUserStats(IUser? user = null)
    {
        await DeferAsync();

        var target = user ?? Context.User;

        var stats = await _archive.GetUserStatsAsync(target.Id);

        if (stats == null)
        {
            await FollowupAsync($"No archived messages for {target.Mention}.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder
        {
            Title = $"Archive Stats — {target.GlobalName ?? target.Username}",
            ThumbnailUrl = target.GetAvatarUrl() ?? target.GetDefaultAvatarUrl(),
            Color = Color.Blue
        };

        embed.AddField("Messages", $"{stats.MessageCount:N0}", true);
        embed.AddField("Channels", stats.ChannelCount, true);

        if (stats.FirstMessage.HasValue)
            embed.AddField("First Message", $"<t:{ToUnix(stats.FirstMessage.Value)}:R>", true);
        if (stats.LastMessage.HasValue)
            embed.AddField("Last Message", $"<t:{ToUnix(stats.LastMessage.Value)}:R>", true);

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("archiveclean", "Purge messages that don't meet current archive filters")]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task CleanArchive()
    {
        await DeferAsync(ephemeral: true);

        var before = await _archive.GetArchiveStatsAsync();
        var deleted = await _archive.PurgeBadEntriesAsync();
        var after = await _archive.GetArchiveStatsAsync();

        var embed = new EmbedBuilder
        {
            Title = "Archive Cleanup",
            Color = deleted > 0 ? Color.Green : Color.Blue
        };

        embed.AddField("Deleted", $"{deleted:N0} messages", true);
        embed.AddField("Before", $"{before.TotalMessages:N0} messages", true);
        embed.AddField("After", $"{after.TotalMessages:N0} messages", true);

        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MB"
    };

    static long ToUnix(DateTime dt) =>
        ((DateTimeOffset)dt).ToUnixTimeSeconds();
}
