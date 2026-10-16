using Lavalink4NET.Players;
using Lavalink4NET.Protocol.Payloads.Events;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;

namespace DiscordMusicBot.Music;

public sealed record TrackEntry(LavalinkTrack Track, ulong RequesterId, bool IsAutoplay = false);

// A new item per playback lets delayed events distinguish repeated/duplicate songs.
public sealed class PlaybackItem(TrackEntry entry) : ITrackQueueItem
{
    public TrackEntry Entry { get; } = entry;
    public TrackReference Reference { get; } = new(entry.Track);
}

public enum PlayerSignalKind { Started, Ended, Failed, Disconnected, VoiceChanged, AutoplayReady }

public sealed record PlayerSignal(
    ulong GuildId,
    MusicPlayer? Player,
    PlayerSignalKind Kind,
    PlaybackItem? Item = null,
    TrackEndReason? EndReason = null,
    long AutoplayGeneration = 0,
    IReadOnlyList<LavalinkTrack>? Recommendations = null);

public sealed class MusicPlayer(
    IPlayerProperties<MusicPlayer, LavalinkPlayerOptions> properties,
    Action<PlayerSignal> publish) : LavalinkPlayer(properties)
{
    protected override ValueTask NotifyTrackStartedAsync(ITrackQueueItem track, CancellationToken cancellationToken = default)
        => Publish(PlayerSignalKind.Started, track);

    protected override ValueTask NotifyTrackEndedAsync(ITrackQueueItem track, TrackEndReason endReason, CancellationToken cancellationToken = default)
        => Publish(PlayerSignalKind.Ended, track, endReason);

    protected override ValueTask NotifyTrackExceptionAsync(ITrackQueueItem track, TrackException exception, CancellationToken cancellationToken = default)
        => Publish(PlayerSignalKind.Failed, track);

    protected override ValueTask NotifyTrackStuckAsync(ITrackQueueItem track, TimeSpan threshold, CancellationToken cancellationToken = default)
        => Publish(PlayerSignalKind.Failed, track);

    protected override ValueTask NotifyChannelUpdateAsync(ulong? voiceChannelId, CancellationToken cancellationToken = default)
    {
        if (voiceChannelId is null) publish(new PlayerSignal(GuildId, this, PlayerSignalKind.Disconnected));
        return ValueTask.CompletedTask;
    }

    private ValueTask Publish(PlayerSignalKind kind, ITrackQueueItem track, TrackEndReason? endReason = null)
    {
        publish(new PlayerSignal(GuildId, this, kind, track as PlaybackItem, endReason));
        return ValueTask.CompletedTask;
    }
}
