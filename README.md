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

Video keeps its own shape and loops without a seam: the player repeats the file itself rather than being stopped and started, which used to leave the desktop bare for about a fifth of a second every time round. Either input can be placed on the monitor the same way; see [Placement](#placement). Both are muted by default and can be unmuted; see [Sound](#sound). The capture buffer value sets the DirectShow queue capacity and is not an exact latency figure. That capacity is worked out from the frame size of the format the device emits, so the same value holds the same number of frames as the resolution goes up. The screen always shows the newest frame, presented as soon as it arrives.

### Capture settings

| Setting | Choices / default |
| --- | --- |
| Pixel format | **NV12**, YUY2, UYVY, RGB24, MJPEG |
| Resolution / FPS | **1920×1080 / 60**, 720p·1440p·4K and others / 59.94·50·30 and others |
| Color space | **Rec.709**, Rec.601, Rec.2020 (SDR conversion matrix) |
| Color range | **Limited**, Full |
| Input HDR / peak | **SDR**, HDR10 / PQ → SDR, HLG → SDR · **1000** nits |
| Sound / rate | taken from the device's own audio pin when it has one · **muted** · **44100**, 48000, 32000 Hz |

Nothing is guessed. The device is opened with exactly the values you chose and YUV→RGB uses exactly the matrix you chose. If the device does not support a combination you get an error rather than a silent switch to another format. The YUV matrix and range selections do not affect RGB input. Rec.2020 does not mean HDR tone mapping. Press **Apply to desktop** for a change to take effect; settings persist across runs. The lists are common presets — querying a device for its own supported modes is not implemented yet.

For a Live Gamer BOLT, start with **NV12 / 1920×1080 / 60 / Rec.709 / Limited / Input resolution**. If blacks look raised or shadow detail is crushed, change the color range to match the actual source. If the card pillarboxes a 4:3 source into its 16:9 frame, set **Display aspect** to `Output resolution` with `Fill, cropping the overflow` and those bars come off.

### Sound

Everything starts muted. Clear **Mute** to hear the input.

A capture device's sound comes off the device itself, on a pin beside the picture, and there is nothing
separate to choose: it is taken whenever the device offers any and silenced by the checkbox, which is
what makes the toggle instant rather than a restart of the capture. Not every device has any. A virtual
camera sends a picture and nothing else - OBS's does not carry audio and registers no audio device to
go with it - and for those the checkbox greys out and says why on hover. To hear an OBS scene, send its
audio to a virtual audio device and capture that, alongside the virtual camera, as the sound source.

The sound format is stated rather than left to the engine, for the same reason the pixel format is.
A pin offers several layouts and the engine takes the first one that fits what it was asked for, so
asking for a rate and not a channel count is worse than asking for nothing at all: on a card that also
offers 7.1, the first format matching `48000` is the eight channel one, and two channels then arrive
as eight. Two channels are asked for explicitly, and the rate with them.

**Sound rate** has to match what the device actually sends. Measured on a Live Gamer BOLT (GC555): it
advertises 48000 and 32000 alongside 44100, but when opened at 48000 it keeps sending 44100 samples a
second under the 48000 label, so ten seconds of sound arrives in 9.19 - 8.8% fast, which is exactly
44100/48000. Nothing here compensates for that. A device that misreports its own format is not
something to build a correction around, and a correction would be wrong on every device that does not
need it. Leave the rate at what the card really sends; on that card, 44100.

The picture and the sound travel separately, so they are not locked together to the sample. The sound
is buffered by the capture buffer value, the same dial that trades latency for a steady picture.

### Placement

| Setting | Choices / default |
| --- | --- |
| Display aspect | **Input resolution**, Stretch to screen, Output resolution |
| Output size / mapping | e.g. `2753x2064` · **Fill, cropping the overflow**, Fit / Centre at 1:1 / Stretch to fill |
| Screen position | 3x3 grid, **Center** |

These three belong to either input and stay on show for both. A capture is placed by the resolution you
chose for it; a video file is placed by the shape the file turns out to be, which is read off the file
with the bundled FFmpeg as soon as you choose it, anamorphic pixels included.

**Display aspect** decides what happens to the frame before it reaches the desktop.
`Input resolution` leaves it alone. `Stretch to screen` fills the monitor and distorts to do it.
`Output resolution` is the one that behaves like OBS: you give a size such as `2753x2064` and the
picture comes out at that size **whatever the source resolution is**. Capture at 1920x1080 or at
3840x2160 and the result is still 2753x2064; only how much detail went into it changes. **Output
mapping** says how the frame is laid into that rectangle, always centred inside it:

- `Fill, cropping the overflow` scales the frame until it covers the whole output and cuts off whatever
  hangs over. Nothing is distorted and no gap is left. This is the one that takes a pillarbox off: on a
  3840x2160 capture of a 4:3 source it cuts `2880x2160` at x=480, exactly where the bars end.
- `Fit, padding the gap` keeps the entire frame and shrinks it until it sits inside the output, so a
  shape mismatch shows up as a gap rather than as a missing edge.
- `Centre at 1:1, padding the gap` does not scale at all. It takes as much of the middle of the frame as
  the output has room for and draws it one source pixel per screen pixel, so it never grows past what
  the source actually has: a 1920x1080 frame stays 1920x1080 even if the output asks for more.
- `Stretch to fill, distorting` keeps the whole frame and stretches it onto the output exactly. This is
  the one for an older card that squeezes the source into its frame instead of pillarboxing it, where
  there is nothing to cut and the picture only needs its proportions back.

A gap is not painted black - it is simply not covered, so your own wallpaper shows through it. An output
bigger than the monitor is scaled down to fit, keeping its shape.

The line under the settings spells out what the current numbers do - what is cut out of the frame, how
big it is drawn and where - so there is no need to guess which reading is in force.

**Screen position** is the 3x3 grid, read like a canvas-size anchor: it decides where on the monitor the
picture sits. It places the output rectangle; what goes inside that rectangle is always centred. It can
only do something where the picture leaves room, so the grid greys out when there is none - a 16:9 source shown whole on a 16:9 monitor already covers every pixel, and so does anything
stretched to the screen. In video mode it also greys out until a file has been chosen, because until
then there is no shape to place. The crop itself always comes out of the middle of the frame, because
that is where a pillarbox puts the bars.

`Input resolution` and `Stretch to screen` look identical whenever the source is the same shape as the
monitor, which is the usual case. They part company as soon as it is not: a 640x480 capture on a 16:9
monitor is drawn 4:3 and undistorted by the first, and stretched to fill by the second.

There are no fixed 16:9 / 4:3 entries. Under cropping they would only be a clumsier output resolution, and
if a fixed ratio is ever wanted it will be wanted as a stretch, not a crop.

Cropping a capture happens in the capture engine, so the bars never travel down the pipe in the first
place. Cropping a video happens in the player, which costs nothing, because the decoder was going to
hand over a whole frame either way.

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
`artifacts/Wallcast-v1.1.0-win-x64.zip`, about 164 MB, which is the single file to upload. The version
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
match: `git tag -a v1.1.0 -m "Wallcast v1.1.0"`.

Because FFmpeg and LibVLC are GPL, so is anything you hand out that contains them. `LICENSE` and
`THIRD-PARTY.txt` are published into the folder for that reason - the latter lists every component, its
licence and where its source lives. Keep both in any archive you distribute.

## Scope and layout

- `Sources.cs`: the file and capture input models.
- `Placement.cs`: output resolution, mapping mode and screen anchor. Shared, so a video file and a capture of the same shape are placed by the same arithmetic.
- `Playback.cs`: picks the playback path per input, LibVLC video playback, seamless looping, the crop and display aspect it hands the player, errors and cleanup.
- `VideoProbe.cs`: reads a video file's displayed shape out of the bundled FFmpeg, so a file can be placed like a capture.
- `CaptureOptions.cs`: pixel format, resolution, FPS and YUV conversion, plus the crop that takes the capture card's black bars off. The geometry itself belongs to `Placement.cs`.
- `CapturePlayback.cs`: FFmpeg DirectShow input, explicit color conversion, keeps the newest frame. Sound, when the device has any, leaves the same process on stdout as WAV and is played from that stream. Frames arrive over a named pipe. A redirected stdout pipe has a small buffer and stalls near 800 MB/s, while 4K 60fps BGRA needs 2.0 GB/s.
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

The automatic checks run with `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true`. They cover VLC loading, device discovery, invalid input, output resolution and every mapping mode, screen anchors, that a video file and a capture of the same shape are placed identically, real FFmpeg NV12 red/blue conversion, Limited/Full, the Rec.709/601 difference, and repeatable playback cleanup.

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

Video output check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --video`. It builds a two second clip with 240px of black bar either side of a solid colour - the pillarbox this app exists to remove - plays it on the desktop, and then reads the screen flat out for ten seconds, five times round the loop. Every read that is not the picture is a hole, and a hole at the loop is the bug this check exists for: the old stop-and-restart loop showed 117 of 1176 reads bare, and repeating the input shows 0 of 1201. It also measures how far the picture reaches inside the window it was given, which catches both an uncropped bar and a gap the wallpaper shows through. `--custom 1440x1080` sets an output resolution (`--pad`, `--pixels`, `--squeeze` for the other three mappings), `--aspect` and `--anchor Left` work as above, `--seconds` sets the clip length, and a path right after `--video` plays that file instead, in which case the geometry is reported but not judged. The grab is saved to `artifacts/desktop-video.png`. Like the capture check it minimises windows, so it is not part of the automatic run.

Form check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --ui-preview`. It renders the settings window in both input modes to `artifacts/settings-capture.png` and `artifacts/settings-video.png`, and fails if the placement settings go missing from either, if the capture settings show up in video mode, or if the greyed-out anchor grid carries no explanation of why.

Throughput check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --bench "Live Gamer BOLT"`. It reports frames per second and MB/s actually received at 1080p, 1440p and 4K. Anything slower than the device emits means that difference in frames piling up in the queue as latency. Start here for latency problems.

Real device check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --capture "Live Gamer BOLT"`. It opens NV12/Rec.709/Limited/1080p60 for ten seconds and checks that frames arrive. Adding `--snapshot` saves one frame to `artifacts/capture-nv12-rec709.png`. Adding `--sound` also asks the device for its audio and writes what the player decodes out of the engine's stdout to `artifacts/capture-sound.wav`, so the size of that file says whether sound ran at real time for the whole ten seconds. The app itself never records or saves the screen.

Sound path check: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --sound "Live Gamer BOLT"`. This one drives the app's own playback: it puts the device on the desktop with sound, mutes and unmutes it while it runs, and times the stop. The timing is the point. The player reads the engine's stdout, and a read there only returns once there is data or the writer is gone, so stopping in the wrong order would park the UI thread on a read nothing is going to answer. It ends the engine first, and the stop is expected to take well under a second.

2026-09-20, capture sound. The Mute checkbox works for capture as well as video, and a capture device
hands over the sound it sends alongside the picture. Both are muted to begin with. It travels as WAV on
the engine's stdout, which the picture could never use - it stalls near 800 MB/s - but stereo PCM is a
thousandth of that traffic. Measured: 1593 KB decoded over 9.25s, which is 44.1kHz stereo at real time
exactly, with no effect on the frame rate. Two things were settled by measurement rather than
assumption: a capture card will not hand its sound over as a device of its own, so it has to be asked
for on the same input as the picture, and OBS's virtual camera has no sound at all - it registers no
audio device to go with the camera - so the checkbox greys out and says so.

The sound format is now stated in full, two channels and a rate, because asking for a rate alone let
the engine pick the pin's eight channel layout. Measured on the same card: it advertises 48000 but
sends 44100 under that label, 8.8% fast. That is left uncorrected and documented; the rate is a
setting so it can be matched to whatever the device really does.

2026-09-20, 1.1.0, video parity. The video path caught up with capture. It takes the same output resolution,
mapping and screen position, placed by the same arithmetic against the shape the file turns out to be,
read off the file with FFmpeg rather than by opening it a second time in the player. Its loop stopped
showing the desktop: repeating the input keeps the video output alive across the seam, where stopping
and restarting the player tore it down and left a hole for about 200ms - 117 of 1176 screen reads
before, 0 of 1201 after. Two things about VLC had to be measured rather than assumed: it reads a crop
written as `WxH+X+Y` as edges rather than as an origin and a size, which made a 1440-wide crop come out
1200 wide, and it works the display aspect out from the whole decoded frame rather than from the part
the crop left. Both are corrected, and all four mappings now land the picture exactly on its window at
1440x1080, 1920x1080, 1200x900 and 1920x600. All automatic checks pass.

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
