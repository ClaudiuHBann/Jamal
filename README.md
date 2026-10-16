# Jamal

A C# 14 / .NET 10 Discord bot using Discord.Net and Lavalink4NET. YouTube is the only music source. Deploy the prebuilt Linux image bundle, or build from source with Docker Compose. Both approaches run the bot alongside Lavalink for audio streaming and yt-cipher for YouTube player signature deciphering.

## What you need

- **For building/running locally with Docker on Windows:** [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/) running in Linux container mode. Exporting the prebuilt image with the SDK does not require local Docker.
- **On a Linux server:** [Docker Engine](https://docs.docker.com/engine/install/) and the [Compose plugin](https://docs.docker.com/compose/install/linux/).
- A Discord application with a bot account, and a server where you can invite it.

Building and running through Docker requires no host .NET or Java installation. Your PC's existing .NET 9 installation can stay as it is. The .NET 10 SDK is used inside the build container; the runtime and Java are included in the running images.

## Create the Discord bot

1. Open the [Discord Developer Portal](https://discord.com/developers/applications), create an application, and open its **Bot** settings.
2. Create or reset its bot token. Keep this token private and place it in the local `.env` file described below.
3. Invite the application to your server with the **bot** and **applications.commands** scopes. Grant **View Channels**, **Send Messages**, **Embed Links**, **Connect**, and **Speak**. Administrator is unnecessary. Channel-specific overrides must also allow these permissions.
4. For prompt command registration while developing, enable Discord's Developer Mode, right-click your server, and copy its server ID. Put this in `DISCORD_GUILD_ID`.

The bot uses the Guilds and Guild Voice States gateway intents. Message Content, Server Members, and Presence privileged intents are unnecessary.

Use a dedicated application for this bot: startup replaces the registered command list in the configured server or global scope.

## Configure and start

For deployment without copying source code or building on the server, use the prebuilt image bundle described in [deploy/README.md](deploy/README.md). The following instructions are for building from this repository with Docker.

Open a terminal in this repository. In PowerShell:

```powershell
Copy-Item .env.example .env
```

On Linux:

```sh
cp .env.example .env
```

Edit `.env` locally:

```dotenv
DISCORD_TOKEN='paste-your-bot-token-here'
DISCORD_GUILD_ID=your-server-id
LAVALINK_PASSWORD='paste-a-long-random-password-here'
```

The server ID is optional. Leave it blank to register commands globally; Discord may take time to display global command changes. Single quotes in the environment file keep special characters literal. The real `.env` file is excluded from Git and the Docker build context.

You can generate a random Lavalink password in PowerShell:

```powershell
[Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Start the services:

```sh
docker compose up --build -d
```

Compose downloads the images, builds the C# application, and starts the private cipher service and Lavalink. Lavalink downloads the configured YouTube plugin automatically. The bot waits for Lavalink to become ready, connects to Discord, and registers its slash commands. Look for `Slash commands registered` in the bot logs.

```sh
docker compose logs -f bot lavalink
```

Join a regular voice channel and run:

```text
/play query:Daft Punk One More Time
```

The first startup needs Internet access to download images, NuGet packages, and the YouTube plugin. Playback needs outbound access to Discord and YouTube, including Discord voice UDP traffic. No incoming port needs to be opened for this bot.

## Commands

| Command | Behavior |
| --- | --- |
| `/play query` | Search YouTube or add a YouTube video/playlist URL. Automatically joins your channel and starts playback when idle. Otherwise adds to the queue. |
| `/skip` | Advance to the next song, including when looping. |
| `/previous` | Play the previous song and place the interrupted song first in the upcoming queue. |
| `/pause` | Pause playback. Paused time counts toward the inactivity timeout. |
| `/resume` | Resume paused playback. |
| `/stop` | Stop playback, clear upcoming tracks, and disable loop and autoplay. |
| `/loop [mode]` | Toggle current-song repetition, or select `On` or `Off`. Manual skip always advances. |
| `/autoplay [mode]` | Toggle related songs from YouTube mixes when the manual queue ends. Select `On` or `Off`, or omit the mode to toggle. |
| `/seek position` | Jump to seconds, `m:ss`, or `h:mm:ss`. Live streams cannot seek. |
| `/nowplaying` | Show the current track, requester, position, playback state, loop, and autoplay status. |
| `/queue [page]` | Show numbered upcoming tracks, ten per page. The current track is listed separately. |
| `/remove number` | Remove an upcoming song using its current queue number, starting at 1. |
| `/clearqueue` | Clear upcoming songs while keeping the current song. |
| `/shuffle` | Shuffle upcoming songs while keeping the current song. |
| `/join` | Join your current voice channel. |
| `/leave` | Clear the entire playback session and disconnect immediately. |
| `/help` | Show all commands and usage information. |

Examples:

```text
/play query:https://www.youtube.com/watch?v=VIDEO_ID
/play query:https://www.youtube.com/playlist?list=PLAYLIST_ID
/queue page:2
/remove number:3
/seek position:1:30
/loop mode:On
/autoplay mode:On
```

Read-only commands work without joining a voice channel. Playback and queue changes require you to be in the bot's voice channel. An active session is never moved to another channel by `/play` or `/join`. Stage channels are not supported.

## Related songs and YouTube mixes

Start a song, then use `/autoplay mode:On` (or `/autoplay` to toggle). When the current song finishes and the upcoming queue is empty, the bot asks YouTube for a mix seeded by that video and plays the first suitable recommendation. It requests a new mix after each song, so playback continues without manually selecting more music. `/skip` also chooses a recommendation when there is no queued song. Manually queued songs always play first, and `/loop` takes priority on natural completion.

Autoplay starts off for each session. `/autoplay mode:Off` stops choosing recommendations while keeping the current song and manual queue. `/stop`, `/leave`, and automatic departure turn autoplay off and cancel any pending lookup. `/clearqueue` keeps autoplay enabled. Recommendations exclude the seed and the last 100 attempted tracks, duplicate video IDs, and live streams. A failed lookup, no fresh recommendations, or a playback failure with no queued song turns autoplay off and sends a message; it does not retry indefinitely.

Mix requests run in the background with a 20-second timeout. Commands and departure timers continue to run while the bot looks for a recommendation. `/queue` shows the lookup state; `/nowplaying` labels automatically chosen songs and displays autoplay status. A short gap can occur between songs while the mix is retrieved.

The [YouTube plugin supports mix loading](https://github.com/lavalink-devs/youtube-source/blob/2be8e542d3f6f178e048dca565892684c2e40177/common/src/main/java/dev/lavalink/youtube/clients/skeleton/NonMusicClient.java). This uses the mix associated with a video through its metadata clients; the result can differ from your personal YouTube recommendations. The saved TV OAuth token remains responsible for playback. You can also paste a YouTube Mix URL into `/play` to load its returned songs into the manual queue, for example `https://www.youtube.com/watch?v=VIDEO_ID&list=RDVIDEO_ID` with the real video ID substituted in both places.

## Automatic departure and session state

Each server has its own player, queue, and history. The bot disconnects when **either** of these conditions lasts for three continuous minutes:

- No human listeners are in its voice channel. Other bots do not count. A human returning cancels this timer, even if songs are still playing or queued.
- No active playback. This includes joining without playing, an empty queue, stopped playback, paused playback, or a disconnected audio connection. Starting or resuming playback cancels this timer. Human presence does not cancel it.

Silence within a playing song is ordinary playback. Adding queued songs or running informational commands does not reset an inactivity timer unless playback starts. Automatic departure, `/leave`, and a bot restart clear the session. `/stop` keeps recent history until departure so `/previous` can still replay a song.

Upcoming queues hold up to 500 songs. A playlist request can contain at most 500 tracks. The previous-song history retains the latest 50 tracks. Queue and history are kept in memory and are not persisted across container restarts. Loop applies to the current song; the setting stays enabled when a manual skip or previous command changes the current song until you turn it off or stop the session. Failed tracks are skipped without retrying them indefinitely.

Lyrics is deferred in [TODO.md](TODO.md).

## Day-to-day Docker commands

```sh
# Show running services
docker compose ps

# Follow application logs
docker compose logs -f bot

# Follow audio/source logs
docker compose logs -f lavalink

# Restart the bot after editing .env; this recreates it with the new environment
docker compose up -d --force-recreate bot

# Rebuild after a source change
docker compose up --build -d bot

# Stop the services
docker compose down
```

Lavalink's downloaded plugins live in a named Docker volume and remain available after normal shutdown. Before Lavalink starts, the `lavalink-plugins-init` service sets this volume's ownership to the image's `lavalink` user, then exits. This also repairs an existing volume with incorrect ownership. The audio API is accessible to the bot on the Compose network as `http://lavalink:2333`; it is not published on the host. The cipher API is private at `http://yt-cipher:8001` and uses the existing Lavalink password. All three application services restart automatically unless explicitly stopped.

## Troubleshooting

- **Docker command is missing:** install/start Docker Desktop locally or Docker Engine with Compose on the server. Select Linux containers on Windows.
- **Compose reports an unset variable:** fill in `DISCORD_TOKEN` and `LAVALINK_PASSWORD` in `.env` in the repository root.
- **Bot cannot connect to Lavalink:** inspect `docker compose logs lavalink`. Both services must use the same Lavalink password. The bot allows three minutes for initial startup and plugin download, then exits so Compose can restart it.
- **Lavalink cannot download its plugin (`Permission denied`):** run `docker compose up -d` with the current Compose file so `lavalink-plugins-init` fixes the plugin volume's ownership. For an older deployment, stop the bot and Lavalink, run `docker compose run --rm --no-deps --user 0:0 --entrypoint /bin/sh lavalink -ec 'chown -R lavalink:lavalink /opt/Lavalink/plugins && chmod u+rwx /opt/Lavalink/plugins'`, then run `docker compose up -d`. The application continues to run as its normal non-root user.
- **Slash commands do not appear:** confirm the application is invited with `applications.commands`. Use the correct test server ID for immediate server-scoped registration. Global command updates can take time. Check bot logs for registration errors.
- **Autoplay appears in help but not in Discord's command picker:** help text confirms the updated handler is running, but does not confirm command visibility in Discord. Check the registered commands, their scope, and command permissions. Set `DISCORD_GUILD_ID` to this server's ID and recreate the bot for immediate server-scoped registration. Refresh the Discord client after registration. `/autoplay` remains a native slash command.
- **Bot joins but audio is unavailable:** check Connect/Speak permissions, server mute state, outbound UDP/firewall settings, and Lavalink logs.
- **Bot self-mutes immediately after joining:** image `1.0.1` works around a mute/deafen argument-order mismatch between Lavalink4NET 4.2.2 and Discord.Net 3.20.2. The app explicitly disables both flags when joining. Load the updated image, set `BOT_IMAGE_TAG=1.0.1` in `.env`, and recreate the bot container.
- **YouTube search works but a song fails:** videos can be private, restricted, removed, or temporarily blocked by YouTube. Try another video. YouTube integration can require plugin/client updates as the upstream service changes; inspect the [plugin documentation](https://github.com/lavalink-devs/youtube-source) before changing its client configuration.
- **Multiple songs fail with `This video requires login`:** the optional [YouTube sign-in guide](deploy/YOUTUBE.md) configures the plugin's authenticated `TV` client. Complete Google device authorization yourself and save the resulting refresh token in the server's `.env`. Authentication may help but does not guarantee that YouTube will permit playback.
- **TV fails with `The page needs to be reloaded`:** update `compose.yml`, `lavalink/application.yml`, and `compose.youtube-oauth.yml` together using the current deployment configuration. It pins a newer upstream YouTube plugin with TV and IOS fixes, prioritizes WEB for searches, and TV for authenticated playback. See the [playback guide](deploy/YOUTUBE.md).
- **TV fails with `Must find sig function`:** the built-in YouTube signature parser cannot process the current player script. Use the current Compose and Lavalink configuration together so the private yt-cipher service handles deciphering. See the [cipher deployment guide](deploy/YOUTUBE.md).
- **Queue disappears:** departures and bot restarts intentionally end the in-memory session.

## Versions and maintenance

To export a prebuilt Linux/amd64 image and a minimal production bundle on this PC, run this with PowerShell 7.4 or newer and the .NET 10 SDK:

```powershell
./scripts/Publish-Deployment.ps1
```

The script also detects the temporary workspace-local SDK when available. The .NET SDK creates the Docker-loadable image archive without requiring a local Docker installation. Output is `.artifacts/deployment/discord-music-bot-linux-x64.tar.gz`. The bundle includes the current `.env` as private runtime configuration, so transfer it privately. The image contains the published app and .NET runtime, with no embedded Discord token. [Microsoft SDK container export documentation](https://learn.microsoft.com/en-us/dotnet/core/containers/sdk-publish)

Component versions checked on October 14, 2026:

| Component | Version |
| --- | --- |
| .NET / C# | .NET 10 LTS / C# 14 |
| Discord.Net | 3.20.2 |
| Lavalink4NET.Discord.NET | 4.2.2 |
| Microsoft.Extensions.Hosting | 10.0.13 |
| Lavalink | 4.2.2 |
| youtube-source plugin | Pinned upstream snapshot `2be8e542d3f6f178e048dca565892684c2e40177` |
| yt-cipher | Published `master` image, digest pinned in Compose |

NuGet package versions and transitive dependencies are pinned in the project and `packages.lock.json`. Docker restore uses locked mode. The official .NET `10.0` images track serviced patches in that stable release; the Lavalink image and plugin use explicit versions. Rebuilding with current base images can be done with `docker compose build --pull` followed by `docker compose up -d`.

The YouTube plugin is the exception to the stable-release selection: stable 1.18.2 fails on the deployed server, and the pinned upstream snapshot includes newer TV and IOS client fixes. See [upstream issue 230](https://github.com/lavalink-devs/youtube-source/issues/230). Bot image 1.1.2 includes native slash-command autoplay from YouTube mixes and the self-mute fix. Deployment configuration includes the private cipher service to handle the confirmed built-in signature parser failure.

For package upgrades, update the project references and regenerate the lock file with the .NET 10 SDK (which can also run inside Docker). Check Lavalink/client DAVE compatibility before upgrading audio components.

No test project or automated playback tests are included. Build verification does not establish live Discord/YouTube playback; that requires your configured bot account and manual use.
