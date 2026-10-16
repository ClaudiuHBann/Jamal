using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.Extensions;
using Lavalink4NET.Players;
using Lavalink4NET.Protocol.Payloads.Events;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordMusicBot.Music;

public sealed class MusicService(
    DiscordSocketClient client,
    IAudioService audio,
    ILogger<MusicService> logger) : BackgroundService
{
    private const int QueueLimit = 500;
    private const int HistoryLimit = 50;
    private const int RecentTrackLimit = 100;
    private const int PageSize = 10;
    private static readonly TimeSpan DepartureDelay = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<ulong, GuildSession> _sessions = new();
    private readonly Channel<PlayerSignal> _signals = Channel.CreateUnbounded<PlayerSignal>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Channel<AutoplayRequest> _autoplayRequests = Channel.CreateUnbounded<AutoplayRequest>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private sealed record AutoplayRequest(ulong GuildId, MusicPlayer Player, PlaybackItem Seed,
        long Generation, IReadOnlySet<string> ExcludedTrackIds, CancellationToken CancellationToken);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateUpdatedAsync;
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(ProcessSignalsAsync(stoppingToken), MonitorSessionsAsync(stoppingToken), ProcessAutoplayAsync(stoppingToken));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated -= OnVoiceStateUpdatedAsync;
        await base.StopAsync(cancellationToken);
        foreach (var session in _sessions.Values)
        {
            await session.Gate.WaitAsync(cancellationToken);
            try { await DisconnectCoreAsync(session, cancellationToken); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not close player for {GuildId} during shutdown.", session.GuildId); }
            finally { session.Gate.Release(); }
        }
    }

    public async Task<MusicReply> PlayAsync(SocketInteractionContext context, string query, CancellationToken cancellationToken)
    {
        _ = CallerChannel(context);
        query = query.Trim().Trim('<', '>');
        if (query.Length is 0 or > 500)
            throw new MusicException("Enter a song name or YouTube URL, up to 500 characters.");

        var isUrl = query.Contains("://", StringComparison.Ordinal);
        if (isUrl && (!Uri.TryCreate(query, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !uri.IsDefaultPort
            || !(uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))))
            throw new MusicException("Only YouTube video and playlist URLs are supported.");

        await audio.WaitForReadyAsync(cancellationToken);
        var result = await audio.Tracks.LoadTracksAsync(
            query, new TrackLoadOptions(
                SearchMode: isUrl ? TrackSearchMode.None : TrackSearchMode.YouTube,
                SearchBehavior: StrictSearchBehavior.Explicit), cancellationToken: cancellationToken);
        if (result.Exception is not null)
            throw new MusicException("YouTube could not load that request. Try a different song or check the Lavalink logs.");
        if (!result.HasMatches || result.Tracks.IsEmpty)
            throw new MusicException("No YouTube results found. Try an artist and song name, or a video URL.");

        var tracks = result.IsPlaylist ? result.Tracks.ToArray() : [result.Tracks[0]];
        if (tracks.Any(track => !string.Equals(track.SourceName, "youtube", StringComparison.OrdinalIgnoreCase)))
            throw new MusicException("Only YouTube tracks are supported.");
        if (tracks.Length > QueueLimit)
            throw new MusicException($"That playlist is too large. Load a playlist with at most {QueueLimit} tracks.");

        return await WithSessionAsync(context, true, async session =>
        {
            if (session.Queue.Count + tracks.Length > QueueLimit)
                throw new MusicException($"The upcoming queue holds at most {QueueLimit} tracks. Remove or clear tracks first.");
            session.TextChannelId = context.Channel.Id;
            var entries = tracks.Select(track => new TrackEntry(track, context.User.Id)).ToArray();
            session.Queue.AddRange(entries);
            var wasIdle = session.Current is null;
            if (wasIdle) await StartNextAsync(session, cancellationToken);
            if (wasIdle && session.Current is null)
                return new MusicReply($"No track could start. {session.Queue.Count} upcoming track(s) remain; check the Lavalink logs or use /skip to retry the queue.");
            if (entries.Length > 1)
                return new MusicReply($"Added {entries.Length} tracks from **{MusicReply.Safe(result.Playlist!.Name)}**."
                    + (wasIdle && session.Current is { } current ? $" Playing {MusicReply.TrackLabel(current.Entry)}." : ""));
            return new MusicReply(wasIdle && session.Current is { } item
                ? $"Playing {MusicReply.TrackLabel(item.Entry)}."
                : $"Added {MusicReply.TrackLabel(entries[0])} to the queue (position {session.Queue.Count}).");
        }, cancellationToken);
    }

    public Task<MusicReply> JoinAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, true, session => Task.FromResult(
            new MusicReply($"Connected to <#{session.Player!.VoiceChannelId}>. Use /play to start music.")), cancellationToken);

    public Task<MusicReply> LeaveAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, async session =>
        {
            await DisconnectCoreAsync(session, cancellationToken);
            return new MusicReply("Disconnected and cleared the playback session.");
        }, cancellationToken);

    public Task<MusicReply> SkipAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, async session =>
        {
            if (session.Current is null && session.AutoplaySeed is not null)
                return new MusicReply("Autoplay is choosing a related song. Use /play to queue your own song, or /autoplay mode:Off to cancel.");
            if (session.Current is null && session.Queue.Count == 0) _ = RequireTrack(session);
            var previous = session.Current;
            if (session.Queue.Count == 0)
            {
                await session.Player!.StopAsync(cancellationToken);
                session.Current = null;
                session.Paused = false;
                session.PlaybackStarted = false;
                MarkIdle(session);
            }
            else
            {
                await StartNextAsync(session, cancellationToken);
            }
            if (previous is not null) Remember(session, previous.Entry);
            if (session.Current is null && session.Queue.Count == 0 && session.Autoplay && previous is not null)
                BeginAutoplayLookup(session, previous);
            return new MusicReply(session.Current is { } current
                ? $"Skipped. Playing {MusicReply.TrackLabel(current.Entry)}."
                : session.AutoplaySeed is not null ? "Skipped. Choosing a related song from a YouTube mix."
                : "Skipped. The queue is empty; I will leave after three minutes without playback.");
        }, cancellationToken);

    public Task<MusicReply> PreviousAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, async session =>
        {
            if (session.History.Count == 0) throw new MusicException("There is no previous song in this session.");
            if (session.Current is not null && session.Queue.Count == QueueLimit)
                throw new MusicException("Remove a queued track first so there is room to save the interrupted song.");
            var previous = session.History[^1];
            var interrupted = session.Current?.Entry;
            await StartEntryAsync(session, previous, cancellationToken);
            session.History.RemoveAt(session.History.Count - 1);
            if (interrupted is not null) session.Queue.Insert(0, interrupted);
            return new MusicReply($"Playing previous song: {MusicReply.TrackLabel(previous)}.");
        }, cancellationToken);

    public Task<MusicReply> PauseAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, async session =>
        {
            _ = RequireTrack(session);
            if (session.Paused) return new MusicReply("Playback is already paused.");
            var position = session.Player!.Position?.Position ?? TimeSpan.Zero;
            await session.Player.PauseAsync(cancellationToken);
            session.PausedPosition = position;
            session.Paused = true;
            MarkIdle(session);
            return new MusicReply("Paused. Use /resume within three minutes to keep this session.");
        }, cancellationToken);

    public Task<MusicReply> ResumeAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, async session =>
        {
            _ = RequireTrack(session);
            if (!session.Paused) return new MusicReply("Playback is already running.");
            await session.Player!.ResumeAsync(cancellationToken);
            session.Paused = false;
            if (session.PlaybackStarted && session.Player.ConnectionState.IsConnected) session.IdleSince = null;
            return new MusicReply("Resumed playback.");
        }, cancellationToken);

    public Task<MusicReply> StopPlaybackAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, async session =>
        {
            session.Autoplay = false;
            session.CancelAutoplayLookup();
            var previous = session.Current?.Entry;
            await session.Player!.StopAsync(cancellationToken);
            if (previous is not null) Remember(session, previous);
            session.Current = null;
            session.Queue.Clear();
            session.Loop = false;
            session.Paused = false;
            session.PlaybackStarted = false;
            MarkIdle(session);
            return new MusicReply("Stopped playback, cleared the queue, and disabled loop and autoplay. I will leave after three minutes without playback.");
        }, cancellationToken);

    public Task<MusicReply> LoopAsync(SocketInteractionContext context, bool? enabled, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, session =>
        {
            _ = RequireTrack(session);
            session.Loop = enabled ?? !session.Loop;
            return Task.FromResult(new MusicReply(session.Loop ? "Current-song loop enabled. /skip still advances to the next song." : "Loop disabled."));
        }, cancellationToken);

    public Task<MusicReply> AutoplayAsync(SocketInteractionContext context, bool? enabled, CancellationToken cancellationToken)
        => WithSessionAsync(context, false,
            session => Task.FromResult(SetAutoplay(session, enabled, context.Channel.Id)), cancellationToken);

    private static MusicReply SetAutoplay(GuildSession session, bool? enabled, ulong textChannelId)
    {
        var turnOn = enabled ?? !session.Autoplay;
        if (turnOn == session.Autoplay)
            return new MusicReply(turnOn ? "Autoplay is already enabled." : "Autoplay is already disabled.");
        if (turnOn && !IsYouTubeTrack(RequireTrack(session).Entry.Track))
            throw new MusicException("Autoplay needs a YouTube song to choose related music.");
        session.Autoplay = turnOn;
        session.TextChannelId = textChannelId;
        if (!turnOn) session.CancelAutoplayLookup();
        return new MusicReply(turnOn
            ? "Autoplay enabled. When your queue ends, I will choose a related song from a YouTube mix."
            : "Autoplay disabled. Your current song and queued songs will keep playing.");
    }

    public Task<MusicReply> SeekAsync(SocketInteractionContext context, string position, CancellationToken cancellationToken)
    {
        var target = MusicReply.ParsePosition(position);
        return WithSessionAsync(context, false, async session =>
        {
            var track = RequireTrack(session).Entry.Track;
            if (!track.IsSeekable || track.IsLiveStream) throw new MusicException("This stream does not support seeking.");
            if (target >= track.Duration) throw new MusicException($"Choose a position before {MusicReply.Duration(track.Duration)}.");
            await session.Player!.SeekAsync(target, cancellationToken);
            if (session.Paused) session.PausedPosition = target;
            return new MusicReply($"Moved to {MusicReply.Duration(target)}.");
        }, cancellationToken);
    }

    public Task<MusicReply> ClearQueueAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, session =>
        {
            var count = session.Queue.Count;
            session.Queue.Clear();
            return Task.FromResult(new MusicReply($"Removed {count} upcoming track(s)."
                + (session.Autoplay ? " Autoplay remains enabled." : "")));
        }, cancellationToken);

    public Task<MusicReply> RemoveAsync(SocketInteractionContext context, int number, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, session =>
        {
            if (number < 1 || number > session.Queue.Count) throw new MusicException("That queue position does not exist. Use /queue to see the current numbers.");
            var track = session.Queue[number - 1];
            session.Queue.RemoveAt(number - 1);
            return Task.FromResult(new MusicReply($"Removed {MusicReply.TrackLabel(track)} from position {number}."));
        }, cancellationToken);

    public Task<MusicReply> ShuffleAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => WithSessionAsync(context, false, session =>
        {
            if (session.Queue.Count < 2) throw new MusicException("Add at least two upcoming songs before shuffling.");
            for (var index = session.Queue.Count - 1; index > 0; index--)
            {
                var other = Random.Shared.Next(index + 1);
                (session.Queue[index], session.Queue[other]) = (session.Queue[other], session.Queue[index]);
            }
            return Task.FromResult(new MusicReply($"Shuffled {session.Queue.Count} upcoming songs."));
        }, cancellationToken);

    public Task<MusicReply> NowPlayingAsync(SocketInteractionContext context, CancellationToken cancellationToken)
        => ReadSessionAsync(context.Guild.Id, session =>
        {
            var entry = RequireTrack(session).Entry;
            var position = session.Paused ? session.PausedPosition : session.Player?.Position?.Position ?? TimeSpan.Zero;
            if (!entry.Track.IsLiveStream && position > entry.Track.Duration) position = entry.Track.Duration;
            var embed = new EmbedBuilder()
                .WithTitle(session.Paused ? "Paused" : "Now playing")
                .WithDescription(MusicReply.TrackLabel(entry))
                .WithColor(Color.Blue)
                .AddField("Artist / channel", MusicReply.Safe(entry.Track.Author), true)
                .AddField("Position", entry.Track.IsLiveStream ? "Live stream" : $"{MusicReply.Duration(position)} / {MusicReply.Duration(entry.Track.Duration)}", true)
                .AddField("Requested by", entry.IsAutoplay ? "Autoplay · YouTube mix" : $"<@{entry.RequesterId}>", true)
                .AddField("Loop", session.Loop ? "Current song" : "Off", true)
                .AddField("Autoplay", session.Autoplay ? "On" : "Off", true);
            if (entry.Track.ArtworkUri is { } artwork) embed.WithThumbnailUrl(artwork.AbsoluteUri);
            return new MusicReply(Embed: embed.Build());
        }, cancellationToken);

    public Task<MusicReply> QueueAsync(SocketInteractionContext context, int page, CancellationToken cancellationToken)
        => ReadSessionAsync(context.Guild.Id, session =>
        {
            var pages = Math.Max(1, (session.Queue.Count + PageSize - 1) / PageSize);
            if (page < 1 || page > pages) throw new MusicException($"Choose a queue page between 1 and {pages}.");
            var text = new StringBuilder();
            if (session.Current is { } current) text.AppendLine($"**Current:** {MusicReply.TrackLabel(current.Entry)}\n");
            if (session.Queue.Count == 0)
                text.AppendLine(session.AutoplaySeed is not null
                    ? "Finding a related song from a YouTube mix…"
                    : session.Autoplay && session.Current is not null
                        ? "Autoplay will choose a related song when this song ends."
                        : "The upcoming queue is empty. Add a song with /play.");
            for (var index = (page - 1) * PageSize; index < Math.Min(page * PageSize, session.Queue.Count); index++)
            {
                var entry = session.Queue[index];
                text.AppendLine($"**{index + 1}.** {MusicReply.TrackLabel(entry, 80)} · {(entry.Track.IsLiveStream ? "Live" : MusicReply.Duration(entry.Track.Duration))}");
            }
            return new MusicReply(Embed: new EmbedBuilder().WithTitle("Music queue").WithDescription(text.ToString())
                .WithColor(Color.Blue).WithFooter($"Page {page}/{pages} · {session.Queue.Count} upcoming · Autoplay: {(session.Autoplay ? "On" : "Off")}"
                    + (pages > 1 ? " · /queue page:2 to change pages" : "")).Build());
        }, cancellationToken);

    private async Task<MusicReply> WithSessionAsync(SocketInteractionContext context, bool join,
        Func<GuildSession, Task<MusicReply>> action, CancellationToken cancellationToken)
    {
        var session = _sessions.GetOrAdd(context.Guild.Id, id => new GuildSession(id));
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            var channel = CallerChannel(context);
            if (session.Player?.State == PlayerState.Destroyed) session.Reset();
            if (session.Player is { } existing && existing.VoiceChannelId != channel.Id)
                throw new MusicException($"Join <#{existing.VoiceChannelId}> to control this music session.");
            if (session.Player is null)
            {
                if (!join) throw new MusicException("I am not connected. Use /play or /join first.");
                var permissions = context.Guild.CurrentUser.GetPermissions(channel);
                if (!permissions.Connect || !permissions.Speak)
                    throw new MusicException("I need Connect and Speak permissions in your voice channel.");
                await audio.WaitForReadyAsync(cancellationToken);
                var factory = PlayerFactory.Create<MusicPlayer, LavalinkPlayerOptions>(properties => new MusicPlayer(properties, Publish));
                // Lavalink4NET 4.2.2 reverses the mute/deafen argument order used by
                // Discord.Net 3.20.2. Equal false values avoid muting the bot on join.
                session.Player = await audio.Players.JoinAsync(context.Guild.Id, channel.Id, factory,
                    new LavalinkPlayerOptions { SelfDeaf = false, SelfMute = false, DisconnectOnStop = false }, cancellationToken);
                session.TextChannelId = context.Channel.Id;
                MarkIdle(session);
                UpdatePresence(session);
            }
            return await action(session);
        }
        finally { session.Gate.Release(); }
    }

    private async Task<MusicReply> ReadSessionAsync(ulong guildId, Func<GuildSession, MusicReply> action, CancellationToken cancellationToken)
    {
        var session = _sessions.GetOrAdd(guildId, id => new GuildSession(id));
        await session.Gate.WaitAsync(cancellationToken);
        try { return action(session); }
        finally { session.Gate.Release(); }
    }

    private static SocketVoiceChannel CallerChannel(SocketInteractionContext context)
    {
        var channel = (context.User as SocketGuildUser)?.VoiceChannel;
        if (channel is null) throw new MusicException("Join a voice channel first.");
        if (channel is SocketStageChannel) throw new MusicException("Use a regular voice channel; stage channels are not supported.");
        return channel;
    }

    private static PlaybackItem RequireTrack(GuildSession session)
        => session.Current ?? throw new MusicException("Nothing is playing right now. Use /play to add a song.");

    private async Task StartEntryAsync(GuildSession session, TrackEntry entry, CancellationToken cancellationToken)
    {
        var previous = session.Current;
        var wasPaused = session.Paused;
        var previousStarted = session.PlaybackStarted;
        var previousIdleSince = session.IdleSince;
        session.Current = new PlaybackItem(entry);
        session.PlaybackStarted = false;
        MarkIdle(session);
        try
        {
            await session.Player!.PlayAsync(session.Current, cancellationToken: cancellationToken);
            // Replacing a paused track preserves the server's paused flag unless explicitly resumed.
            if (session.Player.IsPaused) await session.Player.ResumeAsync(cancellationToken);
            session.Paused = false;
            session.CancelAutoplayLookup();
            session.RecentTrackIds.Enqueue(entry.Track.Identifier);
            if (session.RecentTrackIds.Count > RecentTrackLimit) session.RecentTrackIds.Dequeue();
        }
        catch
        {
            session.Current = previous;
            session.Paused = wasPaused;
            session.PlaybackStarted = previousStarted;
            session.IdleSince = previousIdleSince;
            if (previous is null || wasPaused) MarkIdle(session);
            throw;
        }
    }

    private async Task StartNextAsync(GuildSession session, CancellationToken cancellationToken)
    {
        var failedAttempts = 0;
        while (session.Queue.Count > 0)
        {
            var entry = session.Queue[0];
            session.Queue.RemoveAt(0);
            try
            {
                await StartEntryAsync(session, entry, cancellationToken);
                return;
            }
            catch (OperationCanceledException) { session.Queue.Insert(0, entry); throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not start a queued YouTube track in {GuildId}.", session.GuildId);
                await NotifyAsync(session, $"Could not play {MusicReply.TrackLabel(entry)}. Trying the next queued song.");
                // A node/network failure will also affect the rest of the queue; preserve it for retry.
                if (exception is HttpRequestException or TimeoutException)
                {
                    session.Queue.Insert(0, entry);
                    throw;
                }
                if (++failedAttempts >= 3)
                {
                    await NotifyAsync(session, "Several songs could not start. Remaining queued songs were kept; use /skip or /play to try again.");
                    break;
                }
            }
        }
        await session.Player!.StopAsync(cancellationToken);
        session.Current = null;
        session.Paused = false;
        session.PlaybackStarted = false;
        MarkIdle(session);
    }

    private static void Remember(GuildSession session, TrackEntry entry)
    {
        session.History.Add(entry);
        if (session.History.Count > HistoryLimit) session.History.RemoveAt(0);
    }

    private static void MarkIdle(GuildSession session) => session.IdleSince ??= Stopwatch.GetTimestamp();

    private static bool IsYouTubeTrack(LavalinkTrack track)
        => string.Equals(track.SourceName, "youtube", StringComparison.OrdinalIgnoreCase)
            && track.Identifier is { Length: 11 } identifier
            && identifier.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    private void BeginAutoplayLookup(GuildSession session, PlaybackItem seed)
    {
        session.CancelAutoplayLookup();
        if (!IsYouTubeTrack(seed.Entry.Track) || session.Player is null)
        {
            session.Autoplay = false;
            return;
        }
        session.AutoplaySeed = seed;
        session.AutoplayCancellation = new CancellationTokenSource();
        var excluded = session.RecentTrackIds.Concat(session.Queue.Select(entry => entry.Track.Identifier))
            .Append(seed.Entry.Track.Identifier).ToHashSet(StringComparer.Ordinal);
        _autoplayRequests.Writer.TryWrite(new AutoplayRequest(session.GuildId, session.Player, seed,
            session.AutoplayGeneration, excluded, session.AutoplayCancellation.Token));
    }

    private Task ProcessAutoplayAsync(CancellationToken cancellationToken)
        => Parallel.ForEachAsync(_autoplayRequests.Reader.ReadAllAsync(cancellationToken),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (request, workerToken) =>
            {
                IReadOnlyList<LavalinkTrack> recommendations = [];
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, workerToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    var id = request.Seed.Entry.Track.Identifier;
                    var mixUrl = $"https://www.youtube.com/watch?v={id}&list=RD{id}";
                    var result = await audio.Tracks.LoadTracksAsync(mixUrl,
                        new TrackLoadOptions(SearchMode: TrackSearchMode.None, SearchBehavior: StrictSearchBehavior.Explicit),
                        cancellationToken: deadline.Token);
                    if (result.Exception is null)
                        recommendations = result.Tracks.Where(track => IsYouTubeTrack(track) && !track.IsLiveStream
                                && track.Duration > TimeSpan.Zero && !request.ExcludedTrackIds.Contains(track.Identifier))
                            .DistinctBy(track => track.Identifier).Take(25).ToArray();
                }
                catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested || workerToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("YouTube mix lookup timed out in {GuildId}.", request.GuildId);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not load a YouTube mix in {GuildId}.", request.GuildId);
                }
                if (!request.CancellationToken.IsCancellationRequested && !workerToken.IsCancellationRequested)
                    Publish(new PlayerSignal(request.GuildId, request.Player, PlayerSignalKind.AutoplayReady, request.Seed,
                        AutoplayGeneration: request.Generation, Recommendations: recommendations));
            });

    private void Publish(PlayerSignal signal) => _signals.Writer.TryWrite(signal);

    private Task OnVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (before.VoiceChannel is { } previous) Publish(new PlayerSignal(previous.Guild.Id, null, PlayerSignalKind.VoiceChanged));
        if (after.VoiceChannel is { } current && current.Guild.Id != before.VoiceChannel?.Guild.Id)
            Publish(new PlayerSignal(current.Guild.Id, null, PlayerSignalKind.VoiceChanged));
        return Task.CompletedTask;
    }

    private async Task ProcessSignalsAsync(CancellationToken cancellationToken)
    {
        await foreach (var signal in _signals.Reader.ReadAllAsync(cancellationToken))
        {
            if (!_sessions.TryGetValue(signal.GuildId, out var session)) continue;
            await session.Gate.WaitAsync(cancellationToken);
            try
            {
                if (signal.Kind == PlayerSignalKind.VoiceChanged) { UpdatePresence(session); continue; }
                if (!ReferenceEquals(session.Player, signal.Player)) continue;
                if (signal.Kind == PlayerSignalKind.Disconnected) { session.Reset(); continue; }
                if (signal.Kind == PlayerSignalKind.AutoplayReady)
                {
                    if (!session.Autoplay || signal.AutoplayGeneration != session.AutoplayGeneration
                        || !ReferenceEquals(session.AutoplaySeed, signal.Item)) continue;
                    session.CancelAutoplayLookup();
                    if (session.Current is not null || session.Queue.Count > 0) continue;
                    var recommendation = signal.Recommendations?.FirstOrDefault(track => !session.RecentTrackIds.Contains(track.Identifier));
                    if (recommendation is null)
                    {
                        session.Autoplay = false;
                        await NotifyAsync(session, "YouTube did not return a new related song. Autoplay was turned off; use /play to continue.");
                        continue;
                    }
                    await StartEntryAsync(session, new TrackEntry(recommendation, client.CurrentUser.Id, IsAutoplay: true), cancellationToken);
                    await NotifyAsync(session, $"Autoplay: playing {MusicReply.TrackLabel(session.Current!.Entry)} from a YouTube mix.");
                    continue;
                }
                if (signal.Item is null || !ReferenceEquals(signal.Item, session.Current)) continue;
                if (signal.Kind == PlayerSignalKind.Started)
                {
                    session.PlaybackStarted = true;
                    if (!session.Paused) session.IdleSince = null;
                    continue;
                }
                if (signal.Kind == PlayerSignalKind.Ended && signal.EndReason is not (TrackEndReason.Finished or TrackEndReason.LoadFailed))
                {
                    if (signal.EndReason is TrackEndReason.Stopped or TrackEndReason.Cleanup)
                    {
                        session.Current = null;
                        session.Paused = false;
                        session.PlaybackStarted = false;
                        MarkIdle(session);
                    }
                    continue;
                }
                var completed = session.Current;
                var previous = completed.Entry;
                session.Current = null;
                session.Paused = false;
                session.PlaybackStarted = false;
                MarkIdle(session);
                if (signal.Kind == PlayerSignalKind.Ended && signal.EndReason == TrackEndReason.Finished)
                {
                    if (session.Loop)
                    {
                        try { await StartEntryAsync(session, previous, cancellationToken); continue; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception) { logger.LogWarning(exception, "Loop restart failed in {GuildId}.", session.GuildId); }
                    }
                    Remember(session, previous);
                }
                else
                {
                    await NotifyAsync(session, $"YouTube could not play {MusicReply.TrackLabel(previous)}. Skipping it.");
                }
                await StartNextAsync(session, cancellationToken);
                if (session.Current is null && session.Queue.Count == 0 && session.Autoplay)
                {
                    if (signal.Kind == PlayerSignalKind.Ended && signal.EndReason == TrackEndReason.Finished)
                        BeginAutoplayLookup(session, completed);
                    else
                    {
                        session.Autoplay = false;
                        session.CancelAutoplayLookup();
                        await NotifyAsync(session, "Autoplay was turned off after the playback failure. Use /play to continue.");
                    }
                }
                if (session.Current is { } next) await NotifyAsync(session, $"Now playing {MusicReply.TrackLabel(next.Entry)}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Playback event failed in {GuildId}.", session.GuildId);
                if (session.Current is null || session.Paused) MarkIdle(session);
                if (signal.Kind == PlayerSignalKind.AutoplayReady)
                {
                    session.Autoplay = false;
                    session.CancelAutoplayLookup();
                    await NotifyAsync(session, "Autoplay could not start the next song and was turned off. Use /play to continue.");
                }
            }
            finally { session.Gate.Release(); }
        }
    }

    private void UpdatePresence(GuildSession session)
    {
        if (session.Player is null || client.ConnectionState != ConnectionState.Connected) return;
        var guild = client.GetGuild(session.GuildId);
        if (guild?.CurrentUser is not { } botUser) return;
        var channel = botUser.VoiceChannel;
        if (channel is not null && channel.ConnectedUsers.Any(user => !user.IsBot)) session.EmptySince = null;
        else session.EmptySince ??= Stopwatch.GetTimestamp();
    }

    private async Task MonitorSessionsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            foreach (var session in _sessions.Values)
            {
                // A slow command in one server must not delay the departure timer in another.
                if (!await session.Gate.WaitAsync(0, cancellationToken)) continue;
                try
                {
                    if (session.Player is null) continue;
                    if (session.Player.State == PlayerState.Destroyed) { session.Reset(); continue; }
                    UpdatePresence(session);
                    if (session.Current is null || session.Paused || !session.PlaybackStarted || !session.Player.ConnectionState.IsConnected)
                        MarkIdle(session);
                    else
                        session.IdleSince = null;
                    var empty = session.EmptySince is { } emptySince && Stopwatch.GetElapsedTime(emptySince) >= DepartureDelay;
                    var idle = session.IdleSince is { } idleSince && Stopwatch.GetElapsedTime(idleSince) >= DepartureDelay;
                    if (!empty && !idle) continue;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    await DisconnectCoreAsync(session, deadline.Token);
                    await NotifyAsync(session, empty
                        ? "Disconnected after three minutes without human listeners. The playback session was cleared."
                        : "Disconnected after three minutes without playback. The playback session was cleared.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception exception) { logger.LogWarning(exception, "Player maintenance failed in {GuildId}.", session.GuildId); }
                finally { session.Gate.Release(); }
            }
        }
    }

    private async Task DisconnectCoreAsync(GuildSession session, CancellationToken cancellationToken)
    {
        var player = session.Player;
        session.Reset();
        if (player is not null) await player.DisconnectAsync(cancellationToken);
    }

    private async Task NotifyAsync(GuildSession session, string text)
    {
        if (client.GetChannel(session.TextChannelId) is not IMessageChannel channel) return;
        try
        {
            await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None,
                options: new RequestOptions { Timeout = 5000, RetryMode = RetryMode.AlwaysFail });
        }
        catch (Exception exception) { logger.LogDebug(exception, "Could not send a playback notification in {GuildId}.", session.GuildId); }
    }
}
