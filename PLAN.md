# Discord music bot plan

Status: approved and implemented. See README.md for setup and startup instructions. Live playback requires the user's bot account and manual verification.

## Confirmed scope

- Write the bot in C# 14 targeting .NET 10 LTS, the current stable release combination.
- Build and run the bot and Lavalink in Docker, managed together with Docker Compose.
- Use YouTube as the only music source in the initial version.
- Use slash commands, including song queries and YouTube URLs.
- Add opt-in `/autoplay` using video-seeded YouTube mixes when the manual queue runs out. This was requested after the initial deployment; see README.md for behavior and the 1.1.0 upgrade.
- Defer lyrics to a TODO.
- Disconnect after three continuous minutes with no human listeners, even if music is playing or tracks remain queued.
- Also disconnect after three continuous minutes without active playback, regardless of whether human listeners are present.
- Do not create automated tests or a test project. The user will test the application manually.

## Versions, requirements, and client recommendation

Research checked on October 14, 2026:

- The current Lavalink server release is 4.2.2.
- Running its standalone JAR requires Java 17 or newer. The official Docker image supplies Java, so no host Java installation is needed.
- Lavalink runs as a separate service; it does not require a particular C# language version.
- Lavalink exposes REST and WebSocket APIs, so a wrapper is optional.
- A wrapper is recommended to simplify Discord voice event forwarding, playback commands, player events, and connection handling.
- YouTube playback requires the maintained youtube-source plugin; the old built-in YouTube source is deprecated.
- The server and client must support Discord's DAVE voice encryption.

Lavalink is the audio server. Victoria and Lavalink4NET are alternative C# clients that communicate with that server; neither replaces Lavalink.

Two established C# wrappers were reviewed:

| Wrapper | Published requirements | Assessment |
| --- | --- | --- |
| Lavalink4NET.Discord.NET 4.2.2 | Integration targets .NET 6, 7, and 8 and is compatible with newer targets. Its core package also targets .NET 9 and 10. Depends on Discord.Net.WebSocket >= 3.18.0 and Lavalink4NET >= 4.2.2. | Recommended. Lavalink's official client list confirms DAVE support. Includes queue/player abstractions and reconnect/resume features. |
| Victoria 7.0.6 | Targets .NET 8 or newer; depends on Discord.Net.WebSocket >= 3.19.0, Microsoft.Extensions.DependencyInjection.Abstractions >= 10.0.4, and Microsoft.Extensions.Logging.Abstractions >= 10.0.4. Its README describes Lavalink v4 support. | This is the existing wrapper the user has used before. .NET 10 meets its published framework requirements. Current DAVE compatibility was not established from the available documentation; attempts to inspect current source were unavailable. This is an uncertainty, not evidence that Victoria lacks DAVE support. |

Approved stack: Discord.Net + Lavalink4NET.Discord.NET + Lavalink + youtube-source. Use the existing published wrapper package; do not build our own wrapper.

| Component | Selected version or approach |
| --- | --- |
| Bot runtime | .NET 10 LTS, target framework `net10.0` |
| C# language | C# 14, using the stable SDK defaults |
| Discord library | Discord.Net 3.20.2 |
| Recommended Lavalink client | Lavalink4NET.Discord.NET 4.2.2 |
| Application hosting library | Microsoft.Extensions.Hosting 10.0.13 |
| Audio server | Lavalink 4.2.2, official Docker image |
| YouTube plugin | Pinned upstream snapshot `2be8e542d3f6f178e048dca565892684c2e40177`, containing TV and IOS fixes needed after stable 1.18.2 playback failures |
| YouTube signature deciphering | Private yt-cipher container, published image pinned by digest; required after confirmed built-in parser failures |
| Deployment | Three application containers managed with Docker Compose, plus the short plugin volume ownership helper |

Pin package versions and image versions/digests in the implementation. Use the current serviced .NET 10 SDK/runtime images rather than preview releases. The server and wrapper happen to share the version number 4.2.2; they are separately versioned projects.

## Why .NET 10 instead of .NET 9

- .NET 9 would satisfy the reviewed wrapper framework requirements, but its support ends in November 2026. .NET 10 LTS is supported until November 2028.
- .NET 10 includes runtime optimization improvements such as better JIT code generation and additional stack allocation. The benefits depend on the workload; there is no measured performance claim for this bot.
- C# 14 adds language features such as extension members, field-backed properties, and null-conditional assignment. None is essential to the requested commands.
- Lavalink performs audio processing and streaming separately from the C# command application. Therefore, a large audible playback improvement from changing .NET alone is not expected; this is an architectural inference, not a benchmark result.
- The principal reasons for selecting .NET 10 are its support lifetime and the user's preference for the current stable version when using Docker.

## Docker deployment

- Preferred server workflow: export a Linux/amd64 bot image locally with the .NET SDK, copy the minimal deployment archive to Ubuntu, load the bot image, and start the image-based Compose configuration. The server pulls the official Lavalink image and the published yt-cipher image; no bot source or build SDK is needed there. See deploy/README.md.
- Use a multi-stage bot Dockerfile: the .NET 10 SDK image restores and publishes the C# project, and a .NET 10 runtime image runs the published application.
- Run Lavalink in a separate container using its official image, with the YouTube plugin version configured for automatic download.
- Manage the bot, Lavalink, and private cipher service with one Compose file, startup ordering/readiness handling, and restart policies.
- The bot connects to Lavalink through its Compose service hostname, such as `http://lavalink:2333`, on the shared Docker network.
- Keep the Lavalink API reachable by the bot through the container network; publishing that port to the host is unnecessary for normal operation.
- Configure the Discord token and Lavalink password through runtime secrets or local environment configuration. Provide an example configuration without credentials.
- Use Linux containers. Local Windows development needs Docker Desktop with Linux container support; a Linux server needs Docker Engine and Docker Compose.
- No host .NET 10 SDK/runtime or Java installation is required when both build and execution happen inside containers. The PC's existing .NET 9 installation does not control the container's .NET version.
- Planned startup command: `docker compose up --build -d`.


## Commands

| Command | Behavior |
| --- | --- |
| `/play query` | Search YouTube or load a YouTube video/playlist URL. Automatically join the caller's voice channel when needed. Start playback when idle; otherwise enqueue. Announce the selected track. |
| `/skip` | Advance to the next queued track. Manual skipping overrides repetition of the current track. With no next track, stop playback and start the three-minute playback inactivity timer. |
| `/help` | List available commands with descriptions and examples. |
| `/nowplaying` | Show title, source link, requester, position, duration, playback state, and loop status. |
| `/clearqueue` | Remove upcoming tracks while leaving the current track playing. |
| `/join` | Join the caller's voice channel. Start the three-minute playback inactivity timer if nothing is playing. |
| `/leave` | Stop playback, clear the playback session, and disconnect immediately. |
| `/loop [on/off]` | Enable or disable repetition of the current song; toggle if the option is omitted. |
| `/autoplay [mode]` | Toggle related-song selection from YouTube mixes when the queue ends; manual queue and looping take priority. Stop and departure disable it. |
| `/pause` | Pause the current track and start the three-minute playback inactivity timer. |
| `/previous` | Play the previous track from session history and put the interrupted current track at the front of the upcoming queue. Explain when there is no previous track. |
| `/queue` | Show upcoming tracks with numbering starting at 1 and pagination. Show the current track separately. |
| `/remove number` | Remove the upcoming track at the displayed queue position. |
| `/resume` | Resume paused playback. |
| `/seek position` | Seek to an absolute time such as `1:30` or `1:02:30`. Reject invalid positions and non-seekable streams with a clear message. |
| `/shuffle` | Shuffle upcoming tracks while preserving the current song. |
| `/stop` | Stop playback, clear upcoming tracks, and disable loop and autoplay. Start the three-minute playback inactivity timer. |

Examples:

```text
/play query:Daft Punk One More Time
/play query:https://www.youtube.com/watch?v=...
/remove number:3
/seek position:1:30
/loop mode:On
```

TODO: `/lyrics` for the currently playing song. Do not implement a lyrics provider in the initial version.

Keep the requested command set. No additional commands are included in the initial version.

## Playback and voice behavior

- Keep a separate player, upcoming queue, and bounded history per Discord server.
- Keep session state in memory initially; restarting the bot resets queues and history.
- Automatically join for `/play`. Playback controls without an active track should explain the situation rather than create an empty player.
- Require users changing playback or the queue to be in the bot's voice channel.
- Allow informational commands such as help, queue, and nowplaying without requiring a voice connection.
- Do not move an active bot to another voice channel implicitly; explain which channel it is serving.
- Maintain two independent departure conditions. Either condition reaching three continuous minutes ends the session.
- Listener absence: when the last human leaves, start a three-minute timer. Other bots do not count as listeners. Cancel this timer if a human rejoins; a later departure starts a fresh three-minute timer. This condition applies even while a song is playing or the queue contains tracks.
- Playback inactivity: start a three-minute timer when connected without active playback, including after joining without playing, queue exhaustion, stopping, or pausing. For this plan, paused time counts as inactivity. Cancel this timer when playback starts or resumes. Human presence does not cancel it.
- Playback inactivity means the player is idle or paused; silence within an actively playing track does not require audio analysis or trigger the timer.
- Adding queued tracks or running informational commands does not reset the playback inactivity timer unless playback actually starts.
- On either timeout, stop playback, clear the playback session, and disconnect. Cancel both timers when leaving or disposing the session. Prevent an expired timer from disconnecting a newer session or one whose relevant condition has already changed.
- Handle track completion, skipping, looping, previous, and simultaneous commands consistently so tracks are not started twice or lost.
- Report unavailable YouTube tracks and playback failures clearly; continue to the next playable queued track without infinite retries.

## Implementation sequence after plan review

1. Use the approved Lavalink4NET wrapper with .NET 10 LTS and C# 14.
2. Create the bot project, local configuration, logging, and slash command registration.
3. Add the bot Dockerfile, Docker Compose services, Lavalink configuration, and YouTube plugin configuration.
4. Implement joining, YouTube search/link loading, playback, and queue management.
5. Implement pause/resume, seeking, looping, previous, and the remaining commands.
6. Implement both three-minute departure conditions and failure handling.
7. Build the container images and provide Compose startup instructions for the user's manual testing. Do not create tests or a test project.

Store the Discord bot token in runtime secrets or a local ignored environment file; it does not need to be shared in chat or committed to source control. Supply the Lavalink password to both containers using matching runtime configuration.

## Sources

- [Lavalink standalone requirements](https://lavalink.dev/getting-started/binary)
- [Lavalink Docker deployment](https://lavalink.dev/getting-started/docker)
- [Lavalink 4.2.2 release](https://github.com/lavalink-devs/Lavalink/releases/tag/4.2.2)
- [Lavalink client list and DAVE support](https://lavalink.dev/clients)
- [Lavalink API integration requirements](https://lavalink.dev/api/)
- [Lavalink4NET project](https://github.com/lavalink4net/Lavalink4NET)
- [Lavalink4NET Discord.Net package requirements](https://www.nuget.org/packages/Lavalink4NET.Discord.NET/4.2.2)
- [Victoria project](https://github.com/Yucked/Victoria)
- [Victoria package requirements](https://www.nuget.org/packages/Victoria/7.0.6)
- [Maintained YouTube source plugin](https://github.com/lavalink-devs/youtube-source)
- [YouTube plugin 1.18.2 release](https://github.com/lavalink-devs/youtube-source/releases/tag/1.18.2)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [C# 14 release features](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14/)
- [.NET 10 runtime improvements](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime)
- [Microsoft .NET container build documentation](https://learn.microsoft.com/en-us/dotnet/core/docker/build-container)
- [Discord.Net releases, including 3.20.2](https://github.com/discord-net/Discord.Net/releases)
