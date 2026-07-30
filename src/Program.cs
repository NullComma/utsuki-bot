using System.Reflection;
using App.Extensions;
using App.Models;
using Discord;
using Discord.Commands;
using Microsoft.Extensions.DependencyInjection;
using App.Services;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RunMode = Discord.Commands.RunMode;

namespace App;

internal static class Program {

    public static readonly Version VERSION;

    static Program()
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version("7.0.0");
        var envVersion = Environment.GetEnvironmentVariable("BOT_VERSION");
        
        if (int.TryParse(envVersion, out var buildNumber))
        {
            VERSION = new Version(assemblyVersion.Major, assemblyVersion.Minor, buildNumber);
        }
        else if (Version.TryParse(envVersion, out var parsedVersion))
        {
            VERSION = parsedVersion;
        }
        else
        {
            VERSION = assemblyVersion;
        }
    }

    static IConfigurationRoot Configuration = null!;

    public static async Task Main(string[] args)
    {
        Console.WriteLine($"Program Started, v{VERSION}");
        Configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var builder = Host.CreateApplicationBuilder(args);
        
        // Bind settings
        builder.Services.Configure<BotSettings>(settings => {
            settings.DiscordToken = Configuration["DISCORD_TOKEN_UTSUKI"] ?? string.Empty;
            settings.AiToken = Configuration["AI_TOKEN"] ?? string.Empty;
            settings.AiEndpoint = Configuration["AI_ENDPOINT"] ?? string.Empty;
            settings.AiModel = Configuration["AI_MODEL"] ?? string.Empty;
            settings.WeatherApiKey = Configuration["API_KEY_WEATHER"] ?? string.Empty;
            settings.MainGuildId = ulong.TryParse(Configuration["MAIN_GUILD_ID"], out var mgid) ? mgid : 264800866169651203; // Concord
            settings.GgjGuildId = ulong.TryParse(Configuration["GGJ_GUILD_ID"], out var ggid) ? ggid : default;
            var archiveIds = Configuration["ARCHIVE_GUILD_IDS"];
            if (!string.IsNullOrWhiteSpace(archiveIds))
            {
                settings.ArchiveGuildIds = archiveIds
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(id => ulong.TryParse(id, out var parsed) ? parsed : 0)
                    .Where(id => id != 0)
                    .ToList();
            }
        });

        ConfigureServices(builder.Services);
        var host = builder.Build();

        var settings = host.Services.GetRequiredService<IOptionsSnapshot<BotSettings>>().Value;

        if (string.IsNullOrWhiteSpace(settings.DiscordToken))
            throw new Exception("Bot cannot be started without the bot's token.");

        var client = host.Services.GetRequiredService<DiscordSocketClient>();
        await client.LoginAsync(TokenType.Bot, settings.DiscordToken);
        await client.StartAsync();

        var commands = host.Services.GetRequiredService<CommandService>();
        await commands.AddModulesAsync(Assembly.GetEntryAssembly(), host.Services);

        await host.Services.GetRequiredService<InteractionHandler>().InitializeAsync();

        host.Services.GetRequiredService<MessageArchiveService>().Start();

        await host.RunAsync();
    }


    static void ConfigureServices(IServiceCollection services) {
        services
        .AddSingleton(Configuration)
        .AddSingleton<Random>()
        .AddHttpClient()
        .AddSingleton(new DiscordSocketClient(new DiscordSocketConfig {
            LogLevel = LogSeverity.Info,
            GatewayIntents = GatewayIntents.AllUnprivileged
                             | GatewayIntents.MessageContent | GatewayIntents.GuildPresences | GatewayIntents.GuildMembers,
        }))
        .AddSingleton(new CommandService(new CommandServiceConfig
        {
            LogLevel = LogSeverity.Verbose,
            DefaultRunMode = RunMode.Async,
        }))
        .AddActivatedSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()))
        .AddAnnotatedServices(Assembly.GetExecutingAssembly())
        ;
    }

}
