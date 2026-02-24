# WindSound (Native Windows Music App)

WindSound is a fully native Windows desktop music player built with **WPF (.NET 8)**.

It uses:
- **Spotify Web API** to search tracks (metadata/discovery)
- **YouTube** (via `YoutubeExplode`) to resolve a playable audio stream
- **libVLC** (native playback engine) to play **audio-only** streams with low overhead

## Why this is optimized for gaming
- Native WPF app (no Electron/web runtime)
- Native `libVLC` playback backend
- YouTube playback is **audio-only** (no video surface rendering)
- Virtualized results list for low UI cost
- Async + cancellable search and track resolution
- x64 target, no 32-bit fallback

## Requirements
- Windows 10/11 (Windows 11 recommended for best glass backdrop effect)
- .NET 8 SDK (for building)
- Internet connection
- Spotify Developer credentials (Client ID + Client Secret)

## Setup (Spotify credentials)
1. Go to [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
2. Create an app
3. Copy the **Client ID** and **Client Secret**
4. Launch WindSound and paste them into the settings panel
5. Click **Save Settings**

Credentials are stored locally in:
`%AppData%\\WindSound\\settings.json`

## Build and run
```powershell
cd .\WindSound.App
dotnet build
dotnet run
```

Or open `WindSound.sln` in Visual Studio and run `WindSound.App`.

## Notes
- This app searches Spotify and then resolves a matching YouTube audio stream heuristically (title/artist/duration scoring).
- Some YouTube streams may expire or fail; retrying the track usually resolves a fresh URL.
- The UI uses native window chrome customization + DWM backdrop/Mica where supported.
