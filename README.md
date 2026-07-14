<p align="center">
  <img src="src/Snapfield.App/Assets/snapfield.png" alt="Snapfield 아이콘" width="120" height="120">
</p>

<h1 align="center">Snapfield</h1>

Windows 전용 멀티-PC / 멀티-모니터 커서·키보드 공유 툴.
**Mouse Without Borders**(네트워크 너머 PC 공유)와 **LittleBigMouse**(물리 좌표 기반 정밀 배치)를 결합한다.

## 핵심 아이디어

모든 PC의 모든 모니터를 **하나의 전역 물리 좌표 평면(mm 단위)** 에 배치한다.
커서가 어느 화면 경계를 넘든, 그 위치를 전역 mm로 환산 → 이웃 모니터(같은 PC든 다른 PC든)를
찾아 → 그 PC의 픽셀 좌표로 되돌려 커서를 놓는다. 해상도·물리 크기·DPI·상하 오프셋이 서로 달라도
커서가 **물리적으로 같은 높이**에서 자연스럽게 넘어간다 (MWB의 Y좌표 튐 문제 해결).

## 구조

```
Snapfield.sln
├─ src/
│  ├─ Snapfield.Core/        좌표계 모델 + 3단계 변환 파이프라인 (순수 C#, DI 없음)
│  │   ├─ Geometry/          PhysicalPoint/Rect (mm), PixelRect (물리 픽셀)
│  │   ├─ Model/             MonitorInfo, DesktopLayout (전역 평면)
│  │   └─ Transforms/        CoordinateMapper — pixel↔physical, hit-test, resolve
│  ├─ Snapfield.Platform/    Win32 P/Invoke + WMI (net8.0-windows)
│  │   ├─ Interop/           NativeMethods (DPI, EnumDisplayMonitors, EnumDisplayDevices)
│  │   └─ Monitors/          MonitorEnumerator — 실물 열거 + EDID 물리 크기
│  ├─ Snapfield.App/         WPF 앱 — 한 창 3탭(연결 · 모니터 배치 · 설정), 트레이 상주
│  └─ Snapfield.LinuxReceiver/  리눅스 수신 데몬 (콘솔, uinput 주입, x64/arm64) — Wayland/X11
├─ android/                  안드로이드 수신 앱 — 폰/태블릿을 평면 위 모니터처럼 (커서·한글 키보드·양방향 클립보드)
└─ tests/
   └─ Snapfield.Core.Tests/  단위 테스트 56개 (좌표 변환 · 커서 라우터 · 경계 스캔 · 레이아웃 병합 · 와이어 프로토콜)
```

## 좌표 변환 파이프라인 (CoordinateMapper)

1. **pixel → physical**: 어느 PC의 픽셀 커서를 전역 mm 평면으로. `physical = 물리오프셋 + (픽셀/해상도)·물리크기`
2. **hit-test**: 전역 평면에서 그 점을 품은 모니터 찾기 (없으면 최근접 모니터로 clamp — 커서 유실 방지)
3. **physical → pixel**: 목적지 모니터에서 역변환 → 그 PC가 이해하는 픽셀 좌표

DPI 스케일링(125%/150%)은 **프로세스를 Per-Monitor-V2 aware로 만들어** 중화한다.
그러면 Windows가 스케일된 픽셀이 아닌 **물리 픽셀**을 보고하므로, 변환은 순수 기하 계산이 된다.

## 현재 상태 (검증됨)

- [x] 솔루션 + 4 프로젝트 골격, 전체 빌드 통과
- [x] Core 좌표계 모델 + CoordinateMapper — 화면 간 물리 높이 유지 크로싱 (단위 테스트 56개)
- [x] MonitorEnumerator — 실기기에서 EDID 물리 크기 읽기 검증 (Win32 ↔ WMI를 PnP id로 매칭)
- [x] **배치 캔버스**: 드래그 물리 배치(자동 저장), 엣지 스냅, 휠 확대/이동, 인스펙터 패널(크기 보정·종류 전환·제거), **경계 통과 표시**(드래그 중에만 — 평소엔 조용), **크기 적응형 카드**(폰처럼 작은 기기는 이름만 크게, 확대하면 상세 복귀), 배치 프리셋
- [x] 레이아웃 영속화 (`%APPDATA%\Snapfield\layout.json`) + 재감지 시 저장된 배치 병합
- [x] **입력 엔진**: `WH_MOUSE_LL`/`WH_KEYBOARD_LL` 후킹 + `SendInput` 주입 + 물리 좌표 라우터
  - 엣지 크로싱 감지 → 원격 모니터로 핸드오프 → 커서 캡처(로컬 중앙 파킹) → 델타 누적 → 복귀 시 워프
  - 연결이 활성화되면 자동으로 동작 (별도 화면 없음)
- [x] **네트워크**: 암호화 TCP(ECDH → AES-256-GCM), 연결 코드 인증, 자동 재연결, LAN 자동 발견, 멀티 수신 허브, 클립보드(텍스트·이미지·파일). 고빈도 입력 메시지(커서/버튼/휠/키)는 바이너리 fast-path, 나머지는 source-gen JSON
- [x] **관리자 실행** (v0.15): 관리자 권한 창(작업 관리자, 관리자 cmd 등) 위에서도 후킹·주입이 동작하도록 앱이 관리자로 실행됨(실행 시 UAC 1회). 로그인 시 자동 실행은 작업 스케줄러 태스크로 등록되어 부팅 후 UAC 없이 시작
- [x] **수신 기기 3종**: Windows(exe) · 안드로이드(apk, v0.13) · **리눅스(x64/arm64 데몬, v0.16)** — 우분투 데스크톱 Wayland/X11, 커널 레벨 uinput 주입, 클립보드 양방향

## 사용법

> 📘 처음이라면 **[설치 가이드](docs/install-guide.md)** — EXE/APK 설치부터 연결·단축키·문제 해결까지 단계별 안내.
> 📄 프로그램이 뭘 하는지 궁금하다면 **[프로그램 소개](docs/overview.md)** — 핵심 아이디어·기능·보안·아키텍처 요약.

두 PC 모두 [릴리스](https://github.com/Q07K/Snapfield/releases)의 exe를 실행하면 끝 (설치·.NET 불필요).

1. **조작당할 PC**: 연결 탭에서 **수신 기기** 선택 → 즉시 대기 시작, 화면에 IP와 초록 **연결 코드 6자리**가 표시됨
2. **조작할 PC**: **조작 기기** 선택 → "기기 추가" 목록에 상대가 자동으로 나타남(● 온라인) → 탭하고 **코드 6자리 입력** → 자동 연결
3. **모니터 배치 탭**: 모니터를 실제 책상 배치대로 드래그 (놓는 즉시 저장·라이브 반영). 초록 ‹·› 화살표가 커서가 건너는 구간을 보여줌
4. 커서를 화면 끝으로 밀면 상대 PC로 넘어감. 클릭·키보드·클립보드(파일 포함)가 함께 전달됨

**단축키** — `Ctrl+Alt+←→`: 기기 전환 스트립(Ctrl+Alt를 누른 채 화살표로 선택, 떼면 이동, Esc 취소) · 원격 조작 중 `Ctrl` 3연타: 즉시 로컬 복귀. 커서가 도착한 자리에는 펄스가 표시됨.

> v0.15부터 앱이 **관리자 권한으로 실행**됩니다 (실행 시 UAC 승인 1회) — 관리자 권한 창 위에서도 원격 조작이 끊기지 않기 위해 필요. 방화벽 규칙은 자동 등록되고, 창을 닫아도 트레이에서 연결이 유지되며, 종료는 트레이 아이콘 우클릭 → 종료.

### 안드로이드 수신 기기

[릴리스](https://github.com/Q07K/Snapfield/releases)의 `Snapfield-*.apk`를 설치하면 폰/태블릿이 평면 위의 모니터가 된다 — PC 배치 탭에 스마트폰/태블릿 실루엣으로 나타나고, 커서를 밀어 넘기면 탭·드래그·스크롤이 동작한다. 앱의 **설정 체크리스트**가 필요한 권한(수신·접근성·키보드)을 순서대로 안내한다.

- **키보드**: PC 키보드로 입력 (한/영 키로 전환하는 **두벌식 한글 조합** 내장, Ctrl 단축키·방향키 지원)
- **클립보드**: 텍스트·이미지 양방향 — PC에서 복사해 폰에 붙여넣고, 폰에서 복사해 PC에 붙여넣기
  (폰→PC는 Snapfield 키보드 사용 중이거나 앱이 화면에 있을 때 읽힘 — Android 정책)
- **PrtSc**: PC에서 누르면 폰 화면이 캡처되어 **PC 클립보드로** 들어옴 (Windows PrtSc와 같은 의미)
- **안드로이드식 키매핑**: 마우스 **우클릭 = 뒤로** · 휠클릭 = 최근 앱 · `Esc` = 뒤로 · `Win` = 홈 · `Alt+Tab` = 최근 앱 · 볼륨/음소거/미디어 키 = 폰 볼륨·미디어
- **화면 유지**: PC 커서가 폰에 있는 동안 화면이 꺼지지 않음 (옵션)

첫 설치 시 관문 둘: ①Play 프로텍트가 사이드로드를 차단하면 Play 스토어 → 프로필 → Play 프로텍트 → 설정에서 검사를 잠시 끄고 설치 후 다시 켠다. ②접근성이 "제한된 설정"으로 막히면 설정 → 애플리케이션 → Snapfield → ⋮ → **제한된 설정 허용**. APK는 릴리스 키로 서명되어 이후 버전은 삭제 없이 덮어쓰기 설치된다.

### 리눅스 수신 기기 (우분투 데스크톱, Wayland/X11)

[릴리스](https://github.com/Q07K/Snapfield/releases)의 `Snapfield-Receiver-*-linux-x64`가 헤드리스 수신 데몬이다. 입력 주입이 커널 레벨(`/dev/uinput` 가상 마우스·키보드)이라 Wayland에서도 컴포지터와 무관하게 동작하고, 진짜 시스템 커서가 움직인다.

ARM64 기기(ASUS Ascent GX10, NVIDIA DGX Spark, 라즈베리파이 5 등)는 `Snapfield-Receiver-*-linux-arm64`를 받는다 — `uname -m`이 `aarch64`면 이쪽이다 (x64 바이너리는 `Exec format error`로 실행되지 않는다).

**실행 방법** — 파일 관리자에서 더블클릭하면 "다른 앱으로 열기" 창만 뜬다(GUI 앱이 아니라 콘솔 데몬이다). 데스크톱 세션 안에서 터미널을 열고:

```bash
cd ~/Downloads
chmod +x Snapfield-Receiver-*-linux-arm64   # 최초 1회 (x64 기기는 *-linux-x64)
./Snapfield-Receiver-*-linux-arm64
```

최초 실행 시 부족한 것들을 **자동 설정으로 제안**한다 — uinput 권한(udev `uaccess` 규칙이라 재로그인 없이 즉시 적용)과 클립보드 동기화용 패키지(v0.16.5+, 세션에 맞춰 Wayland는 wl-clipboard / X11은 xclip). 각각 Y 누르고 sudo 암호면 끝이고, 거절하면 수동 명령이 출력된다. (v0.16.3 이하는 자동 설정 스크립트에 버그가 있다 — v0.16.4 이상을 쓰거나 출력된 수동 명령을 사용.)

실행하면 연결 코드 6자리가 출력되고(`~/.config/snapfield/receiver.json`에 유지), 터미널 창은 닫지 말고 그대로 둔다 — 데몬이 그 안에서 돈다.

**PC에서 연결** — Windows Snapfield에서:

1. **기기 추가** → 목록에서 리눅스 기기 호스트명 선택 (안 보이면 IP 직접 입력 — 터미널 배너에 표시됨)
2. 터미널에 뜬 **연결 코드 6자리** 입력
3. 보정 화면에서 리눅스 모니터를 실제 책상 배치대로 드래그해서 놓고 **Save layout**

이후 커서를 그 방향 화면 끝으로 밀면 리눅스로 넘어간다. 키 입력은 물리 키 위치 그대로 전달되므로 **한/영 키**도 리눅스 쪽 ibus 한글 입력기로 그대로 토글된다. 화면 감지는 xrandr(XWayland) 기준이며 이상하면 `--size 2560x1440`으로 지정 (`--help` 참고). 클립보드는 텍스트·이미지 양방향.

## 릴리스 히스토리

버전별 변경 사항은 **[릴리스 히스토리](docs/releases.md)** 문서 참고 — v0.1(좌표계 모델)부터 v0.16(리눅스 수신 기기)까지.

> 남은 아이디어: 대용량 파일 청크 전송, 코드 서명(데스크톱 exe), v1.0 마무리

## 빌드 / 테스트 / 배포

```powershell
dotnet build Snapfield.sln
dotnet test Snapfield.sln
dotnet run --project src/Snapfield.App

# 포터블 단일 exe (테스트 PC에 .NET 불필요)
dotnet publish src/Snapfield.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# 산출물: src/Snapfield.App/bin/Release/net8.0-windows/win-x64/publish/Snapfield.App.exe
```

.NET 8 SDK 필요 (WPF는 `net8.0-windows` TFM).

**릴리스**: `v*` 태그를 푸시하면 GitHub Actions가 단일 exe를 빌드해 릴리스에 첨부한다
(`src/Snapfield.App/Snapfield.App.csproj`의 `<Version>`도 함께 올릴 것 — 창 제목에 표시됨).
