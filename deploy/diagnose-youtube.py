#!/usr/bin/env python3
"""Read Lavalink's YouTube lookup response without exposing its credentials."""

import ipaddress
import json
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


def docker(*arguments):
    return subprocess.check_output(
        ["docker", *arguments], text=True, stderr=subprocess.PIPE
    )


def main():
    container_id = docker("compose", "ps", "-q", "lavalink").strip()
    if not container_id:
        print("Lavalink is not running. Check sudo docker compose ps -a.")
        return 1

    container = json.loads(docker("inspect", container_id))[0]
    environment = dict(
        entry.split("=", 1)
        for entry in container["Config"]["Env"]
        if "=" in entry
    )
    password = environment.get("LAVALINK_SERVER_PASSWORD", "")
    refresh_token = environment.get("PLUGINS_YOUTUBE_OAUTH_REFRESHTOKEN", "")
    if not password:
        print("The running Lavalink container has no LAVALINK_SERVER_PASSWORD.")
        return 1

    def safe(value):
        text = str(value)
        for secret in (password, refresh_token):
            if secret:
                text = text.replace(secret, "[REDACTED]")
        return re.sub(
            r"(?:1//|ya29\.)[A-Za-z0-9_.~+/=-]+", "[REDACTED]", text
        )[:20000]

    networks = container["NetworkSettings"]["Networks"].values()
    addresses = [network.get("IPAddress") for network in networks]
    address = next((item for item in addresses if item), None)
    if not address or not ipaddress.ip_address(address).is_private:
        print("Could not find Lavalink's private Docker network address.")
        return 1

    print("Clients:", safe(environment.get("PLUGINS_YOUTUBE_CLIENTS", "default")))
    print("OAuth enabled:", safe(environment.get("PLUGINS_YOUTUBE_OAUTH_ENABLED", "false")))
    print("Saved refresh token:", "present" if refresh_token else "missing")

    # Contact the local container directly, bypassing any host HTTP proxy.
    http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    identifiers = sys.argv[1:] or [
        "ytsearch:adi minune carmina",
        "https://www.youtube.com/watch?v=qhIONkp-cZA",
    ]
    for identifier in identifiers:
        print("\nRequest:", safe(identifier))
        query = urllib.parse.urlencode({"identifier": identifier})
        request = urllib.request.Request(
            f"http://{address}:2333/v4/loadtracks?{query}",
            headers={"Authorization": password},
        )
        try:
            with http.open(request, timeout=20) as response:
                result = json.load(response)
        except urllib.error.HTTPError as error:
            print("Lavalink HTTP status:", error.code)
            continue
        except (urllib.error.URLError, TimeoutError):
            print("The lookup did not finish within the connection timeout.")
            continue

        load_type = result.get("loadType", "unknown")
        print("Load type:", safe(load_type))
        data = result.get("data")
        if load_type == "error" and isinstance(data, dict):
            for field in ("message", "severity", "cause", "causeStackTrace"):
                if data.get(field) is not None:
                    print(field + ":", safe(data[field]))
        elif load_type in ("search", "playlist", "track"):
            tracks = data if load_type == "search" else (
                data.get("tracks", []) if load_type == "playlist" else [data]
            )
            print("Tracks returned:", len(tracks))
            for track in tracks[:3]:
                print("Title:", safe(track.get("info", {}).get("title", "unknown")))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, subprocess.CalledProcessError, ValueError, KeyError):
        # Never print docker inspect output: it contains runtime secrets.
        print("Could not read the deployment. Run from its directory with sudo and check docker compose ps -a.")
        sys.exit(1)
