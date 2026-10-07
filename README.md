# DVD-Audio

Jellyfin plugin that reads a DVD-Audio `.iso` or a folder rip and publishes it to two libraries:

- Music: one album per audio group, as FLAC (16/20/24-bit LPCM or MLP).
- Movies: one link to the video zone when the disc also contains `VIDEO_TS`.

Jellyfin cannot play AOB or MLP directly. The music files are real FLAC so direct play, seeking, and ffprobe work. The movie side is a symlink (or a `.strm` file if the server cannot create a file symlink) so Jellyfin's existing DVD playback is used.

Target: Jellyfin 10.11.11, ABI `10.11.0.0`, .NET 9.

## What gets published

A disc named `Artist - Album.iso` becomes:

```text
Music/{Artist}/{Album}/01 - Track 01.flac
Music/{Artist}/{Album}/album.nfo
Music/{Artist}/{Album}/dvd-audio.json
Movies/DVD-Audio/{Album}/{Album}.iso    -> source image, only when VIDEO_TS is present
```

Group 1 uses the disc name. Later groups are `{Album} (Group N)`. A file that is not `Artist - Album` uses the artist name `DVD-Audio`.

`dvd-audio.json` records the source path, length, modification time, group, delivery mode, and track count. A completed album is not extracted again unless **Extract tracks again** is enabled. An existing `album.nfo` is never replaced. An existing movie file or folder is never replaced. Removing the source image does not delete library files.

Cache mode writes the FLAC files under the Jellyfin data directory (`dvd-audio/{hash}/groupNN`) and puts `.strm` shortcuts in the music library. Each shortcut contains the absolute FLAC path.

## Library scan

An audio-only DVD-Audio image is ignored in movie and mixed libraries so it does not become a broken video item. A hybrid image stays visible there. Any DVD-Audio image inside a music library is ignored; the scheduled task publishes the playable audio.

The task is **Import DVD-Audio**, in the Library category, every 24 hours. It searches the configured music root, movie root, and extra source paths for `*.iso` files and for directories that contain `AUDIO_TS`. When it writes something, it queues one library scan.

## Limits

- `DVDAUDIO.MKB` means CPPM. The audio is reported and skipped. A hybrid video zone can still be linked. This plugin does not decrypt CPPM, CPRM, or CSS.
- LPCM that uses two channel groups with different rate or depth is skipped.
- Supported LPCM is a single group at 16, 20, or 24 bits. MLP is passed to ffmpeg as an MLP stream.
- Titles inside the IFO are not used. Tracks are named `Track 01` onward. A year is read from `19xx` or `20xx` in the disc name.
- The server ffmpeg binary must be able to encode FLAC. Jellyfin's configured encoder path is used.
- Creating the movie symlink needs permission to create symbolic links. A file image falls back to `{Album}.strm`. A folder rip does not: the server needs a directory symlink so `VIDEO_TS` stays intact.
- Same folder cannot be both a music library and a movie library. Use two library roots.

## Install

Build, then copy both assemblies into a new plugin folder on the Jellyfin server:

```text
Jellyfin.Plugin.DvdAudio.dll
DvdAudio.Core.dll
```

Restart Jellyfin, open Dashboard, Plugins, DVD-Audio, and set the library paths. Paths are paths on the Jellyfin server. Run **Import DVD-Audio** from Scheduled Tasks.

This repository does not install the plugin onto a server by itself.

## Build

.NET 9 SDK is required. From this directory:

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet;" + $env:PATH
dotnet test
dotnet publish src/Jellyfin.Plugin.DvdAudio -c Release -o artifacts
```

The publish folder contains the two assemblies listed in `build.yaml`.
