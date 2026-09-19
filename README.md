# Wallcast

[한국어](README.ko.md)

A small live wallpaper app for Windows. Point it at a capture card or virtual camera - or a video file - and that picture runs behind the desktop icons on one monitor.

## Use

1. Run `artifacts/Wallcast/Wallcast.exe`.
2. Pick a device under **Capture card / virtual camera**, which is the default, or a file under **Video file**.
3. Pick the output monitor and press **Apply to desktop**.
4. **Stop** brings your original wallpaper back. The window's X hides to the tray; **Exit** in the tray menu quits for real.

Video keeps its own aspect ratio and loops. It is muted by default and can be unmuted. Capture is video only. The capture buffer value sets the DirectShow queue capacity and is not an exact latency figure. That capacity is worked out from the frame size of the format the device emits, so the same value holds the same number of frames as the resolution goes up. The screen always shows the newest frame, presented as soon as it arrives.

### Capture settings

| Setting | Choices / default |
| --- | --- |
| Pixel format | **NV12**, YUY2, UYVY, RGB24, MJPEG |
| Resolution / FPS | **1920×1080 / 60**, 720p·1440p·4K and others / 59.94·50·30 and others |
| Color space | **Rec.709**, Rec.601, Rec.2020 (SDR conversion matrix) |
| Color range | **Limited**, Full |
| Display aspect | **Input resolution**, 16:9, 4:3, 16:10, Stretch to screen, Custom size |
| Custom size / sizing | e.g. `2732x2048` · **Fit to screen**, Actual pixels |

Nothing is guessed. The device is opened with exactly the values you chose and YUV→RGB uses exactly the matrix you chose. If the device does not support a combination you get an error rather than a silent switch to another format. The YUV matrix and range selections do not affect RGB input. Rec.2020 does not mean HDR tone mapping. Press **Apply to desktop** for a change to take effect; settings persist across runs. The lists are common presets — querying a device for its own supported modes is not implemented yet.

For a Live Gamer BOLT, start with **NV12 / 1920×1080 / 60 / Rec.709 / Limited / Input resolution**. If blacks look raised or shadow detail is crushed, change the color range to match the actual source. Unlike an earlier version, the whole input is no longer force-squeezed to 4:3. Black bars baked into the source are left alone.

**Display aspect** decides the shape of the rectangle the picture is drawn into, centred on the
monitor. It does not crop and it does not change what the device captures: the whole frame is stretched
into that rectangle, so picking a shape the source is not will squash it. `Input resolution` means the
source's own shape, which is the undistorted one; the fixed ratios are there for a source whose reported
resolution does not match its real shape.

`Custom size` takes a size of your own, such as an iPad's `2732x2048`. `Fit to screen` keeps that shape
and grows it until it touches an edge of the monitor. `Actual pixels` places it at exactly that many
pixels, centred, which is what you want when the source resolution and the monitor do not divide evenly
and you would rather not rescale at all.

Whatever the picture does not cover is **not drawn on**. The app's window is only as large as the
picture, so the surround is still the wallpaper Windows was already showing - no black bars. Set the
wallpaper you want around it in Windows itself.

To use an OBS scene or desktop, start OBS's virtual camera and pick that device. A native desktop capture input is not implemented. Capturing your own desktop feeds the picture back into itself, so use another monitor or window as the source.

## Build

Needs Windows x64 and the .NET 8 SDK. Prepare the capture engine before the first build. VLC and FFmpeg ship inside the publish folder, so users install nothing extra.

```powershell
./scripts/setup-capture.ps1
dotnet run --project Wallcast
dotnet publish Wallcast -c Release -r win-x64 --self-contained true -o artifacts/Wallcast
```

With the project-local SDK, use `.\.tools\dotnet\dotnet.exe` instead of `dotnet`. Distribution needs the whole `artifacts/Wallcast` folder; this is not a single-exe build. Publish into an empty folder. Overwriting an existing one can leave a framework assembly in place when its timestamp is newer than the package version's, and the app then fails at startup loading `System.Text.Json`.

## Scope and layout

- `Sources.cs`: the file and capture input models.
- `Playback.cs`: picks the playback path per input, LibVLC video playback, looping, errors and cleanup.
- `CaptureOptions.cs`: pixel format, resolution, FPS, YUV conversion, display aspect.
- `CapturePlayback.cs`: FFmpeg DirectShow input, explicit color conversion, keeps the newest frame. Frames arrive over a named pipe. A redirected stdout pipe has a small buffer and stalls near 800 MB/s, while 4K 60fps BGRA needs 2.0 GB/s.
- `CaptureSurface.cs`: presents BGRA frames through a DXGI flip-model swap chain and keeps the aspect ratio. Scaling runs on the GPU.
- `DesktopHost.cs`: attaches the video window to the Windows Explorer WorkerW.
- `CaptureDevices.cs`: DirectShow video device discovery.
- `MainForm.cs`: settings, monitor selection, tray, local settings file.
- `scripts/make-icon.ps1`: draws `Wallcast.ico`. Every size is drawn natively, since the two icon
  silhouettes turn to mush when a large drawing is scaled down. `-PngDirectory` also writes the
  sizes out as PNG.

Settings live in `%LOCALAPPDATA%\Wallcast\settings.json`. The app does not auto-play on launch and does not add itself to Windows startup. Simultaneous playback on several monitors, an editor, web backgrounds and a workshop are all out of scope.

The Explorer window this attaches to has no GDI redirection surface. Pixels painted there with GDI are never composited, so both video (LibVLC's Direct3D11 output) and capture (the swap chain above) reach the screen only through a D3D path. Draw the capture with GDI and frames arrive normally, the control paints normally, and the desktop shows nothing at all.

The WorkerW approach is not a public Windows wallpaper API, so behaviour varies with the Windows and Explorer version. When Explorer restarts or the monitor layout changes, playback stops and the app asks you to apply again. Only devices exposed through DirectShow are supported; devices that need a vendor SDK are out of scope. Several devices with the same name are not guaranteed to be told apart.

## Verification

The automatic checks run with `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true`. They cover VLC loading, device discovery, invalid input, display aspect geometry, real FFmpeg NV12 red/blue conversion, Limited/Full, the Rec.709/601 difference, and repeatable playback cleanup.

By hand:

- A short video loops when it ends; mute toggles; stop works.
- Desktop icons and their context menu still work.
- A secondary monitor and a different DPI still fill the chosen screen.
- Applying a capture device or OBS virtual camera, unplugging it, and another app holding the device all produce sensible errors.
- Switching inputs, hiding to tray, restoring and exiting all release the device and restore the original wallpaper.
- Restarting Explorer or detaching a monitor prompts to apply again.

Video uses LibVLCSharp with VideoLAN.LibVLC.Windows.GPL, capture input uses the FFmpeg 9.0.1 Gyan essentials build, and capture output uses Vortice.Direct3D11. `scripts/setup-capture.ps1` pins the version and SHA-256. Keep `capture/LICENSE-FFmpeg.txt` and `capture/README-FFmpeg.txt` in place.

Desktop output check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --desktop "Live Gamer BOLT"`. It opens the device, attaches it to the desktop, briefly minimises open windows, grabs the real screen and compares it against the frames it received. `--resolution` and `--aspect` set the resolution and display aspect, `--custom 2732x2048` (with `--actual` for exact pixels) sets a custom one, and it reports frames received and frames on screen separately. Whenever the picture does not fill the monitor it also checks the surround still matches the bare desktop, and says how many of those samples were not black to begin with, since a black wallpaper cannot tell a working surround from a black bar. The grab is saved to `artifacts/desktop-capture.png`. It minimises and restores windows, so it is not part of the automatic run. It exists because receiving frames does not prove anything reached the screen.

That check excludes the area of any window that refused to minimise, and reports SKIP rather than a failure when less than 20% of the wallpaper was uncovered. With a moving source the moment of the screen grab and the moment a frame arrives do not line up, so it compares against the best of several frames taken either side of the grab. Without both of those a perfectly good renderer looks broken.

Throughput check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --bench "Live Gamer BOLT"`. It reports frames per second and MB/s actually received at 1080p, 1440p and 4K. Anything slower than the device emits means that difference in frames piling up in the queue as latency. Start here for latency problems.

Real device check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --capture "Live Gamer BOLT"`. It opens NV12/Rec.709/Limited/1080p60 for ten seconds and checks that frames arrive. Adding `--snapshot` saves one frame to `artifacts/capture-nv12-rec709.png`. The app itself never records or saves the screen.

2026-09-19, 4K latency fix: the cause was the throughput ceiling of the redirected stdout pipe. Moving to a named pipe took 4K reception from 24.3fps to 59.9fps (1895 MB/s). The renderer also moved from timer polling to presenting when a frame arrives. The DirectShow queue size was corrected to use the input format, so a 150ms setting at 4K is 112 MB (9 frames) rather than 298 MB (24 frames). In steady state both 1080p and 4K receive at 60fps and reach the screen at 60fps. Verified on a published build all the way through applying 4K.

2026-09-19, desktop output fix: capture not appearing on the desktop at all was fixed by replacing the GDI renderer with a DXGI swap chain. The `--desktop` check matched 95% of real screen pixels against the received frame, and the 4:3 bars came out black. A published build was run and **Apply to desktop** pressed to confirm the Live Gamer BOLT picture reaches the desktop. All automatic checks pass.

2026-09-19, color fix: automatic checks pass. 542 frames received from a Live Gamer BOLT over ten seconds. The device was showing its own No Signal screen at the time, so comparing against a reference image of real iPad content was deferred. A sandbox may be denied device access, so run this with ordinary Windows process rights.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).

This is not a free choice: the bundled FFmpeg is built `--enable-gpl --enable-version3`, and the bundled LibVLC is the GPL build. Distributing the result therefore carries GPLv3 obligations, including providing source for those components. A permissive license would require dropping both bundled binaries.
