using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordMusicBot.Music;
using Microsoft.Extensions.Logging;

namespace DiscordMusicBot.Commands;

public enum ToggleMode { Toggle, On, Off }

[RequireContext(ContextType.Guild)]
public sealed class MusicModule(MusicService music, ILogger<MusicModule> logger)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("play", "Search YouTube or add a YouTube video or playlist URL.")]
    public Task PlayAsync([Summary("query", "Song name, artist, or YouTube URL.")] string query)
        => RunAsync(token => music.PlayAsync(Context, query, token));

    [SlashCommand("join", "Join your voice channel.")]
    public Task JoinAsync() => RunAsync(token => music.JoinAsync(Context, token));

    [SlashCommand("leave", "Clear this music session and leave the voice channel.")]
    public Task LeaveAsync() => RunAsync(token => music.LeaveAsync(Context, token));

    [SlashCommand("skip", "Skip the current song, including when looping.")]
    public Task SkipAsync() => RunAsync(token => music.SkipAsync(Context, token));

    [SlashCommand("previous", "Play the previous song and queue the interrupted song.")]
    public Task PreviousAsync() => RunAsync(token => music.PreviousAsync(Context, token));

    [SlashCommand("pause", "Pause playback. Disconnects if paused for three minutes.")]
    public Task PauseAsync() => RunAsync(token => music.PauseAsync(Context, token));

    [SlashCommand("resume", "Resume paused playback.")]
    public Task ResumeAsync() => RunAsync(token => music.ResumeAsync(Context, token));

    [SlashCommand("stop", "Stop playback, clear upcoming songs, and disable loop and autoplay.")]
    public Task StopAsync() => RunAsync(token => music.StopPlaybackAsync(Context, token));

    [SlashCommand("loop", "Toggle repetition of the current song, or explicitly turn it on or off.")]
    public Task LoopAsync([Summary("mode", "Toggle, On, or Off.")] ToggleMode mode = ToggleMode.Toggle)
        => RunAsync(token => music.LoopAsync(Context, mode switch { ToggleMode.On => true, ToggleMode.Off => false, _ => null }, token));

    [SlashCommand("autoplay", "Use YouTube mix recommendations to keep playing when the queue ends.")]
    public Task AutoplayAsync([Summary("mode", "Toggle, On, or Off.")] ToggleMode mode = ToggleMode.Toggle)
        => RunAsync(token => music.AutoplayAsync(Context, mode switch { ToggleMode.On => true, ToggleMode.Off => false, _ => null }, token));

    [SlashCommand("seek", "Jump to a position in the current song.")]
    public Task SeekAsync([Summary("position", "Seconds, m:ss, or h:mm:ss (for example 1:30).")] string position)
        => RunAsync(token => music.SeekAsync(Context, position, token));

    [SlashCommand("clearqueue", "Remove upcoming songs and keep the current song.")]
    public Task ClearQueueAsync() => RunAsync(token => music.ClearQueueAsync(Context, token));

    [SlashCommand("remove", "Remove a song using its number in /queue.")]
    public Task RemoveAsync([Summary("number", "Upcoming queue position, starting at 1."), MinValue(1)] int number)
        => RunAsync(token => music.RemoveAsync(Context, number, token));

    [SlashCommand("shuffle", "Shuffle upcoming songs and keep the current song.")]
    public Task ShuffleAsync() => RunAsync(token => music.ShuffleAsync(Context, token));

    [SlashCommand("nowplaying", "Show the current song, position, requester, and loop status.")]
    public Task NowPlayingAsync() => RunAsync(token => music.NowPlayingAsync(Context, token));

    [SlashCommand("queue", "Show the numbered music queue, ten songs per page.")]
    public Task QueueAsync([Summary("page", "Queue page to display."), MinValue(1)] int page = 1)
        => RunAsync(token => music.QueueAsync(Context, page, token));

    [SlashCommand("help", "Show every command and how to use the music bot.")]
    public Task HelpAsync() => RunAsync(_ => Task.FromResult(new MusicReply(Embed: HelpEmbed())), ephemeral: true);

    private async Task RunAsync(Func<CancellationToken, Task<MusicReply>> action, bool ephemeral = false)
    {
        await DeferAsync(ephemeral);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        MusicReply reply;
        try { reply = await action(deadline.Token); }
        catch (MusicException exception) { reply = Error(exception.Message); }
        catch (OperationCanceledException)
        {
            reply = Error("The request timed out. Check /nowplaying and /queue before trying again.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Command {Command} failed in server {GuildId}.",
                (Context.Interaction as SocketSlashCommand)?.CommandName, Context.Guild.Id);
            reply = Error("The music service could not complete that request. Please try again, or check the bot and Lavalink logs.");
        }
        await ModifyOriginalResponseAsync(message =>
        {
            message.Content = reply.Text ?? "";
            message.Embeds = reply.Embed is { } embed ? new[] { embed } : Array.Empty<Embed>();
            message.AllowedMentions = AllowedMentions.None;
        });
    }

    private static MusicReply Error(string message)
        => new(Embed: new EmbedBuilder().WithTitle("Could not complete the command").WithDescription(message).WithColor(Color.Orange).Build());

    private static Embed HelpEmbed() => new EmbedBuilder()
        .WithTitle("Music bot commands")
        .WithColor(Color.Blue)
        .WithDescription("Join a voice channel, then use `/play query:artist song`. YouTube video and playlist URLs work too. Playback controls require you to be in my voice channel.")
        .AddField("/play query", "Search YouTube or load a video/playlist URL. Automatically joins your channel.")
        .AddField("/skip · /previous", "Skip to the next song, or play the previous one and queue the interrupted song.")
        .AddField("/pause · /resume", "Pause and resume playback.")
        .AddField("/loop [mode]", "Toggle repeating the current song. Choose On or Off explicitly. Skip always advances.")
        .AddField("/autoplay [mode]", "Toggle related songs from YouTube mixes when the queue runs out. Queued songs play first; loop takes priority.")
        .AddField("/seek position", "Jump to a time: `/seek position:1:30`. Live streams cannot seek.")
        .AddField("/nowplaying", "Show the current song, progress, requester, loop, and autoplay status.")
        .AddField("/queue [page]", "Show upcoming songs with numbers starting at 1. Use `/queue page:2` for more.")
        .AddField("/remove number · /clearqueue", "Remove one upcoming song, or all upcoming songs. The current song stays.")
        .AddField("/shuffle", "Shuffle upcoming songs.")
        .AddField("/stop", "Stop playback, clear the queue, and disable loop and autoplay.")
        .AddField("/join · /leave", "Join your channel, or disconnect and clear the session.")
        .AddField("/help", "Show this command list.")
        .WithFooter("Leaves after 3 minutes without human listeners OR without playback. Paused time counts. Lyrics is planned for later.")
        .Build();
}
