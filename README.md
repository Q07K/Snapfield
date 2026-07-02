# Snapfield

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
│  └─ Snapfield.App/         WPF 앱 (현재는 감지 모니터 진단 뷰)
└─ tests/
   └─ Snapfield.Core.Tests/  좌표 변환 단위 테스트 (5개, MWB 문제 재현·해결 증명)
```

## 좌표 변환 파이프라인 (CoordinateMapper)

1. **pixel → physical**: 어느 PC의 픽셀 커서를 전역 mm 평면으로. `physical = 물리오프셋 + (픽셀/해상도)·물리크기`
2. **hit-test**: 전역 평면에서 그 점을 품은 모니터 찾기 (없으면 최근접 모니터로 clamp — 커서 유실 방지)
3. **physical → pixel**: 목적지 모니터에서 역변환 → 그 PC가 이해하는 픽셀 좌표

DPI 스케일링(125%/150%)은 **프로세스를 Per-Monitor-V2 aware로 만들어** 중화한다.
그러면 Windows가 스케일된 픽셀이 아닌 **물리 픽셀**을 보고하므로, 변환은 순수 기하 계산이 된다.

## 현재 상태 (검증됨)

- [x] 솔루션 + 4 프로젝트 골격, 전체 빌드 통과
- [x] Core 좌표계 모델 + CoordinateMapper
- [x] 단위 테스트 9/9 통과 — 화면 간 물리 높이 유지 크로싱 + 레이아웃 저장/병합
- [x] MonitorEnumerator — 실기기에서 EDID 물리 크기 읽기 검증 (Win32 ↔ WMI를 PnP id로 매칭)
- [x] **보정 UI** (WPF): 모니터를 드래그로 물리 배치, 엣지 스냅, 자동정렬, JSON 저장/복원
- [x] 레이아웃 영속화 (`%APPDATA%\Snapfield\layout.json`) + 재감지 시 저장된 배치 병합
- [x] **입력 엔진**: `WH_MOUSE_LL` 후킹 + `SendInput` 주입 + 물리 좌표 라우터
  - 엣지 크로싱 감지 → 원격 모니터로 핸드오프 → 커서 캡처(로컬 중앙 파킹) → 델타 누적 → 복귀 시 워프
  - 팬텀 원격 화면으로 단일 PC 테스트 가능 (Input Engine 창)
  - 라우터 로직 단위 테스트 15/15, 후킹 라이프사이클 검증
- [x] **네트워크 릴레이** (v0.2.0, 마우스): 두 PC를 TCP로 연결
  - Hello로 모니터 레이아웃 교환 → 통합 물리 좌표계 구성
  - 커서가 상대 PC 화면으로 넘어가면 커서 이동·클릭·휠을 전송 → 상대가 `SendInput`으로 재현
  - 전송 계층 루프백 검증 (프레이밍·직렬화·양방향)

### 보정 UI 사용법
`dotnet run --project src/Snapfield.App` → 감지된 모니터가 물리 크기 비율대로 캔버스에 표시됨.
- **드래그**: 모니터를 실제 배치대로 이동. 이웃 엣지에 자동 스냅.
- **Auto-arrange**: 픽셀 순서대로 좌→우, 상단 정렬 재배치.
- **Save layout**: 현재 배치를 저장 (다음 실행 시 자동 복원).

## 입력 엔진 테스트 (단일 PC)
1. `dotnet run --project src/Snapfield.App` → 보정 창에서 모니터 배치 후 **Save layout**
2. **Input Engine…** 버튼 → 엔진 창
3. **Start engine** (팬텀 화면 체크된 상태) → 커서를 화면 **오른쪽 끝으로 밀면** 상태가
   초록(LOCAL) → 주황(REMOTE captured)으로 바뀌고 커서가 로컬 중앙에 파킹됨
4. 마우스를 **왼쪽으로 되돌리면** 자동으로 로컬 복귀 + 커서 워프 (물리 높이 유지)

## 두 PC 연결 테스트 (v0.2.0)
같은 네트워크(LAN)의 두 PC에서 각각 exe 실행 후 **Network…** 창:
1. **제어당할 PC**(부): **Listen (be receiver)** 클릭
2. **제어할 PC**(주): 상대 PC의 IP 입력 → **Connect (be controller)**
3. 연결되면 주 PC에서 커서를 **오른쪽 끝으로 밀어** 상대 PC 화면으로 넘김 → 상대 커서가 움직이고 클릭·휠도 전달됨
4. 왼쪽으로 되돌리면 주 PC로 복귀

> 방화벽에서 포트(기본 45654) 인바운드 허용 필요. 상대 IP는 `ipconfig`로 확인.

## 다음 단계

- [x] 키보드 포워딩 (`WH_KEYBOARD_LL`) — v0.4.0: 캡처 중 키 입력이 상대 PC로 전달, 연결 끊김 시 캡처 자동 해제
- [x] v0.5.0: 캡처 중 로컬 커서 숨김(시스템 커서 교체), **자동 재연결**(컨트롤러 3초 재시도·리시버 자동 재대기), 네트워크 감도 슬라이더, 연결 종료 시 프로세스 크래시 수정
- [x] **v0.6**: 보정 UI 기반 크로스머신 배치 ← 핵심 차별점 완성
  - 연결하면 상대 PC 모니터가 `layout.json`에 병합되고 보정 캔버스에 주황색으로 표시
  - 드래그로 실제 책상 배치(오른쪽/왼쪽/위/아래)대로 놓고 **Save layout** → 그 배치대로 커서가 넘어감
  - 저장 즉시 연결 중인 세션에 라이브 반영 (재연결 불필요)
  - 모니터 사이 틈은 30mm까지 자동 브리지 (엣지 프로브 2단)
- [x] **v0.7**: 트레이 상주 + 자동화
  - 창을 닫아도 백그라운드에서 연결 유지 (트레이 아이콘 우클릭 → 종료가 실제 종료)
  - 네트워크 세션이 앱 수준으로 승격 — 네트워크 창을 닫아도 세션 유지
  - 트레이 메뉴: 보정/네트워크 창 열기, **로그인 시 자동 실행**(레지스트리), **실행 시 마지막 연결 복원**
  - 마지막 역할(Listen/Connect) 자동 복원 + 자동 재연결 → 부팅하면 알아서 다시 붙음
  - Listen 시 **방화벽 규칙 자동 등록** (UAC 1회, MWB 방식)
- [x] **v0.8**: 클립보드 공유 + 페어링 보안 (이번 계획의 종착점 — v1.0은 보류)
  - **클립보드 공유(텍스트)**: 한쪽에서 복사하면 반대쪽에서 붙여넣기 가능 (양방향, 에코 루프 방지, 500KB 제한)
  - **연결 코드(PIN)**: 수신 PC가 6자리 코드를 IP 옆에 표시 → 제어 PC는 IP+코드를 입력해야 연결됨.
    코드 불일치 시 차단되고 재시도도 중단 (같은 LAN의 임의 접속 차단)

> 남은 아이디어(v0.8 이후): 전송 암호화, 이미지/파일 클립보드, mDNS 자동 발견, 3대 이상 동시 연결

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
