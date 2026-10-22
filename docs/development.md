# Development

Architecture notes, design decisions and known pitfalls are kept in [CLAUDE.md](../CLAUDE.md). This page covers building, local debugging and releasing.

## Building

Requires the **.NET 10 SDK**. The project targets `net10.0` and references `Jellyfin.Controller`/`Jellyfin.Model` pinned to an exact version (`JellyfinPackageVersion` in the `.csproj`, currently `12.0.0`). Keep it pinned: a floating version can resolve to an assembly that does not match the host and fail with `ReflectionTypeLoadException`.

```bash
git clone https://github.com/TIGamingTV/jellynext.git
cd jellynext
dotnet build -c Release Jellyfin.Plugin.JellyNext.sln
# output: Jellyfin.Plugin.JellyNext/bin/Release/net10.0/
```

To try a build, copy the contents of the output folder into `plugins/JellyNext_<version>/` on a Jellyfin 12 server and restart it.

A `net9.0` build for Jellyfin 10.11 is not produced any more. It can be built from source by adding the `net9.0` target framework and `Jellyfin.Controller`/`Jellyfin.Model` `10.11.0` references back to the `.csproj`; the plugin code itself is not .NET 10 specific.

## Local debugging with Docker

`docker-compose.debug.yml` and `Dockerfile.debug` start a disposable test stack:

| Service | Image | Host port | Notes |
|---|---|---|---|
| `jellyfin-jellynext-debug` | built from `Dockerfile.debug` (`jellyfin/jellyfin:latest` + `vsdbg` in `/vsdbg`) | 8097 → 8096 (web UI), 4024 | Mounts the **Debug** DLL and PDB into `/config/plugins/JellyNext_1.0.0.0/` read-only; config persisted in `./jellyfin-config` (git-ignored) |
| `radarr` | `linuxserver/radarr:latest` | 7878 | |
| `sonarr` | `linuxserver/sonarr:latest` | 8989 | |
| `jellyseerr` | `fallenbagel/jellyseerr:latest` | 5055 | |

Jellyfin, Radarr and Sonarr also mount `../media_server/data/media` (a folder **next to** the repository); create it or adjust the paths before starting. Inside the stack, services reach each other by name (`http://radarr:7878`, `http://sonarr:8989`, `http://jellyseerr:5055`).

```bash
# build the plugin, then start the stack
dotnet build Jellyfin.Plugin.JellyNext/Jellyfin.Plugin.JellyNext.csproj -c Debug
docker compose -f docker-compose.debug.yml up --build

# after code changes: rebuild and recreate
dotnet build Jellyfin.Plugin.JellyNext/Jellyfin.Plugin.JellyNext.csproj -c Debug
docker compose -f docker-compose.debug.yml up --build --force-recreate

# logs, process id for attaching, stop
docker compose -f docker-compose.debug.yml logs -f jellyfin-jellynext-debug
docker compose -f docker-compose.debug.yml exec jellyfin-jellynext-debug pidof jellyfin
docker compose -f docker-compose.debug.yml down
```

Jellyfin is then at <http://localhost:8097>. `docker compose ... down -v` also deletes the named volumes (Jellyfin cache, Radarr/Sonarr/Jellyseerr config); `./jellyfin-config` must be deleted by hand for a completely fresh Jellyfin.

The bundled Rider run configurations (`.idea/.../runConfigurations`) wrap the same commands: *Build (Debug)*, *Build (Release)*, *Clean*, *Rebuild (Release)*, *Start Jellyfin Docker*, *Stop Jellyfin Docker*, *Rebuild and Restart Docker*, and *Debug with Docker* (Build (Debug) + Start Jellyfin Docker). They call the legacy `docker-compose` binary.

To debug in Rider: start the stack, then **Run → Attach to Process…**, choose the Docker connection, the Jellyfin container and the `jellyfin` process, and attach with the .NET debugger. Breakpoints in plugin code hit once you trigger it from the Jellyfin UI.

## Releasing

Releases are produced by `.github/workflows/build.yml`. Pull requests to `main` only build. **Pushing to `main` publishes a release**, so merging is releasing.

1. Bump `<AssemblyVersion>` **and** `<FileVersion>` in `Jellyfin.Plugin.JellyNext/Jellyfin.Plugin.JellyNext.csproj`.
2. Add a `## v<version>` section to `CHANGELOG.md`. The workflow uses everything up to the next `## v` heading as the release notes and as the manifest changelog; without it the notes are just "Release v<version>".
3. Merge to `main`.

The workflow then:

1. Fails if the tag `v<version>` already exists (a published zip must never change, or its checksum in the manifest would be wrong).
2. Builds and zips `bin/Release/net10.0` as `jellynext-v<version>.zip` and publishes a GitHub release `v<version>`.
3. Adds the version to `manifest.json` (newest first) with `targetAbi` derived from `JellyfinPackageVersion`, the download URL and the zip's MD5, and commits it to `main` (`.github/scripts/update-manifest.sh`; idempotent).

No secrets are needed beyond the built-in `GITHUB_TOKEN`.

### Plugin identity

The plugin GUID `ce392429-21cb-43a1-b02d-be316b54bdf3` appears in `Plugin.cs`, `Configuration/configPage.html` (`pluginUniqueId`) and `manifest.json`; all three must match. Never change it without reading the *Plugin Identity* section of [CLAUDE.md](../CLAUDE.md).

## Contributing

Fork the repository, create a branch, and open a pull request against `main`. Keep user-facing documentation in the [README](../README.md) and the guides in `docs/`.
