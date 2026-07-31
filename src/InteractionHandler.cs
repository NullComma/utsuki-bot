using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using App.Attributes;
using App.Services;

namespace App;

[Service]
public class InteractionHandler
{
    readonly DiscordSocketClient _client;
    readonly InteractionService _handler;
    readonly IServiceProvider _services;
    readonly IConfiguration _configuration;
    readonly LoggingService _log;

    public InteractionHandler(DiscordSocketClient client, InteractionService handler, IServiceProvider services, IConfiguration config, LoggingService log)
    {
        _client = client;
        _handler = handler;
        _services = services;
        _configuration = config;
        _log = log;
    }

    public async Task InitializeAsync()
    {
        // Process when the client is ready, so we can register our commands.
        _client.Ready += ReadyAsync;
        _handler.Log += LogAsync;

        // Add the public modules that inherit InteractionModuleBase<T> to the InteractionService
        await _handler.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

        // Process the InteractionCreated payloads to execute Interactions commands
        _client.InteractionCreated += HandleInteraction;

        // Also process the result of the command execution.
        _handler.InteractionExecuted += HandleInteractionExecute;
    }

    Task LogAsync(LogMessage log)
    {
        if(log.Exception != null) _log.Error(log.Exception.Message);
        else _log.Info(log.Message);
        return Task.CompletedTask;
    }

    async Task ReadyAsync()
    {
        await _client.SetGameAsync($"v{Program.VERSION} | chrisjogos.com", type: ActivityType.CustomStatus);

        await CleanupOrphanedGuildCommandsAsync();
        await _handler.RegisterCommandsGloballyAsync();
    }

    async Task CleanupOrphanedGuildCommandsAsync()
    {
        try
        {
            foreach (var guild in _client.Guilds)
            {
                var guildCommands = await guild.GetApplicationCommandsAsync();
                var orphaned = guildCommands.Where(c => !_handler.SlashCommands.Any(s => s.Name == c.Name)).ToList();
                foreach (var command in orphaned)
                {
                    _log.Info($"Deleting orphaned guild command /{command.Name} in {guild.Name} ({guild.Id})");
                    await command.DeleteAsync();
                }
            }
        }
        catch (Exception e)
        {
            _log.Error($"Failed to clean up orphaned guild commands: {e.Message}");
        }
    }

    async Task HandleInteraction(SocketInteraction interaction)
    {
        try
        {
            // Create an execution context that matches the generic type parameter of your InteractionModuleBase<T> modules.
            var context = new SocketInteractionContext(_client, interaction);

            // Execute the incoming command.
            var result = await _handler.ExecuteCommandAsync(context, _services);

            // Due to async nature of InteractionFramework, the result here may always be success.
            // That's why we also need to handle the InteractionExecuted event.
            if (!result.IsSuccess)
                switch (result.Error)
                {
                    case InteractionCommandError.UnmetPrecondition:
                        // implement
                        break;
                    default:
                        break;
                }
        }
        catch
        {
            // If Slash Command execution fails it is most likely that the original interaction acknowledgement will persist. It is a good idea to delete the original
            // response, or at least let the user know that something went wrong during the command execution.
            if (interaction.Type is InteractionType.ApplicationCommand)
                await interaction.GetOriginalResponseAsync().ContinueWith(async (msg) => await msg.Result.DeleteAsync());
        }
    }

    Task HandleInteractionExecute(ICommandInfo commandInfo, IInteractionContext context, IResult result)
    {
        if (!result.IsSuccess)
            switch (result.Error)
            {
                case InteractionCommandError.UnmetPrecondition:
                    // implement
                    break;
                default:
                    break;
            }

        return Task.CompletedTask;
    }
}
