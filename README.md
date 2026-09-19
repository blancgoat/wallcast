# Wallcast

<img width="3840" height="2160" alt="스크린샷 2026-09-20 015700" src="https://github.com/user-attachments/assets/d46da8b0-7133-4d29-8f7f-0bf254de44b0" />

<img width="1182" height="1936" alt="image" src="https://github.com/user-attachments/assets/16722787-99f9-4908-a52a-5589c6e0162b" />

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
| Input HDR / peak | **SDR**, HDR10 / PQ → SDR, HLG → SDR · **1000** nits |
| Display aspect | **Input resolution**, Stretch to screen, Output resolution |
| Output size / mapping | e.g. `2753x2064` · **Fill, cropping the overflow**, Fit / Centre at 1:1 / Stretch to fill |
| Screen position | 3x3 grid, **Center** |

Nothing is guessed. The device is opened with exactly the values you chose and YUV→RGB uses exactly the matrix you chose. If the device does not support a combination you get an error rather than a silent switch to another format. The YUV matrix and range selections do not affect RGB input. Rec.2020 does not mean HDR tone mapping. Press **Apply to desktop** for a change to take effect; settings persist across runs. The lists are common presets — querying a device for its own supported modes is not implemented yet.

For a Live Gamer BOLT, start with **NV12 / 1920×1080 / 60 / Rec.709 / Limited / Input resolution**. If blacks look raised or shadow detail is crushed, change the color range to match the actual source. If the card pillarboxes a 4:3 source into its 16:9 frame, set **Display aspect** to `Output resolution` with `Fill, cropping the overflow` and those bars come off.

**Display aspect** decides what happens to the captured frame before it reaches the desktop.
`Input resolution` leaves it alone. `Stretch to screen` fills the monitor and distorts to do it.
`Output resolution` is the one that behaves like OBS: you give a size such as `2753x2064` and the
picture comes out at that size **whatever the capture resolution is**. Capture at 1920x1080 or at
3840x2160 and the result is still 2753x2064; only how much detail went into it changes. **Output
mapping** says how the frame is laid into that rectangle, always centred inside it:

- `Fill, cropping the overflow` scales the frame until it covers the whole output and cuts off whatever
  hangs over. Nothing is distorted and no gap is left. This is the one that takes a pillarbox off: on a
  3840x2160 capture of a 4:3 source it cuts `2880x2160` at x=480, exactly where the bars end.
- `Fit, padding the gap` keeps the entire frame and shrinks it until it sits inside the output, so a
  shape mismatch shows up as a gap rather than as a missing edge.
- `Centre at 1:1, padding the gap` does not scale at all. It takes as much of the middle of the frame as
  the output has room for and draws it one source pixel per screen pixel, so it never grows past what
  the capture actually has: a 1920x1080 capture stays 1920x1080 even if the output asks for more.
- `Stretch to fill, distorting` keeps the whole frame and stretches it onto the output exactly. This is
  the one for an older card that squeezes the source into its frame instead of pillarboxing it, where
  there is nothing to cut and the picture only needs its proportions back.

A gap is not painted black - it is simply not covered, so your own wallpaper shows through it. An output
bigger than the monitor is scaled down to fit, keeping its shape.

The line under the settings spells out what the current numbers do - what is cut out of the frame, how
big it is drawn and where - so there is no need to guess which reading is in force.

**Screen position** is the 3x3 grid, read like a canvas-size anchor: it decides where on the monitor the
picture sits. It places the output rectangle; what goes inside that rectangle is always centred. It can
only do something where the picture leaves room, so the grid greys out when there is none - a 16:9 capture shown whole on a 16:9 monitor already covers every pixel, and so does anything
stretched to the screen. The crop itself always comes out of the middle of the frame, because that is
where a pillarbox puts the bars.

`Input resolution` and `Stretch to screen` look identical whenever the capture is the same shape as the
monitor, which is the usual case. They part company as soon as it is not: a 640x480 capture on a 16:9
monitor is drawn 4:3 and undistorted by the first, and stretched to fill by the second.

There are no fixed 16:9 / 4:3 entries. Under cropping they would only be a clumsier output resolution, and
if a fixed ratio is ever wanted it will be wanted as a stretch, not a crop.

Cropping happens in the capture engine, so the bars never travel down the pipe in the first place.

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

### Releasing a build

```powershell
./scripts/package.ps1
```

That publishes into an empty `artifacts/Wallcast` and archives it as
`artifacts/Wallcast-v1.0.0-win-x64.zip`, about 164 MB, which is the single file to upload. The version
in the name is read out of the binary that was just built, so the two can never disagree; name, then
version, then platform, the order Node and PowerShell use for their own downloads. It unpacks to one
`Wallcast` folder that runs from anywhere - no installer and nothing to install alongside it. The script refuses to package a build that
is missing the capture engine, the VLC runtime or either licence file.

The publish drops the x86 and arm64 VLC runtimes the package ships, which an x64-only build can never
load; that alone is 244 MB of the 642 MB it would otherwise be. It archives with `tar.exe`, which comes
with Windows 10 and 11, because `Compress-Archive` writes entry names with backslashes that unzip on
macOS and Linux turns into one long filename per file.

The version lives in one place, `<Version>` in `Wallcast/Wallcast.csproj`. It reaches the title bar, the
tray tooltip and the file properties of the exe, and the build appends the commit it came from, so a
screenshot of the title bar is enough to know exactly which build someone is running. Tag a release to
match: `git tag -a v1.0.0 -m "Wallcast v1.0.0"`.

Because FFmpeg and LibVLC are GPL, so is anything you hand out that contains them. `LICENSE` and
`THIRD-PARTY.txt` are published into the folder for that reason - the latter lists every component, its
licence and where its source lives. Keep both in any archive you distribute.

## Scope and layout

- `Sources.cs`: the file and capture input models.
- `Playback.cs`: picks the playback path per input, LibVLC video playback, looping, errors and cleanup.
- `CaptureOptions.cs`: pixel format, resolution, FPS, YUV conversion, and the crop that takes the capture card's black bars off.
- `CapturePlayback.cs`: FFmpeg DirectShow input, explicit color conversion, keeps the newest frame. Frames arrive over a named pipe. A redirected stdout pipe has a small buffer and stalls near 800 MB/s, while 4K 60fps BGRA needs 2.0 GB/s.
- `CaptureSurface.cs`: presents BGRA frames through a DXGI flip-model swap chain and keeps the aspect ratio. Scaling runs on the GPU.
- `DesktopHost.cs`: attaches the video window to the Windows Explorer WorkerW.
- `CaptureDevices.cs`: DirectShow video device discovery.
- `MainForm.cs`: settings, monitor selection, tray, local settings file.
- `scripts/package.ps1`: publishes clean and archives the release zip, refusing to ship a build that is missing a licence or the capture engine.
- `scripts/make-icon.ps1`: draws `Wallcast.ico`. Every size is drawn natively, since the two icon
  silhouettes turn to mush when a large drawing is scaled down. `-PngDirectory` also writes the
  sizes out as PNG.

Settings live in `%LOCALAPPDATA%\Wallcast\settings.json`. The app does not auto-play on launch and does not add itself to Windows startup. Simultaneous playback on several monitors, an editor, web backgrounds and a workshop are all out of scope.

The Explorer window this attaches to has no GDI redirection surface. Pixels painted there with GDI are never composited, so both video (LibVLC's Direct3D11 output) and capture (the swap chain above) reach the screen only through a D3D path. Draw the capture with GDI and frames arrive normally, the control paints normally, and the desktop shows nothing at all.

The WorkerW approach is not a public Windows wallpaper API, so behaviour varies with the Windows and Explorer version. When Explorer restarts or the monitor layout changes, playback stops and the app asks you to apply again. Only devices exposed through DirectShow are supported; devices that need a vendor SDK are out of scope. Several devices with the same name are not guaranteed to be told apart.

## Verification

The automatic checks run with `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true`. They cover VLC loading, device discovery, invalid input, output resolution and every mapping mode, screen anchors, real FFmpeg NV12 red/blue conversion, Limited/Full, the Rec.709/601 difference, and repeatable playback cleanup.

By hand:

- A short video loops when it ends; mute toggles; stop works.
- Desktop icons and their context menu still work.
- A secondary monitor and a different DPI still fill the chosen screen.
- Applying a capture device or OBS virtual camera, unplugging it, and another app holding the device all produce sensible errors.
- Switching inputs, hiding to tray, restoring and exiting all release the device and restore the original wallpaper.
- Restarting Explorer or detaching a monitor prompts to apply again.

Video uses LibVLCSharp with VideoLAN.LibVLC.Windows.GPL, capture input uses the FFmpeg 9.0.1 Gyan essentials build, and capture output uses Vortice.Direct3D11. `scripts/setup-capture.ps1` pins the version and SHA-256. Keep `capture/LICENSE-FFmpeg.txt` and `capture/README-FFmpeg.txt` in place.

Desktop output check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --desktop "Live Gamer BOLT"`. It opens the device, attaches it to the desktop, briefly minimises open windows, grabs the real screen and compares it against the frames it received. `--resolution` and `--aspect` set the capture resolution and the display aspect, `--custom 2753x2064` sets an output resolution (`--pixels` for `Centre at 1:1`, `--squeeze` for `Stretch to fill`), `--anchor Left` moves it on the monitor, and it reports frames received and frames on screen separately. Whenever the picture does not fill the monitor it also checks the surround still matches the bare desktop, and says how many of those samples were not black to begin with, since a black wallpaper cannot tell a working surround from a black bar. The grab is saved to `artifacts/desktop-capture.png`. It minimises and restores windows, so it is not part of the automatic run. It exists because receiving frames does not prove anything reached the screen.

That check excludes the area of any window that refused to minimise, and reports SKIP rather than a failure when less than 20% of the wallpaper was uncovered. With a moving source the moment of the screen grab and the moment a frame arrives do not line up, so it compares against the best of several frames taken either side of the grab. Without both of those a perfectly good renderer looks broken.

Throughput check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --bench "Live Gamer BOLT"`. It reports frames per second and MB/s actually received at 1080p, 1440p and 4K. Anything slower than the device emits means that difference in frames piling up in the queue as latency. Start here for latency problems.

Real device check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --capture "Live Gamer BOLT"`. It opens NV12/Rec.709/Limited/1080p60 for ten seconds and checks that frames arrive. Adding `--snapshot` saves one frame to `artifacts/capture-nv12-rec709.png`. The app itself never records or saves the screen.

2026-09-20, 1.0.0. The surround is no longer painted: the window is only as big as the picture, so
whatever it does not cover stays the wallpaper Windows was already showing. Display aspect became a crop
rather than a squeeze, then an OBS-style output resolution with four mapping modes, so the picture comes
out at the size asked for whatever the capture resolution is. Fixed 16:9 and 4:3 entries went away. A
3x3 screen position places the output on the monitor and greys itself out when there is no room, saying
why. A line under the settings spells out the resulting geometry. Measured on a Live Gamer BOLT: the
pillarbox is exactly 480px each side of a 3840x2160 frame, and `Fill` cuts `2880x2160` at x=480 to
match. 60fps received and 60fps on screen at both 1080p and 4K. All automatic checks pass and a
published build was run through **Apply to desktop**.

2026-09-19, 4K latency fix: the cause was the throughput ceiling of the redirected stdout pipe. Moving to a named pipe took 4K reception from 24.3fps to 59.9fps (1895 MB/s). The renderer also moved from timer polling to presenting when a frame arrives. The DirectShow queue size was corrected to use the input format, so a 150ms setting at 4K is 112 MB (9 frames) rather than 298 MB (24 frames). In steady state both 1080p and 4K receive at 60fps and reach the screen at 60fps. Verified on a published build all the way through applying 4K.

2026-09-19, desktop output fix: capture not appearing on the desktop at all was fixed by replacing the GDI renderer with a DXGI swap chain. The `--desktop` check matched 95% of real screen pixels against the received frame, and the 4:3 bars came out black. A published build was run and **Apply to desktop** pressed to confirm the Live Gamer BOLT picture reaches the desktop. All automatic checks pass.

2026-09-19, color fix: automatic checks pass. 542 frames received from a Live Gamer BOLT over ten seconds. The device was showing its own No Signal screen at the time, so comparing against a reference image of real iPad content was deferred. A sandbox may be denied device access, so run this with ordinary Windows process rights.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).

This is not a free choice: the bundled FFmpeg is built `--enable-gpl --enable-version3`, and the bundled LibVLC is the GPL build. Distributing the result therefore carries GPLv3 obligations, including providing source for those components. A permissive license would require dropping both bundled binaries.
