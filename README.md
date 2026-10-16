# chop - a better windows media player

A small, fast video player for Windows that can also cut videos without re-encoding them.

Open a video, press C to cut at the playhead, Delete to drop the piece you're on, and Ctrl+E to save. Gaps close up on their own and playback skips whatever you removed, so what you see is what you get.

Exports keep the original video and audio streams, so frame rate, bitrate and quality stay the same. In Exact mode only the few frames between a cut and the next keyframe get re-encoded, at very high quality. Lossless mode doesn't re-encode anything, but cuts snap to keyframes. When you export you can rename the file or replace the original (the old one goes to the Recycle Bin).

Playback is [mpv](https://mpv.io), cutting is [ffmpeg](https://ffmpeg.org). Handles mp4, mkv, mov, webm, ts and most audio files, including HDR and rotated phone video. Exact cuts work for H.264, HEVC, VP9 and VP8, and transparent WebM keeps its transparency.

## Keys

| Key | |
|---|---|
| Space / K | play / pause |
| ← → | previous / next frame |
| Shift + ← → | 1 second |
| J / L | 5 seconds |
| ↑ ↓ | previous / next cut |
| C | cut at the playhead |
| Delete | delete the segment you're on (or the I–O range) |
| Q / W | trim segment start to playhead / playhead to segment end |
| I / O | mark in / out |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Tab | switch between Exact and Lossless |
| F | fullscreen (controls show when the mouse hits the bottom) |
| Ctrl+E | export |
| M, 9, 0 | mute, volume down, volume up |

You can also drag a segment's edges to trim it, and scroll on the timeline to zoom.

## Download

Get the zip from [Releases](https://github.com/EugeneAffirmative/chop/releases), unzip it anywhere and run `Chop.exe`. Windows 10 or 11, 64-bit. Double-click `register.bat` if you want it as your default video player.

## Building it yourself

No SDK needed, it builds with the C# compiler that comes with Windows.

1. Put these in the `app` folder:
   - `libmpv-2.dll` from [mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases) (the `mpv-dev-x86_64-*.7z` download)
   - `ffmpeg.exe` and `ffprobe.exe`, version 7 or newer, from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) (the essentials build is fine)
2. Run `build.bat`. That makes `app\Chop.exe`.
3. Optional: run `register.bat` so Chop shows up in Windows' Default apps and you can make it your video player. `register.bat -Uninstall` undoes it.

There's also `ChopCli.exe` for scripted cuts:

```
ChopCli.exe --export in.mp4 out.mp4 exact 1.5-6,10-end
```

## License

MIT, see [LICENSE](LICENSE). The release zip also includes mpv and FFmpeg, which are GPL.
