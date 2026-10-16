using System.Globalization;
using System.Text;
using Discord;

namespace DiscordMusicBot.Music;

public sealed record MusicReply(string? Text = null, Embed? Embed = null)
{
    public static string Duration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    public static string Safe(string text, int limit = 160)
    {
        var builder = new StringBuilder();
        foreach (var character in text.Take(limit))
        {
            if (character is '*' or '_' or '`' or '~' or '[' or ']' or '(' or ')' or '\\' or '|' or '>')
                builder.Append('\\');
            if (!char.IsControl(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    public static string TrackLabel(TrackEntry entry, int titleLimit = 160)
        => entry.Track.Uri is { } uri
            ? $"[{Safe(entry.Track.Title, titleLimit)}]({uri.AbsoluteUri})"
            : Safe(entry.Track.Title, titleLimit);

    public static TimeSpan ParsePosition(string value)
    {
        var parts = value.Trim().Split(':');
        if (parts.Length is < 1 or > 3 || parts.Any(part => part.Length == 0 || part.Any(character => character is < '0' or > '9')))
            throw new MusicException("Use a position such as 90, 1:30, or 1:02:30.");
        long seconds = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 359999)
                throw new MusicException("That position is too large.");
            if (index > 0 && number >= 60)
                throw new MusicException("Minutes and seconds after a colon must be between 00 and 59.");
            seconds = seconds * 60 + number;
        }
        return TimeSpan.FromSeconds(seconds);
    }
}
