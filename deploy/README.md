# Ubuntu deployment from a prebuilt image

This bundle is for an Ubuntu **amd64/x86_64** server. It contains the bot's Linux image and its runtime configuration. The server does not build C# code or download a .NET SDK. Docker Compose downloads the official Lavalink image and the published yt-cipher image on its first run, and Lavalink downloads the YouTube plugin. The cipher service handles YouTube player signature changes that the plugin's built-in parser cannot process.

Docker Engine must be running before these commands will work. A prebuilt image does not resolve a Docker service startup failure.

## Copy and start

Copy `discord-music-bot-linux-x64.tar.gz` to the server using SCP, SFTP, or your usual file transfer tool. Then run:

```sh
tar -xzf discord-music-bot-linux-x64.tar.gz
cd discord-music-bot-linux-x64
sha256sum -c SHA256SUMS
sudo docker load -i discord-music-bot-image.tar.gz
sudo docker compose up -d
sudo docker compose logs -f bot lavalink yt-cipher
```

The bundle contains `.env` when the production environment file was available during packaging. Keep the bundle private: it includes the Discord token and Lavalink password. These credentials are supplied at runtime and are not embedded in the bot image. If only `.env.example` is present, copy it to `.env` and fill in the values before starting. If the token has been reset in Discord, update `.env` to match.

The `.env` file can optionally specify `DISCORD_GUILD_ID` for server-scoped commands. Leaving it empty uses global command registration, which can take time to appear in Discord.

## Operation

```sh
# Service state
sudo docker compose ps

# Follow logs
sudo docker compose logs -f bot lavalink yt-cipher

# Recreate the bot after changing .env
sudo docker compose up -d --force-recreate bot

# Stop the deployment
sudo docker compose down
```

Queues and history are in memory and reset when the bot restarts. Docker logs rotate at 10 MB per file with three files per application service. Lavalink plugins persist in a named volume. The short `lavalink-plugins-init` service sets this volume's ownership before Lavalink starts, then exits successfully. Seeing this helper exit with code 0 is expected. The Lavalink and cipher APIs are reachable only through the container network; no incoming port is required. The cipher API reuses the existing Lavalink password and keeps its player cache in its writable temporary directory.

For an upgrade, load the new bot image and use the matching bundle configuration, then run `sudo docker compose up -d --force-recreate bot`. Keep your current credentials when replacing the configuration.

## Upgrade to autoplay (1.1.2)

Copy the new `discord-music-bot-image.tar.gz` into your existing deployment directory and run:

```sh
sudo docker load -i discord-music-bot-image.tar.gz &&
sed -i '/^BOT_IMAGE_TAG=/d' .env &&
printf '\nBOT_IMAGE_TAG=1.1.2\n' >> .env &&
sudo docker compose up -d --no-deps --force-recreate bot
```

This uses the working Lavalink, cipher, and OAuth configuration already on the server. Bot startup registers `/autoplay` alongside the other slash commands. Server-scoped commands should appear promptly; global commands may take time to update. Restarting the bot clears the current queue and playback session. Image `1.1.2` removes the unwanted mention-message workaround and keeps native slash-command autoplay.

Play a song, then enable `/autoplay mode:On`. The bot chooses related songs from YouTube mixes whenever the manual queue is empty, including after `/skip`. `/autoplay mode:Off` keeps the current song playing. `/stop` disables autoplay, and `/loop` takes priority on natural song completion. Autoplay is off after a restart or departure. The three-minute departure rules still apply. Mix availability and recommendations need manual verification on the server.

If `/autoplay` is in help but missing from Discord's command picker, set `DISCORD_GUILD_ID` to the server ID in `.env`, recreate the bot, and check the registration log. Server-scoped commands are available immediately. Existing image 1.1.0 already supports `/autoplay`; correcting registration does not require an image upgrade.

Image `1.0.1` fixes self-muting on join caused by a mute/deafen argument-order mismatch in the Discord client integration. To update an existing `1.0.0` installation, copy only `discord-music-bot-image.tar.gz` into its deployment directory, then run:

```sh
sudo docker load -i discord-music-bot-image.tar.gz
sed -i 's/^BOT_IMAGE_TAG=.*/BOT_IMAGE_TAG=1.0.1/' .env
sudo docker compose up -d --no-deps --force-recreate bot
sudo docker compose logs --tail=60 -f bot
```

## Repair plugin permissions in an older deployment

If Lavalink reports `./plugins/youtube-plugin-1.18.2.jar (Permission denied)`, run these commands from the extracted deployment directory:

```sh
sudo docker compose stop bot lavalink
sudo docker compose run --rm --no-deps --user 0:0 --entrypoint /bin/sh lavalink -ec 'chown -R lavalink:lavalink /opt/Lavalink/plugins && chmod u+rwx /opt/Lavalink/plugins'
sudo docker compose up -d
sudo docker compose logs --tail=80 -f bot lavalink
```

This repairs only the plugin volume's ownership. Lavalink itself keeps running as the image's non-root `lavalink` user. No image rebuild or plugin deletion is needed. The current Compose file performs the same repair automatically on startup.

## YouTube requires login

If several songs fail with `This video requires login`, follow [YOUTUBE.md](YOUTUBE.md). The optional `compose.youtube-oauth.yml` override enables the plugin's authenticated `TV` client. You complete the Google device sign-in and keep its refresh token in the server's `.env`. The existing bot image is sufficient. When packaging with a saved refresh token, the production bundle automatically enables this override through `COMPOSE_FILE` in its `.env`. Otherwise, the default configuration uses unauthenticated clients until you enable the override.

If TV instead fails with `The page needs to be reloaded`, use the current main Compose file, Lavalink configuration, and OAuth override together. They pin upstream build `2be8e542d3f6f178e048dca565892684c2e40177` with TV and IOS fixes, prioritize WEB for searches, and TV for authenticated playback. Start the cipher service and recreate Lavalink and the bot after replacing those three files; keep your current `.env`. See [YOUTUBE.md](YOUTUBE.md) for details.

If TV fails with `Must find sig function`, use the current `compose.yml` and `lavalink/application.yml` together to enable the private yt-cipher service. Existing deployments can apply the small `youtube-cipher-fix.tar.gz` patch without replacing their bot image or `.env`. Follow the commands in [YOUTUBE.md](YOUTUBE.md).
