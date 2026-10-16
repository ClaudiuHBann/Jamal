using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordMusicBot.Configuration;
using DiscordMusicBot.Hosting;
using DiscordMusicBot.Music;
using Lavalink4NET;
using Lavalink4NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var options = BotOptions.Load(builder.Configuration);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Lavalink4NET", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
    LogLevel = LogSeverity.Info,
    AlwaysDownloadUsers = false,
}));
builder.Services.AddSingleton(provider => new InteractionService(
    provider.GetRequiredService<DiscordSocketClient>(),
    new InteractionServiceConfig { DefaultRunMode = RunMode.Async, LogLevel = LogSeverity.Info }));
builder.Services.AddHostedService<DiscordBotHost>();
builder.Services.AddLavalink();
builder.Services.AddHttpClient("lavalink", http => http.Timeout = TimeSpan.FromSeconds(20));
builder.Services.Configure<AudioServiceOptions>(audio =>
{
    audio.BaseAddress = options.LavalinkAddress;
    audio.Passphrase = options.LavalinkPassword;
    audio.HttpClientName = "lavalink";
    audio.ReadyTimeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<MusicService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MusicService>());

using var host = builder.Build();
await host.RunAsync();
