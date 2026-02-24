# WindSonic

WindSonic is a native Windows music player built with **WPF (.NET 8)** for low-overhead listening.

It searches songs using Apple's public **iTunes Search API** (no account required), then plays audio from YouTube using **YoutubeExplode** + native **libVLC** (audio-only).

## Highlights

- Native Windows app (no Electron / no embedded browser UI)
- Audio-only playback for lower GPU/CPU overhead while gaming
- iTunes Search API metadata search (no Spotify Premium / no API signup required)
- Queue, playlists, recents, shuffle, repeat, next/previous
- YouTube **Source Picker** (switch between multiple YouTube uploads for the same track)
- Glassy dark UI with custom native window chrome
- Persistent settings and queue/library state in `%AppData%\WindSonic`

## How It Works

1. Search track metadata (title/artist/album) via iTunes Search API
2. Resolve matching YouTube audio streams
3. Play the chosen stream with native `libVLC` (audio-only)

Because playback comes from YouTube uploads, quality can vary by uploader. WindSonic includes a source picker so you can switch to a better upload when needed.

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK (for building/running from source)
- Internet connection

## Run (PowerShell)

```powershell
cd .\WindSonic
dotnet build .\WindSonic.sln
.\WindSonic.App\bin\x64\Debug\net8.0-windows\WindSonic.exe
```

Or run directly with `dotnet`:

```powershell
dotnet run --project .\WindSonic.App\WindSonic.App.csproj
```

## Usage

### Search and play

1. Enter a song name in the search box
2. Press `Enter` or click `Search`
3. Double-click a result or click `Play`

### Queue / playlists

- `Add to Queue`: add selected search result
- `Queue Top 10`: quick session builder from search results
- `Save as Playlist`: save current queue as a playlist
- `Load + Play`: replace queue with a playlist and start playback
- `Append to Queue`: add playlist tracks to current queue

### Source picker (fix bad YouTube uploads)

When a song is playing:

1. Open the source dropdown in **Now Playing**
2. Choose another YouTube source
3. Click `Use Source`

Use `Refresh` to fetch a new set of YouTube candidates.

## Keyboard Shortcuts

- `Ctrl+F` focus the search box
- `Space` play/pause (when not typing in a textbox)
- `Delete` remove selected queue item / playlist track

## Performance Notes

WindSonic is tuned for gaming-friendly playback:

- YouTube playback is audio-only (`libVLC` is started with no video rendering)
- Native WPF UI with virtualized lists where appropriate
- Async/cancellable search and stream resolution
- x64 target only

## Local Data / Privacy

WindSonic stores local settings and queue/library state in:

- `%AppData%\WindSonic\settings.json`
- `%AppData%\WindSonic\startup-error.log` (if a startup crash occurs)

No cloud sync, telemetry, or accounts are required for search/playback metadata lookup.

## Troubleshooting

### Some songs sound bad

This is expected for some YouTube uploads. Use the **Source Picker** to switch to a better uploader (official/topic/VEVO uploads usually sound better).

### App exits on startup

Check:

- `%AppData%\WindSonic\startup-error.log`

### Build fails with file lock

Close the running app first (the EXE is locked while running):

```powershell
taskkill /IM WindSonic.exe /F 2>$null
```

## Tech Stack

- .NET 8
- WPF
- LibVLCSharp + VideoLAN.LibVLC.Windows
- YoutubeExplode
- iTunes Search API (Apple)
