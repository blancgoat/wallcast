# Wallcast

<img width="3836" height="2160" alt="image" src="https://github.com/user-attachments/assets/c82c550f-b159-4199-89da-cc70eb265aae" />


[한국어](README.ko.md)

A small live wallpaper app for Windows.

- **A capture card or virtual camera runs on your desktop**, behind the icons, on the monitor you pick. Literally the wallpaper.
- **A video file works too.** If you run Wallpaper Engine for a video wallpaper, this takes its place.
- **Sound comes with it**, if you want it. Off to begin with.
- **Click it and it clicks back** (beta). A click on the wallpaper reaches the device as a tap, over
  Bluetooth. It aims true as long as the picture is exactly the device's screen; a placement that
  leaves the source's black bars in shifts the target with it.

## Install

Windows 10 or 11, x64. Download `Wallcast-v1.3.0-beta.1-win-x64.zip` from
[Releases](https://github.com/blancgoat/wallcast/releases), unpack it anywhere and run
`Wallcast\Wallcast.exe`.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).

This is not a free choice: the bundled FFmpeg is built `--enable-gpl --enable-version3`, and the
bundled LibVLC is the GPL build. Distributing the result therefore carries GPLv3 obligations,
including providing source for those components.

---

## Build

Only needed if you want to build it yourself; the release zip above needs none of this. Windows x64
and the .NET 8 SDK. The first line fetches the capture engine, which is not in the repository.

```powershell
./scripts/setup-capture.ps1
dotnet run --project Wallcast
dotnet publish Wallcast -c Release -r win-x64 --self-contained true -o artifacts/Wallcast
```

Publish into an empty folder - overwriting an existing one can leave a stale framework assembly
behind and the app then fails at startup. `./scripts/package.ps1` does the whole release: a clean
publish plus the zip, and it refuses to package a build missing the capture engine, the VLC runtime
or either licence file.

The automatic checks run with
`dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true`.
