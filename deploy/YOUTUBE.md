# YouTube playback failures and sign-in

## Repair `Must find sig function` playback failures

If search succeeds but TV playback reports `LocalSignatureCipherManager` and `Must find sig function`, the plugin cannot decipher the current YouTube player script. Changing the song or repeating Google authorization does not repair this parser. The [plugin maintainer recommends a remote cipher server](https://github.com/lavalink-devs/youtube-source/issues/225), and the plugin supports [yt-cipher](https://github.com/kikkia/yt-cipher).

The current Compose configuration runs yt-cipher privately alongside Lavalink. Its API uses the existing Lavalink password, publishes no host port, and requires no host runtime installation. The published multi-platform image is pinned by digest. It uses one worker, a smaller in-memory solver cache, and a writable temporary cache directory. The `IAS` player variant follows the service's recommendation.

For an existing Ubuntu deployment, copy `youtube-cipher-fix.tar.gz` into `~/discord-bot/discord-music-bot-linux-x64` and run:

```sh
cd ~/discord-bot/discord-music-bot-linux-x64 &&
tar -xzf youtube-cipher-fix.tar.gz &&
sha256sum -c YOUTUBE-CIPHER-SHA256SUMS &&
sudo docker compose pull yt-cipher &&
sudo docker compose up -d --force-recreate yt-cipher lavalink bot
```

The patch contains Compose/Lavalink configuration and deployment documentation. Keep your existing `.env`, saved refresh token, and bot image. Docker downloads approximately 47 MB of compressed cipher image layers on first use. Wait for the bot to reconnect, then try `/play` with an artist and song name. This addresses signature deciphering; direct URL metadata can still be rejected by YouTube's unauthenticated clients.

If playback still fails, inspect the new cipher and Lavalink error:

```sh
sudo docker compose logs --since=5m --tail=160 yt-cipher lavalink |
sed -E '/Token retrieved successfully|token is |integration with token:|authorization header with value|refresh.?token|access.?token|OAUTH INTEGRATION/Id'
```

The former `Must find sig function` error should disappear when Lavalink uses `RemoteCipherManager`. A cipher timeout, HTTP error, or stream rejection is a different failure and needs its new message. Live playback must be verified on the server; packaging and configuration validation do not confirm that YouTube will accept a stream.

## Update the YouTube clients

The stable plugin 1.18.2 has a [reported TV failure](https://github.com/lavalink-devs/youtube-source/issues/226) that returns `The page needs to be reloaded`, including with OAuth configured. Later upstream changes update the TV user agent and [fix the IOS client](https://github.com/lavalink-devs/youtube-source/issues/230). This deployment pins the published upstream build `2be8e542d3f6f178e048dca565892684c2e40177`, which includes those changes. This is a snapshot build selected to address playback failures in the stable release; it is pinned rather than following a moving branch.

WEB is first for search and metadata. The OAuth override puts TV first for authenticated playback; TV cannot load metadata, so the plugin skips it for lookups. IOS remains a playback fallback. Putting IOS first can return an empty search response that prevents other clients from being tried.

For an existing deployment, copy the current `compose.yml`, `lavalink/application.yml`, and `compose.youtube-oauth.yml` to their matching locations. The current Lavalink configuration requires the cipher service from the matching Compose file. Then run:

```sh
sudo docker compose pull yt-cipher &&
sudo docker compose up -d --force-recreate yt-cipher lavalink bot
```

Lavalink downloads the configured build and removes the superseded plugin automatically. Keep your existing `.env` and saved refresh token. No bot image rebuild is needed. The updated clients do not guarantee that YouTube will allow every server or video; inspect any new playback error before changing other components.

If the pinned build is already installed but `diagnose-youtube.py` shows `Clients: IOS,TV,...` and searches return `empty`, update only the client order on Ubuntu:

```sh
cd ~/discord-bot/discord-music-bot-linux-x64 &&
sed -i 's/^\([[:space:]]*PLUGINS_YOUTUBE_CLIENTS:\).*/\1 TV,WEB,MUSIC,IOS,ANDROID_VR,WEBEMBEDDED/' compose.youtube-oauth.yml &&
sudo docker compose up -d --force-recreate lavalink bot
```

Wait for the bot to reconnect and try `/play` with an artist and song name. A direct URL can still fail if YouTube refuses metadata requests: the TV token only authenticates streaming, not video metadata or searches. If playback then fails, collect the new playback exception; the earlier URL lookup error does not show whether TV streaming works.

## When YouTube asks for sign-in

If searches work but multiple songs fail with `This video requires login`, YouTube is refusing unauthenticated playback requests. The logs alone do not establish whether this is a temporary server IP restriction. The YouTube plugin supports Google device sign-in through its `TV` client, which can help with these failures.

Use a separate Google/YouTube account for the bot. The [plugin maintainers](https://github.com/lavalink-devs/youtube-source/tree/1.18.2#using-oauth-tokens) warn that account restrictions are possible and authentication does not guarantee playback. You complete Google's sign-in yourself; the bot does not need your Google password.

## Retrieve a lookup error that is missing from the logs

If the bot reports `YouTube could not load that request` but Lavalink logs only the search request, copy `diagnose-youtube.py` beside `compose.yml` and run from that directory:

```sh
sudo python3 diagnose-youtube.py
```

This read-only helper inspects the running Lavalink container, reports the configured clients and whether a saved token is present, then retrieves the REST response for a search and the corresponding video URL. It prints error details, including the nested cause stack trace, or a few result titles without displaying credentials. It does not join voice, play audio, restart services, or change configuration. Share its output when diagnosing lookup failures.

An earlier copy omitted `causeStackTrace`, leaving only the generic outer error. Update that copy in place and repeat the video lookup:

```sh
sed -i 's/"severity", "cause")/"severity", "cause", "causeStackTrace")/; s/\[:6000\]/[:20000]/' diagnose-youtube.py
sudo python3 diagnose-youtube.py 'https://www.youtube.com/watch?v=qhIONkp-cZA'
```

## Enable the optional configuration on Ubuntu

Copy `compose.youtube-oauth.yml` into the extracted deployment directory beside `compose.yml`. The bot image and main configuration can stay as they are. Run:

```sh
cd ~/discord-bot/discord-music-bot-linux-x64 &&
test -f compose.yml && test -f compose.youtube-oauth.yml && test -f .env &&
sed -i '/^COMPOSE_FILE=/d' .env &&
printf '\nCOMPOSE_FILE=compose.yml:compose.youtube-oauth.yml\n' >> .env &&
sudo docker compose up -d --force-recreate lavalink bot &&
sudo docker compose logs --tail=80 -f lavalink
```

The `COMPOSE_FILE` entry makes normal `docker compose` commands load the override on subsequent runs. It prioritizes TV for playback, WEB for search and metadata, and enables OAuth. The API stays private on the existing Docker network.

If running from the source repository instead, place the override in the repository root, or use `COMPOSE_FILE=compose.yml:deploy/compose.youtube-oauth.yml`.

## Complete and save the sign-in

1. Find the log line starting with `OAUTH INTEGRATION` that supplies a verification URL and a code. Open that URL in your browser and enter the code using the separate account.
2. Wait for the success message. The plugin logs a refresh token; keep it private and do not paste this log into chat.
3. Press Ctrl+C, edit the server's `.env`, and set one line:

   ```dotenv
   YOUTUBE_REFRESH_TOKEN='paste-the-refresh-token-here'
   ```

4. Recreate Lavalink and the bot so the saved token is used after future restarts:

   ```sh
   sudo docker compose up -d --force-recreate lavalink bot
   ```

5. Try `/play` again. If it still fails, share the playback error without any access or refresh tokens.

If the device code expires, recreate Lavalink and start the sign-in again. A rejected or revoked refresh token should be cleared from `.env` before starting a new sign-in. The plugin can print credentials at DEBUG level; keep the existing INFO logging configuration.

## Disable the optional configuration

```sh
sed -i '/^COMPOSE_FILE=/d' .env &&
sudo docker compose up -d --force-recreate lavalink bot
```

This returns to the default unauthenticated clients, including IOS. Remove the `YOUTUBE_REFRESH_TOKEN` entry and revoke the account authorization in Google if it is no longer needed.
