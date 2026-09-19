# Wallcast

[English](README.md)

Windows용 작은 라이브 바탕화면 앱. 캡처보드나 가상 카메라 — 또는 동영상 파일 — 를 모니터 하나의 바탕화면 아이콘 뒤에 표시합니다.

## 사용

1. `artifacts/Wallcast/Wallcast.exe` 실행.
2. 기본값인 **캡처보드 / 가상 카메라**에서 장치를 선택하거나, **동영상 파일**에서 파일 선택.
3. 출력 모니터를 선택하고 **바탕화면 적용**.
4. **중지**하면 기존 배경이 다시 보입니다. 창의 X는 트레이로 숨기며, 완전 종료는 트레이 메뉴의 **종료**입니다.

동영상은 원본 비율을 유지하며 반복됩니다. 기본 음소거이며 해제할 수 있습니다. 캡처는 영상만 출력합니다. 캡처 버퍼 값은 DirectShow 큐 용량을 정하는 기준이며 정확한 지연 시간이 아닙니다. 큐 용량은 장치가 내보내는 형식의 프레임 크기로 계산하므로, 같은 값이면 해상도가 올라가도 담기는 프레임 수는 같습니다. 화면 출력은 가장 최근 프레임을 사용하며, 프레임이 도착할 때마다 바로 표시합니다.

### 캡처 설정

| 설정 | 선택지 / 기본값 |
| --- | --- |
| 영상 형식 | **NV12**, YUY2, UYVY, RGB24, MJPEG |
| 해상도 / FPS | **1920×1080 / 60**, 720p·1440p·4K 등 / 59.94·50·30 등 |
| 색 공간 | **Rec.709**, Rec.601, Rec.2020 (SDR 변환 행렬) |
| 색 범위 | **Limited**, Full |
| 표시 비율 | **Input resolution**, 16:9, 4:3, 16:10, Stretch to screen, Custom size |
| 사용자 크기 / 맞춤 | 예: `2732x2048` · **Fit to screen**, Actual pixels |

설정은 자동 추측하지 않고 선택한 값으로 장치를 열고 YUV→RGB 변환합니다. 장치가 지원하지 않는 조합이면 오류를 표시하며 다른 형식으로 몰래 전환하지 않습니다. RGB 입력에는 YUV 행렬/범위 선택이 영향을 주지 않습니다. Rec.2020은 HDR 톤 매핑을 뜻하지 않습니다. 선택 후 **바탕화면 적용**을 눌러야 반영되며 설정은 다음 실행에도 유지됩니다. 현재 설정 목록은 공통 프리셋으로, 장치별 지원 모드 자동 조회는 아직 없습니다.

Live Gamer BOLT는 우선 **NV12 / 1920×1080 / 60 / Rec.709 / Limited / Input resolution**로 사용하세요. 검정이 뜨거나 어두운 부분이 뭉개지면 실제 소스에 맞게 색 범위를 변경하세요. 이전 버전처럼 전체 입력을 강제로 4:3으로 압축하지 않습니다. 원본에 포함된 여백은 그대로 유지합니다.

**표시 비율**은 화면 가운데에 그림을 그릴 사각형의 모양을 정합니다. 잘라내지 않고, 장치가 캡처하는
내용도 바꾸지 않습니다. 프레임 전체를 그 사각형에 늘려 넣으므로, 소스와 다른 모양을 고르면 찌그러집니다.
`Input resolution`이 소스 본래 모양이라 왜곡이 없고, 고정 비율은 장치가 보고하는 해상도와 실제 모양이
다를 때 쓰라고 있는 값입니다.

`Custom size`는 아이패드의 `2732x2048` 같은 임의의 크기를 받습니다. `Fit to screen`은 그 비율을
유지한 채 화면 가장자리에 닿을 때까지 키웁니다. `Actual pixels`는 딱 그 픽셀 수로 가운데 놓습니다.
소스 해상도와 모니터가 딱 떨어지지 않아 아예 리스케일하고 싶지 않을 때 쓰면 됩니다.

그림이 덮지 않는 부분에는 **아무것도 그리지 않습니다.** 앱 창 자체가 그림 크기만큼만 있어서, 남는
영역은 원래 쓰던 바탕화면 그대로입니다. 검은 여백이 생기지 않으니 주변에 뭘 둘지는 Windows에서
직접 정하시면 됩니다.

OBS의 장면/데스크톱을 사용하려면 OBS에서 가상 카메라를 시작하고 해당 장치를 선택하세요. 네이티브 데스크톱 캡처 입력은 아직 구현하지 않았습니다. 자기 바탕화면을 캡처하면 반복 화면이 발생하므로 다른 모니터나 창을 소스로 사용하세요.

## 빌드

Windows x64와 .NET 8 SDK 필요. 최초 빌드 전에 캡처 엔진을 준비합니다. 배포 폴더에는 VLC와 FFmpeg가 포함되므로 사용자는 별도 설치가 필요하지 않습니다.

```powershell
./scripts/setup-capture.ps1
dotnet run --project Wallcast
dotnet publish Wallcast -c Release -r win-x64 --self-contained true -o artifacts/Wallcast
```

프로젝트 로컬 SDK가 있으면 `dotnet` 대신 `.\.tools\dotnet\dotnet.exe` 사용. 배포 시 `artifacts/Wallcast` 폴더 전체가 필요합니다. 단일 exe 배포가 아닙니다. publish는 빈 폴더에 하세요. 기존 폴더에 덮어쓰면 프레임워크 어셈블리가 패키지 버전보다 최신 타임스탬프를 가진 경우 교체되지 않아, 실행 시 `System.Text.Json` 로드 실패가 납니다.

## 범위와 구조

- `Sources.cs`: 파일/캡처 입력 모델.
- `Playback.cs`: 입력별 재생 경로 선택, LibVLC 동영상 재생, 반복, 오류와 자원 정리.
- `CaptureOptions.cs`: 픽셀 형식·해상도·FPS·YUV 변환·표시 비율.
- `CapturePlayback.cs`: FFmpeg DirectShow 입력, 명시적인 색 변환, 최신 프레임 유지. 프레임은 이름 있는 파이프로 받습니다. 리디렉션된 stdout 파이프는 버퍼가 작아 약 800MB/s에서 막히는데, 4K 60fps BGRA는 2.0GB/s가 필요합니다.
- `CaptureSurface.cs`: BGRA 프레임을 DXGI 플립 모델 스왑 체인으로 출력하고 비율을 유지합니다. 확대/축소는 GPU가 처리합니다.
- `DesktopHost.cs`: Windows Explorer WorkerW에 영상 창 연결.
- `CaptureDevices.cs`: DirectShow 비디오 장치 검색.
- `MainForm.cs`: 설정, 모니터 선택, 트레이, 로컬 설정 저장.
- `scripts/make-icon.ps1`: `Wallcast.ico`를 그립니다. 큰 그림을 줄이면 아이콘 실루엣 두 개가 뭉개져서
  크기마다 따로 그립니다. `-PngDirectory`를 주면 PNG로도 내보냅니다.

설정은 `%LOCALAPPDATA%\Wallcast\settings.json`에 저장합니다. 앱 시작 시 자동 재생하거나 Windows 시작 프로그램에 등록하지 않습니다. 다중 모니터 동시 재생, 편집기, 웹 배경, 워크숍은 포함하지 않습니다.

바탕화면에 붙는 Explorer 창에는 GDI 리디렉션 표면이 없습니다. 그래서 그 안에서는 GDI로 그린 픽셀이 화면에 합성되지 않으며, 영상(LibVLC의 Direct3D11 출력)과 캡처(위 스왑 체인) 모두 D3D 경로로만 표시됩니다. 캡처를 GDI로 그리면 프레임이 정상적으로 들어오고 컨트롤이 칠해져도 바탕화면에는 아무것도 나오지 않습니다.

WorkerW 방식은 공개된 Windows 바탕화면 API가 아니므로 Windows/Explorer 버전에 따라 동작 차이가 있습니다. Explorer 재시작이나 모니터 구성이 바뀌면 재생을 중지하고 재적용을 안내합니다. DirectShow에 노출되는 장치만 지원하며 제조사 전용 SDK만 지원하는 장치는 대상이 아닙니다. 같은 이름의 장치 여러 개는 구분을 보장하지 않습니다.

## 수동 검증

자동 검증은 `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true`로 실행합니다. VLC 로딩, 장치 검색, 잘못된 입력, 표시 비율, 실제 FFmpeg의 NV12 빨강/파랑 변환, Limited/Full, Rec.709/601 차이, 재생 정리를 검사합니다.

- 짧은 영상이 끝난 뒤 반복되는지, 음소거 전환과 중지가 되는지.
- 바탕화면 아이콘과 컨텍스트 메뉴가 정상 작동하는지.
- 보조 모니터 및 서로 다른 DPI에서 선택한 화면에 맞게 표시되는지.
- 캡처 장치/OBS 가상 카메라 적용, 장치 분리, 다른 앱의 장치 점유 시 오류 처리.
- 입력 전환, 트레이 숨김/복원/종료 후 장치 해제 및 원래 배경 복원.
- Explorer 재시작, 모니터 분리 후 재적용 안내.

동영상은 LibVLCSharp/VideoLAN.LibVLC.Windows.GPL, 캡처 입력은 FFmpeg 9.0.1 Gyan essentials 빌드, 캡처 출력은 Vortice.Direct3D11을 사용합니다. `scripts/setup-capture.ps1`은 버전과 SHA-256을 고정합니다. `capture/LICENSE-FFmpeg.txt` 및 `capture/README-FFmpeg.txt`를 유지하세요.

바탕화면 출력 검증: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --desktop "Live Gamer BOLT"`. 장치를 열어 바탕화면에 붙이고, 열린 창을 잠시 최소화한 뒤 실제 화면을 캡처해 수신한 프레임과 비교합니다. `--resolution`과 `--aspect`로 해상도와 표시 비율을, `--custom 2732x2048`(`--actual`을 붙이면 실제 픽셀)로 사용자 크기를 지정할 수 있고, 받은 fps와 화면에 나온 fps를 따로 보고합니다. 그림이 화면을 다 덮지 않으면 남는 영역이 원래 바탕화면과 같은지도 확인하고, 그중 몇 개가 원래부터 검지 않았는지 같이 알려줍니다. 바탕화면이 검으면 정상 동작과 검은 여백을 구분할 수 없기 때문입니다. `artifacts/desktop-capture.png`에 그때의 화면을 남깁니다. 창을 최소화했다가 되돌리므로 자동 검증에는 포함하지 않습니다. 프레임 수신만으로는 화면 출력이 증명되지 않기 때문에 필요한 검사입니다.

이 검사는 최소화되지 않고 남은 창의 영역을 빼고 비교하며, 가려지지 않은 영역이 20% 미만이면 실패가 아니라 SKIP입니다. 움직이는 소스에서는 화면을 찍는 순간과 프레임을 받는 순간이 어긋나므로 앞뒤로 받은 여러 프레임 중 가장 잘 맞는 것과 비교합니다. 둘 다 없으면 렌더러가 멀쩡해도 실패로 보입니다.

처리량 검증: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --bench "Live Gamer BOLT"`. 1080p·1440p·4K에서 실제로 받은 초당 프레임과 MB/s를 보고합니다. 장치가 내보내는 속도보다 느리면 그 차이만큼 프레임이 큐에 쌓여 지연이 됩니다. 지연 문제는 여기서 먼저 확인하세요.

실제 장치 검증: `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true -- --capture "Live Gamer BOLT"`. 10초간 NV12/Rec.709/Limited/1080p60으로 열어 프레임 수신을 검사합니다. `--snapshot`을 추가하면 로컬 `artifacts/capture-nv12-rec709.png`에 한 프레임을 저장합니다. 기본 앱은 화면을 녹화하거나 저장하지 않습니다.

2026-09-19 4K 지연 수정: 4K에서 지연이 심하던 원인은 리디렉션된 stdout 파이프의 처리량 한계였습니다. 이름 있는 파이프로 바꿔 4K 수신이 24.3fps에서 59.9fps(1895MB/s)로 올랐습니다. 렌더러도 타이머 폴링에서 프레임 도착 시점 표시로 바꿨습니다. DirectShow 큐 크기도 입력 형식 기준으로 고쳐, 150ms 설정이 4K에서 298MB(24프레임)가 아니라 112MB(9프레임)가 됩니다. 정상 구간에서 1080p·4K 모두 수신 60fps, 화면 출력 60fps입니다. 배포본에서 4K 적용까지 확인했습니다.

2026-09-19 바탕화면 출력 수정: 캡처가 바탕화면에 전혀 나오지 않던 문제를 GDI 렌더러에서 DXGI 스왑 체인으로 바꿔 해결했습니다. `--desktop` 검증에서 실제 화면 픽셀의 95%가 수신 프레임과 일치했고, 4:3에서 여백도 검게 나왔습니다. 배포본을 실행해 **바탕화면 적용**까지 눌러 Live Gamer BOLT 화면이 바탕화면에 나오는 것을 확인했습니다. 자동 검증 전체 통과.

2026-09-19 색상 수정 검증: 자동 검증 통과. Live Gamer BOLT에서 10초간 542프레임 수신. 당시 장치 자체의 No Signal 화면이 수신되어 실제 아이패드 콘텐츠의 기준 이미지 비교는 보류했습니다. 샌드박스에서는 장치 접근이 거부될 수 있어 일반 Windows 프로세스 권한으로 실행해야 합니다.

## 라이선스

GPL-3.0-or-later. [LICENSE](LICENSE) 참고.

선택의 여지가 있는 부분이 아닙니다. 동봉된 FFmpeg는 `--enable-gpl --enable-version3`로 빌드됐고 동봉된 LibVLC도 GPL 빌드입니다. 따라서 배포물 전체에 GPLv3 의무가 따르며 해당 구성 요소의 소스 제공도 포함됩니다. 허용적 라이선스를 쓰려면 두 바이너리를 동봉하지 않아야 합니다.

UI와 앱 메시지는 영어입니다. 이 문서는 한국어판이고 표준 문서는 [README.md](README.md)입니다.
