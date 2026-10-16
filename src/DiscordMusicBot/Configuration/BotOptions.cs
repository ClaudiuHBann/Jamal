using Microsoft.Extensions.Configuration;

namespace DiscordMusicBot.Configuration;

public sealed record BotOptions(string Token, ulong? GuildId, Uri LavalinkAddress, string LavalinkPassword)
{
    public static BotOptions Load(IConfiguration configuration)
    {
        var token = configuration["Discord:Token"]?.Trim();
        var password = configuration["Lavalink:Passphrase"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Set Discord__Token and Lavalink__Passphrase (or the values in .env when using Compose).");
        }

        var address = configuration["Lavalink:BaseAddress"] ?? "http://localhost:2333";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Lavalink__BaseAddress must be an absolute HTTP or HTTPS address.");
        }

        ulong? guildId = null;
        var guildValue = configuration["Discord:GuildId"];
        if (!string.IsNullOrWhiteSpace(guildValue))
        {
            if (!ulong.TryParse(guildValue, out var parsedId) || parsedId == 0)
            {
                throw new InvalidOperationException("Discord__GuildId must be a Discord server ID, or empty for global commands.");
            }
            guildId = parsedId;
        }

        return new BotOptions(token, guildId, uri, password!);
    }
}
