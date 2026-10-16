namespace DiscordMusicBot.Music;

internal sealed class GuildSession(ulong guildId)
{
    public ulong GuildId { get; } = guildId;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public MusicPlayer? Player { get; set; }
    public ulong TextChannelId { get; set; }
    public PlaybackItem? Current { get; set; }
    public List<TrackEntry> Queue { get; } = [];
    public List<TrackEntry> History { get; } = [];
    public Queue<string> RecentTrackIds { get; } = [];
    public bool Loop { get; set; }
    public bool Autoplay { get; set; }
    public PlaybackItem? AutoplaySeed { get; set; }
    public CancellationTokenSource? AutoplayCancellation { get; set; }
    public long AutoplayGeneration { get; private set; }
    public bool Paused { get; set; }
    public bool PlaybackStarted { get; set; }
    public TimeSpan PausedPosition { get; set; }
    public long? IdleSince { get; set; }
    public long? EmptySince { get; set; }

    public void Reset()
    {
        Player = null;
        Current = null;
        Queue.Clear();
        History.Clear();
        RecentTrackIds.Clear();
        Loop = false;
        Autoplay = false;
        CancelAutoplayLookup();
        Paused = false;
        PlaybackStarted = false;
        PausedPosition = TimeSpan.Zero;
        IdleSince = null;
        EmptySince = null;
    }

    public void CancelAutoplayLookup()
    {
        AutoplayGeneration++;
        AutoplaySeed = null;
        var cancellation = AutoplayCancellation;
        AutoplayCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }
}
