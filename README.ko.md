# Wallcast

<img width="3840" height="2160" alt="스크린샷 2026-09-20 140240" src="https://github.com/user-attachments/assets/5054b539-527d-422a-bbba-d84a6cec4ea7" />


[English](README.md)

Windows용 작은 라이브 바탕화면 앱.

- **캡처보드나 가상 카메라가 바탕화면에 뜹니다.** 고른 모니터의 아이콘 뒤에서 돌아갑니다. 말그대로 바탕화면
- **동영상 파일도 됩니다.** 동영상기반 월페이퍼엔진을 사용중이시라면 이걸로 대체가능.
- **소리도 납니다.** 원하면요. 기본은 꺼짐입니다.

## 설치

Windows 10 또는 11, x64. [Releases](https://github.com/blancgoat/wallcast/releases)에서
`Wallcast-v1.2.0-win-x64.zip`을 받아 아무 곳에나 풀고 `Wallcast\Wallcast.exe`를 실행하세요.

## 라이선스

GPL-3.0-or-later. [LICENSE](LICENSE) 참고.

선택의 여지가 있는 부분이 아닙니다. 동봉된 FFmpeg는 `--enable-gpl --enable-version3`로 빌드됐고
동봉된 LibVLC도 GPL 빌드입니다. 따라서 배포물 전체에 GPLv3 의무가 따르며 해당 구성 요소의 소스
제공도 포함됩니다.

UI와 앱 메시지는 영어입니다. 이 문서는 한국어판이고 표준 문서는 [README.md](README.md)입니다.

---

## 빌드

직접 빌드할 때만 필요합니다. 위 릴리스 zip을 쓰면 아무것도 필요 없습니다. Windows x64와 .NET 8
SDK가 있어야 하고, 첫 줄이 저장소에 없는 캡처 엔진을 받아옵니다.

```powershell
./scripts/setup-capture.ps1
dotnet run --project Wallcast
dotnet publish Wallcast -c Release -r win-x64 --self-contained true -o artifacts/Wallcast
```

publish는 빈 폴더에 하세요. 기존 폴더에 덮어쓰면 옛 프레임워크 어셈블리가 남아 실행 시 로드
실패가 납니다. `./scripts/package.ps1`이 릴리스 전체를 처리합니다. 깨끗한 publish에 zip까지
만들며, 캡처 엔진·VLC 런타임·라이선스 파일 중 하나라도 빠지면 패키징을 거부합니다.

자동 검증은 `dotnet run --project SmokeTests -c Release -r win-x64 --self-contained true`로
실행합니다.
