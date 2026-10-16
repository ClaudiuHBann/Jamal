using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordMusicBot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordMusicBot.Hosting;

public sealed class DiscordBotHost(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    BotOptions options,
    ILogger<DiscordBotHost> logger) : IHostedService
{
    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private bool _registered;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await WaitForLavalinkAsync(cancellationToken);
        client.Log += LogAsync;
        interactions.Log += LogAsync;
        client.Ready += RegisterCommandsAsync;
        client.InteractionCreated += ExecuteInteractionAsync;
        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);
        await client.LoginAsync(TokenType.Bot, options.Token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        client.Ready -= RegisterCommandsAsync;
        client.InteractionCreated -= ExecuteInteractionAsync;
        await client.StopAsync();
        await client.LogoutAsync();
        client.Log -= LogAsync;
        interactions.Log -= LogAsync;
    }

    private async Task WaitForLavalinkAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = options.LavalinkAddress, Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("Authorization", options.LavalinkPassword);
        logger.LogInformation("Waiting for Lavalink at {Address}.", options.LavalinkAddress);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            while (true)
            {
                try
                {
                    using var response = await http.GetAsync("/v4/info", deadline.Token);
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        throw new InvalidOperationException("Lavalink rejected the password. Check matching LAVALINK_PASSWORD configuration.");
                    }
                    if (response.IsSuccessStatusCode)
                    {
                        logger.LogInformation("Lavalink is ready. Connecting to Discord.");
                        return;
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Lavalink did not become ready within three minutes. Check docker compose logs lavalink.");
        }
    }

    private async Task RegisterCommandsAsync()
    {
        await _registrationGate.WaitAsync();
        try
        {
            if (_registered) return;
            if (options.GuildId is { } guildId)
                await interactions.RegisterCommandsToGuildAsync(guildId, deleteMissing: true);
            else
                await interactions.RegisterCommandsGloballyAsync(deleteMissing: true);
            _registered = true;
            logger.LogInformation("Slash commands registered for {Scope}. Signed in as {Name}.",
                options.GuildId is { } registeredGuild ? $"server {registeredGuild}" : "global use", client.CurrentUser.Username);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not register slash commands. Check the bot invite and Discord__GuildId.");
        }
        finally { _registrationGate.Release(); }
    }

    private async Task ExecuteInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var result = await interactions.ExecuteCommandAsync(new SocketInteractionContext(client, interaction), services);
            if (!result.IsSuccess)
            {
                logger.LogWarning("Interaction failed: {Reason}", result.ErrorReason);
                const string message = "This command is available in a server. Use /help to see the supported commands.";
                if (!interaction.HasResponded)
                    await interaction.RespondAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
                else
                    await interaction.FollowupAsync("The command could not complete. Please try again.", ephemeral: true, allowedMentions: AllowedMentions.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to handle a Discord interaction.");
        }
    }

    private Task LogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        logger.Log(level, message.Exception, "{Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
