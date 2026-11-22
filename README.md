# DJFlix

Personal media ecosystem: a Windows hub (service + desktop app) and a Fire TV client sharing one backend.

## Status

| Component | State |
|---|---|
| `src/DJFlix.Server` — hub service, REST API v1, SQLite, pairing, sync, TMDB + Real-Debrid connectors, library scanner | Built and tested in CI on Windows and Linux (103 end-to-end checks, `tests/e2e.py`) |
| Windows Service hosting and firewall rule | Compiles on Windows; not yet exercised (needs the installer) |
| `firetv/` — Fire TV app (Kotlin, Compose, Media3): setup wizard, pairing, profiles, home, browse, search, details, player, settings | Built in CI; smoke-tested on an Android TV emulator (pairs, plays, reports progress). Not yet run on a physical Fire TV |
| WinUI 3 desktop app, installer | Not started |

## Install the Fire TV app

1. Download `DJFlix-FireTV.apk` from this repo's Releases page ("DJFlix Fire TV (latest build)").
2. On the Fire TV: Settings → My Fire TV → Developer options → turn on ADB debugging. Note the IP under About → Network.
3. From a PC with adb: `adb connect <fire-tv-ip>:5555` then `adb install -r DJFlix-FireTV.apk`.

Every build is signed with the same key (`firetv/sideload.keystore`), so new builds install over old ones.
Screenshots from the latest emulator run are on the `ci-screenshots` branch.

## Run the hub

Requires the .NET 8 SDK.

    dotnet run --project src/DJFlix.Server

Data lives in `%ProgramData%\DJFlix` (Windows) or `~/.djflix`. Override with `DJFLIX_DATA`; override the port (default 8674) with `DJFLIX_PORT`.
The desktop app authenticates with the token in `admin.token` in that folder, over loopback only.

## Test

    dotnet build src/DJFlix.Server
    python3 tests/e2e.py src/DJFlix.Server/bin/Debug/net8.0/DJFlix.Server.dll

The tests start a fake TMDB / Real-Debrid upstream (`tests/fake_upstream.py`); no internet or accounts are needed.

## Design notes

- **No third-party packages in the server.** SQLite is reached through the system library (`winsqlite3.dll` ships with Windows 10+).
- **Credentials** are AES-GCM encrypted in the database; the key file is DPAPI-wrapped on Windows. They are never returned by any endpoint or sent to clients.
- **Clients** authenticate with a per-device token obtained by pairing (6-digit code, or approval on the PC). Admin endpoints accept only the local admin token from loopback.
- **Network**: requests from non-private addresses are refused; API calls are rate limited per IP; logs omit query strings and play tokens.
- **Playback**: the hub issues a session with a stream URL. Local files are served with range support; debrid files redirect to the provider's playback link (or are relayed through the hub when "proxy streams" is on).
- **Progress conflicts**: updates are ordered on the hub's clock (`now − ageMs`); an older update from a different session is rejected, and concurrent sessions are reported to each player.
- **Metadata**: IMDb has no free public API, so TMDB is the provider behind `IMetadataProvider`; IMDb IDs are stored and linked.
- **Debrid content** is limited to files already in the user's own Real-Debrid cloud, listed through the documented API.
- **Discovery**: UDP probe `DJFLIX_DISCOVER` on port 47810; manual IP entry is the fallback.
