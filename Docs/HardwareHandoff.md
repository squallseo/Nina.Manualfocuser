# 실제 장비에서 이어서 작업하기

정리 날짜: 2026-10-05. 작업 브랜치: `spike_analysis`.
코드 기준 커밋: `c6aed27`; 이후 커밋은 이 인계 문서와 README 연결이다.
NINA 본체나 QHY 드라이버를 수정한 상태가 아니라, 플러그인 구현과 측정 기능까지 완료한 상태다.

## 완료한 기능과 현재 동작

| 항목 | 현재 구현 |
| --- | --- |
| 장비/촬영 안정화 | null 장비 정보, 취소, 진행 보고, 촬영 예약과 예외 처리 보완. 별 검출 결과를 변경하지 않음. |
| contributor 기능 병합 | LinearAF coarse/fine 스캔, 패스별 이차 곡선, 렌즈 설정 및 외부 RTG 연동. 기존 플러그인 식별자 유지. |
| 일반 초점 UI | Manual / Auto / Live focus. 기본 Manual. Focus와 Focus star를 넓이에 따라 배치하며 차트는 남는 높이를 사용. 호버는 NINA 테마 브러시 사용. |
| Focus star | 위치·현재 UTC·지평선을 반영한 밝은 별 목록을 처음 열 때 표시. Refresh/GOTO 제공. GOTO는 사용자가 실행해야 함. |
| Spike | 원형 별/도넛/불확실한 회절선은 HFR로 표시. 여러 폭 지표와 오프라인 평가 도구 제공. 일반 spider 회절선과 바흐티노프 마스크를 구분해서 검증해야 함. |
| Live focus | 1–5000ms 노출(카메라 지원 범위 내), 기본 250ms. 256px ROI, 별 확대 영상, 밝기 단면과 최근 120초 HFR 그래프(최대 600점). HFR은 NINA 전체 영상 다중 별 값이 아닌 로컬 단일 별 값. |
| ROI | Stop live → Select ROI → 전체 영상에서 별 클릭 → Start live. X/Y % 직접 입력도 가능. 50/50은 센서 중앙. 검출 실패는 NaN 대신 `no star detected`. |
| 바흐티노프 | 투영으로 세 선을 찾고 부호 있는 오차 계산. Live overlay는 선택 사항. Auto 모드에는 experimental scan 제공. |
| 성능 측정 | 촬영+다운로드, host download, 변환, 크롭, 분석, 미리보기/그래프 준비 시간을 표시. 첫 프레임과 20프레임마다 로그 기록. |

Live focus에는 `Streaming (experimental)` 선택이 추가됐다(2026-10-06).
초기 실험에서는 native QHY600M에도 공개 `ICameraMediator.LiveView`를 연결했지만, 같은 날 NINA 내부 프레임 수신에서
실제 AccessViolation 종료가 발생하여 일시 차단했다. 이후 사용자가 hardware Bin3 read mode 대신
2CMS-0로 바꾸어 촬영 지연이 해결됐다고 보고하고 스트리밍 재활성화를 요청했다.
현재는 사용자 요청에 따라 native QHY600M의 3x3 bin mode (Bin3*3Mode hardware)만 스트리밍을 차단하고 다른 read mode는 허용한다.
선택 기본값은 켜짐이며, 조건을 만족하지 않으면 `CaptureImage` 단일 촬영을 사용한다.
다른 카메라는 NINA LiveView capability가 true인 경우에만 허용한다. 재활성화가 AccessViolation 해결을 입증하지는 않는다.
SDK 실장비 시험은 아래에 기록했으며, 새 플러그인의 NINA 내부 스트리밍 화면은 아직 사용자 실장비 확인이 필요하다.
노출 ms와 실제 화면 갱신 ms는 다르다. 카메라가 ROI를 지원하지 않거나 요청을 무시하면 전체 영상을 다운로드한 뒤 크롭한다.
모드를 변경하면 Live 촬영을 취소한다. 스트리밍 중 In/Out은 스트림 종료 후 이동하고 새 스트림을 시작한다.
노출과 ROI, 스트리밍 선택은 Live를 정지한 뒤 변경한다.

2026-10-06 ROI 선택을 확장했다. 기본은 512×512이며 Live의 256/512/1024 버튼과 W/H 입력을 제공한다.
Select ROI로 전체 센서 축소 영상을 촬영한 뒤 별을 클릭하면 선택 크기의 사각형이 배치되고,
드래그하면 직사각형을 지정한다. 선택 중에는 그래프를 숨겨 전체 너비로 영상을 보여주고 사각형 및 확대 미리보기를 표시한다.
Use ROI로 확대 영역을 확인하거나 바로 Start live를 누른다. 확대 미리보기는 축소된 overview에서 가져오며,
실제 센서 해상도 영상은 Live 시작 후 받는다. 선택값은 센서 px 기준이고 각 변은 32–2048px, 4px 단위로 제한한다.
native QHY600M의 요청 좌표와 크기는 4px 정렬 및 센서 경계 보정을 거친다.
스트리밍/단일 촬영/바흐티노프 scan 모두 선택한 W/H를 사용한다. Live HFR와 별 밝기 단면은 중앙 최대 256×256에서
분석하며, 바흐티노프 overlay는 전체 ROI에서 분석한다. 대상 별을 사각형 중앙에 두어야 한다.
ROI 크기별 실장비 속도 및 새 선택 화면의 실제 NINA 조작은 아직 사용자 확인이 필요하다.

### 2026년 10월 6일 NINA 종료 대응과 Live UI 정리

사용자 재현 순서는 Imaging 기본 카메라 컨트롤러에서 수동 촬영, Select ROI, Start live다.
`20261006-002131-3.2.0.9001.8096-202610.log`에는 QHY 영상 모드 시작 직후 로그가 끝난다.
Windows Application Error와 .NET Runtime 기록에서 `0xc0000005`, `System.AccessViolationException`과
`QHYCCD.QhySdk.GetQHYCCDLiveFrame` → `QHYCamera.DownloadLiveView` 호출 스택을 확인했다.
SDK buffer 크기 또는 재연결/동시 접근 중 어떤 항목이 원인인지는 아직 확정하지 않았다.
이 native 오류를 managed try/catch로 복구할 수 없으므로 플러그인에서 비활성화된 QHY LiveView capability를 우회하지 않는다.
종료 대응 직후에는 QHY600M Live focus를 일반 ROI 단일 촬영으로 실행했다.
사용자 요청으로 이후 2CMS-0에서만 실험 경로를 다시 허용했으며, 종료 원인이 해결됐는지는 실장비 재검증이 필요하다.

같은 로그에서 플러그인 실행 전 기본 2초 촬영 중 camera poll에 62.21초가 걸린 기록이 있었다.
전체 프레임 SDK 지연 자체는 해결됐다고 볼 수 없다. Select ROI는 `ImagePrepared`로 받은 같은 카메라의 최신 전체 센서
영상이 있으면 읽기 전용으로 축소해 재사용한다. 추가 전체 촬영을 생략하며 기존 영상/별 검출 결과는 변경하지 않는다.
최신 영상이 없으면 새로 촬영한다. Details의 Refresh full image는 명시적으로 새 촬영을 요청한다.

Live 컨트롤은 모드 선택 바로 아래에서 노출 슬라이더와 수치 입력, 시작/정지 및 ROI 선택, ROI 크기,
포커서 이동 순서로 배치했다. 수동 좌표와 streaming 옵션, 시간 정보는 Details로 접는다.
360/520px 너비에서 실제 컨트롤 XAML을 독립 WPF 렌더로 확인했다. NINA 전체 화면/실장비 UI 조작을 대신하는 검증은 아니다.
FocusStreamChecks 32개와 FocusPreviewChecks 25개 통과: QHY의 위험 경로 호출 전 차단,
기존 단일 촬영, 재사용 이미지 축소 및 취소를 확인했다. native streaming이 고쳐졌다는 의미는 아니다.

## 다른 PC에서 빌드하고 적용하기

Windows, .NET 8 SDK와 NINA 3.2.0.9001 이상을 준비한다. 플러그인 패키지 참조는 3.2.0.9001이다.

```powershell
git fetch origin
git switch spike_analysis
git pull --ff-only origin spike_analysis
dotnet build ManualFocuser.csproj -c Release -p:DeployPlugin=false
```

`DeployPlugin=false` 없이 빌드하면 기존 PostBuild가 설치 위치로 복사한다. 장비 PC에서는 위 명령으로 빌드와 배포를 분리한다.
산출물은 `bin\Release\net8.0-windows\Cwseo.NINA.ManualFocuser.dll`이다.

NINA를 사용자가 종료한 뒤 다음처럼 DLL을 백업하고 적용한다. 진행 중인 촬영을 스크립트로 강제 종료하지 않는다.

```powershell
if (Get-Process NINA -ErrorAction SilentlyContinue) { throw '먼저 NINA를 종료하세요.' }
$dll = Join-Path (Get-Location) 'bin\Release\net8.0-windows\Cwseo.NINA.ManualFocuser.dll'
$plugin = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\Manual Focuser\Cwseo.NINA.ManualFocuser.dll'
$pending = Join-Path $env:LOCALAPPDATA 'NINA\PluginStaging\3.0.0\Manual Focuser\Cwseo.NINA.ManualFocuser.dll'
$backup = Join-Path (Get-Location) ('bin\deployment-backups\hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $plugin) -Force | Out-Null
if (Test-Path -LiteralPath $plugin) { Copy-Item -LiteralPath $plugin -Destination (Join-Path $backup 'installed.dll') }
Copy-Item -LiteralPath $dll -Destination $plugin
if (Test-Path -LiteralPath $pending) {
    Copy-Item -LiteralPath $pending -Destination (Join-Path $backup 'staged.dll')
    Copy-Item -LiteralPath $dll -Destination $pending
}
if ((Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath $plugin).Hash) { throw 'DLL 해시 불일치' }
```

NINA가 실행 중이면 설치 DLL을 덮어쓰지 않고 위 `PluginStaging` 위치에 새 DLL을 준비하여 다음 시작 때 적용한다.
이미 staging에 이전 DLL이 있다면 직접 설치 시에도 동기화해야 다음 시작 때 이전 파일로 되돌아가지 않는다.
프로필, NINA 로그, 장비 연결 설정과 로컬 이미지 데이터는 Git에 포함되어 있지 않다.

## 가장 먼저 할 일: Live focus 실제 병목 측정

1. 실제 QHY600M에서 밝고 고립된 별을 찾고, 마스크 없이 Live focus의 Select ROI로 별을 지정한다.
2. 100/250/500ms 노출을 각각 실행한다. 처음 한 프레임만 보지 말고 안정화 후 상태줄과 `[ManualFocuser/LiveTiming]` 로그를 비교한다.
3. 같은 대상에서 Bahtinov overlay를 켠 경우도 비교한다. 마스크 없는 상태의 overlay 결과를 초점 기준으로 사용하지 않는다.
4. NINA 버전, 카메라 연결 종류(native QHY/ASCOM), SDK/드라이버 버전, 필터, gain, USB 설정, 로그를 함께 보관한다.

로그 위치: `%LOCALAPPDATA%\NINA\Logs`. 로그에는 요청 노출, 실제 갱신 간격, 원본 크기, ROI 크기,
`hardwareRoi`, mask 사용 여부와 단계별 ms가 들어간다. 아래 표를 실제 측정으로 채우면 다음 개선의 근거가 된다.

| 노출 ms | 원본 크기 / hardwareRoi | 갱신 ms | 촬영+다운로드 ms | 분석 ms | 미리보기/그래프 ms |
| --- | --- | --- | --- | --- | --- |
| 100 | 미측정 | 미측정 | 미측정 | 미측정 | 미측정 |
| 250 | 미측정 | 미측정 | 미측정 | 미측정 | 미측정 |
| 500 | 미측정 | 미측정 | 미측정 | 미측정 | 미측정 |

`host download`는 NINA가 보고하는 시간이다. QHY 내부 작업이 겹칠 수 있으므로 촬영 시간에서 빼서
순수 노출 시간이나 USB 전송 시간이라고 단정하지 않는다. 미리보기/그래프 값은 CPU 준비 시간이며 물리 화면 표시 지연을 측정하지 않는다.
실제 장비의 속도 개선률은 아직 확인하지 않았다. 개발 PC의 바흐티노프 분석 약 95ms는 카메라 갱신 속도가 아니다.

## 다음 단계: QHY 연속 촬영

설치된 NINA 3.2.0.9001의 QHYCamera `CanShowLiveView`가 false인 것은 실제 패키지에서 확인했다.
검토한 upstream QHY 구현에는 영상 모드 관련 코드가 있지만, false를 true로 바꾸는 것만으로 지원이 검증되는 것은 아니다.

우선 NINA가 카메라를 계속 소유하는 공개 API로 프레임을 받을 수 있는지 설치 버전에서 조사한다.
2026-10-05 실장비 조사에서 설치된 `ICameraMediator.LiveView(CaptureSequence, CancellationToken)`가
`CameraVM.LiveView`를 거쳐 QHY 드라이버로 전달되며, 그 경로에서는 `CanShowLiveView`를 검사하지 않는 것을 확인했다.
공개 API를 통한 실험 가능성은 있으나, 아래 SDK 시험은 NINA 내부 연결 검증을 대신하지 않는다.
필요하면 NINA QHY 드라이버 테스트 빌드에서 시작/프레임 수신/취소/종료, ROI, 설정 복원과 일반 촬영 복귀를 검증한다.
같은 카메라를 플러그인이 별도의 QHY SDK 연결로 동시에 열지 않는다. 실행 중 카메라에 reflection으로 LiveView를 강제로 켜는 코드도 넣지 않았다.
프레임 수신은 별도 작업에서 수행하고 크기 1 큐에 최신 프레임만 유지한다. UI 분석이 느리면 오래된 프레임을 버린다.
NINA 공개 `LiveView` API 구현의 실장비 시험에서 아래 드라이버 문제를 재현하면 upstream 수정 여부를 결정한다.

### 2026년 10월 5일 QHY600M 실장비 스트리밍 검토

연결된 QHY600M에서 **256×256 ROI, 16bit 연속 프레임 수신과 단일 촬영 복귀를 확인했다**.
NINA와 SharpCap이 종료된 상태에서 NINA가 배포한 SDK를 별도 프로세스로 사용했다.
이 SDK 시험 당시 플러그인 Live focus는 기존 순차 촬영 방식이었다. 2026-10-06에 공개 API 연결을 추가했으며,
새 NINA 내부 스트리밍이나 화면 갱신 속도는 아직 검증하지 않았다.

| 환경 | 확인한 값 |
| --- | --- |
| NINA | 3.2.0.9001 |
| SDK | NINA의 `External/x64/QHYCCD/qhyccd.dll`, 25.4.23.15 |
| USB 장치 설명 / 드라이버 | QHY600U3G20-20221128 / 23.1.12.0 |
| 카메라 | SDK에서 QHY600M 1대 확인, singleFrame/liveVideo 지원 반환값 0 |
| Read mode | 0, PhotoGraphic DSO 16BIT |
| Gain / offset / USB traffic | 30 / 30 / 50 |
| 센서 / ROI | 9600×6422 / X=4672, Y=3080, 256×256, bin 1×1 |
| 냉각 | SDK가 보고한 PWM 0, 냉각 설정 명령 없음 |

각 노출에서 23프레임을 받고 첫 3프레임을 제외한 20개 수신 간격을 집계했다.
FPS는 SDK에서 프레임이 반환된 간격의 역수이며, 실제 화면 표시 속도나 센서 타임스탬프 기반 측정은 아니다.

| 요청 노출 ms | 평균 간격 ms | 중앙값 ms | 최소–최대 ms | 수신 FPS |
| --- | --- | --- | --- | --- |
| 100 | 99.76 | 99.88 | 89.69–109.40 | 10.02 |
| 250 | 250.58 | 251.36 | 236.72–265.55 | 3.99 |
| 500 | 498.61 | 497.76 | 474.66–510.55 | 2.01 |

세 스트림을 시작/종료하고 매번 single mode로 복귀했다. 마지막에는 100ms 단일 촬영으로
256×256, 16bit, 1채널 프레임을 받았다. 별도 취소 시험에서는 500ms 스트림 시작 후 1500ms에
취소를 요청했으며, 1프레임 수신 후 `StopQHYCCDLive`가 135.77ms에 반환됐다.
이 값은 Stop 호출 시간이며, 취소 요청부터 전체 복원까지 걸린 시간이 아니다. 취소 후 단일 촬영도 성공했다.
복원, 닫기, SDK 해제 명령의 반환값은 모두 0이었다.

사용한 [독립 진단 도구](../Tools/QhyStreamProbe/README.md)는 `Tools/QhyStreamProbe`에 있다.
원본 실행 로그와 설치 DLL 역컴파일 결과는 Git에서 제외되는 `bin/qhy-review/`에 보관했다.
진단 도구는 NINA 프로필을 읽거나 변경하지 않는다. 초기 single-mode SDK 초기화 후 얻은
gain/offset/exposure/transfer bits/USB traffic과 read mode를 복원하고 ROI를 전체 센서로 되돌린 뒤 핸들을 닫는다.
실행 전 다른 프로그램에서 사용하던 설정이나 ROI까지 보존한다는 의미는 아니다.

설치된 DLL의 메타데이터와 역컴파일을 조사한 결과:

- `QHYCamera.CanShowLiveView`는 IL `16 2A`, 즉 상수 false다.
- 공개 `ICameraMediator.LiveView(sequence, token)`는 NINA가 가진 카메라 연결을 사용한다.
  `CameraVM.LiveView`는 ROI와 binning을 적용하고 Start/Download를 실행하며 finally에서 Stop을 호출한다.
- QHY 드라이버는 스트림 전환마다 Disconnect/Connect를 실행한다. 이 복귀와 냉각 작업의 상호작용은 NINA 내부 실험이 필요하다.
- Start의 일부 실패 경로는 예외 대신 상태를 Error로 바꾸고 return한다. Download의 프레임 대기는 별도 시간 제한이 없으며,
  취소 후에도 이미지 생성 코드로 진행할 수 있다. 정상 SDK 시험만으로 이 오류 경로가 검증되지는 않았다.

현재 구현은 실험 선택으로 공개 mediator API를 사용하고 capture block을 유지한다.
프레임 읽기와 UI/분석을 분리해 최신 프레임 하나만 유지하며, 빈 프레임 거부, 프레임 대기 취소 제한,
취소/예외/중단 시 열거자 종료와 read mode/binning/ROI 복원 코드를 포함한다.
대기 제한은 최소 15초이며 SDK native 호출 자체가 멈춘 경우 강제 종료하지 않는다.
capture block은 수신 작업과 host StopLiveView가 끝난 뒤 해제한다. 수신 오류 발생 시 단일 촬영을 자동 재시도하지 않는다.
원래 ROI가 비활성이면 공개 API로 전체 센서 사각형을 복원한다. 내부 ROI enabled 플래그까지 끄는 공개 API는 없으며,
다음 일반 촬영의 CaptureSequence가 ROI 사용 여부를 다시 적용한다. 카메라 냉각 설정과 별 검출 결과는 변경하지 않는다.
일반 촬영 복귀는 새 플러그인의 실장비 시험에서 추가 확인한다.
NINA를 거친 100/250/500ms 측정과 기존 `CaptureImage` 측정을 같은 조건에서 비교한 뒤 기본 경로 전환을 판단한다.
이번에는 별 검출/HFR/마스크 정확도, 전체 센서 스트리밍, 장시간 안정성과 8bit 모드를 측정하지 않았다.

2026-10-06 구현 검증: Release 빌드 경고/오류 0, 기존 FocusPreviewChecks 20개와 신규 FocusStreamChecks 24개 통과.
신규 검사는 최신 프레임 선택, 종료 대기, 취소 시간 제한, native/ASCOM 구분, 설정 복원, ROI 무시 시 crop과 오류 종료를
가짜 mediator로 확인한다. 하드웨어나 NINA UI를 실행한 검사가 아니다.

```powershell
dotnet run --project Tools/FocusStreamChecks -c Release -p:DeployPlugin=false
```

최신 upstream 조사 기준은 [NINA 소스 24ca11f](https://github.com/isbeorn/nina/tree/24ca11facb685bde69107051423b6e7707f4fc5c)다.
[QHYCamera 소스](https://github.com/isbeorn/nina/blob/24ca11facb685bde69107051423b6e7707f4fc5c/NINA.Equipment/Equipment/MyCamera/QHYCamera.cs)에도
capability false와 영상 모드 코드가 있으며,
[CameraVM 소스](https://github.com/isbeorn/nina/blob/24ca11facb685bde69107051423b6e7707f4fc5c/NINA.WPF.Base/ViewModel/Equipment/Camera/CameraVM.cs)는
공개 LiveView 경로를 보여준다. 설치된 3.2.0.9001 DLL과 최신 소스는 별도로 조사했다.

참고: [SharpCap 촬영 문서](https://docs.sharpcap.co.uk/4.1/5_ControllingCameras.htm),
[QHY600PH 공식 사양](https://www.qhyccd.com/astronomical-camera-qhy600/),
[NINA QHY 소스](https://github.com/isbeorn/nina/blob/develop/NINA.Equipment/Equipment/MyCamera/QHYCamera.cs).
모델별 지원과 upstream 소스는 실제 장비 작업 시 다시 확인한다.

## 바흐티노프 검증과 재현 명령

알고리즘의 출처, 라이선스 검토와 프로젝트 자체 수치 선택은 [조사 기록](BahtinovResearch.md)에 있다.
물리 마스크를 장착하고 초점 양쪽에서 촬영한 실제 FITS는 아직 없다. 합성 모델은 이를 대체하는 실장비 정확도 근거가 아니다.
Auto scan은 첫 이미지 확인 후 이동하며, 각 위치에서 한 프레임을 버리고 세 프레임 중앙값을 사용한다.
오차 영점 구간 안에서만 보간하고 스캔과 같은 방향으로 접근한다. 최종 오차 0.5px 이내를 재측정한다.
검출/이동 실패, 불안정, 영점 부재 또는 취소 시 현재 위치에서 종료한다. micron/critical focus 정확도를 보장하지 않는다.
성공 후 마스크를 제거하고 일반 영상 HFR을 확인해야 한다.

```powershell
dotnet run --project Tools/BahtinovChecks/BahtinovChecks.csproj -c Release -- bin/bahtinov-samples
dotnet run --project Tools/FocusPreviewChecks/FocusPreviewChecks.csproj -c Release -p:DeployPlugin=false
dotnet run --project Tools/SpikeChecks/SpikeChecks.csproj -c Release
dotnet run --project Tools/FocusChecks/FocusChecks.csproj -c Release
```

2026-10-05에 재검증: Release 빌드 경고/오류 0, Bahtinov 57, FocusPreview 20, Spike 31, Focus 11,
총 **119개 검사 통과**. 장비 이동/촬영 명령 없이 실행했다. FocusChecks는 NINA 설치와 초기화된 로컬 별 목록 DB가 필요하다.
생성 FITS/PGM은 `bin/bahtinov-samples`에 있고, Git에는 생성기만 포함한다. 시뮬레이터의 고정 이미지 폴더는
포커서 위치와 광학 초점을 연동하지 않으므로 전체 AF 수렴 증거가 아니다.

기존 분석 데이터는 `C:\StellaC\QHY600M\Autofocus`의 2026-09-10/19 세션이다.
검토한 crop은 뚜렷한 바흐티노프 패턴이 아니었다. 2026-02-18 Snapshot 예시는 일반 spider 회절선이며 마스크 검증 데이터가 아니다.
시뮬레이터 연결 오류는 이전에 존재하지 않는 이미지 폴더 경로 때문에 발생했다. 새 PC에서도 폴더 경로를 다시 설정한다.

## 릴리스/manifest 및 로컬 파일

플러그인 ID/assembly 이름은 유지했으며 assembly 버전은 아직 1.1.0.0이다. 공식 배포 전에는 버전 증가,
패키지/체크섬/manifest 검증이 필요하다. Git push는 릴리스나 manifest 제출을 의미하지 않는다.
추적된 skill은 [.claude/skills/nina-plugin-manifest](../.claude/skills/nina-plugin-manifest/SKILL.md)에 있다.
개발 PC의 Codex 사용자 skill 설치는 다른 PC로 자동 이동하지 않는다.

`.vscode/`는 기존의 미추적 개인 IDE 설정으로 남겨두었다. 현재 설정에 NINA 강제 종료 디버그 작업도 있어
장비 PC에서 무심코 실행하지 않도록 공용 인계 커밋에는 넣지 않았다. `bin/`의 배포 백업, 미리보기 harness와 다운로드한
참고 소스도 로컬 전용이다. 재현 가능한 분석/검사 도구는 `Tools/`에 추적되어 있다.

## Live autofocus preview integration (2026-10-06)

Auto focus now includes Bahtinov and Spike analysis alongside the existing Linear scan.
Both new preview paths use the shared exposure/ROI, an immutable scan snapshot, the
public camera stream when available, and single frames otherwise. Each motor move
awaits stream shutdown before moving. Three measurements are combined after discarding
the first post-move frame. The full scan owns one camera capture block. Stop cancels
both capture and the bounded scan; final cleanup awaits the stream before releasing it.

Bahtinov retains the zero-error bracket and 0.5 px final verification. Spike uses
single-star diffraction-spike FWHM, a bounded coarse/fine minimum scan, and final
reproduction within 10%. It requires a valid central isolated star, clear bilateral
spikes, stable repeat measurements, and a 16-bit saturation check. Invalid measurements
abort further moves. Measurements stay local to this plugin and are never published to
NINA image-analysis history. After a verified scan, preview and metric monitoring
continue without further motor movement until Stop is pressed.

Validation: 18 SpikeFocusChecks, 57 BahtinovChecks, 41 FocusStreamChecks; Release build
clean. These use synthetic images/fake equipment. Neither new autofocus mode has been
validated on the connected telescope/focuser. Camera mode Bin3*3 remains stream-blocked.
## Offline verification with camera disconnected (2026-10-06)

All seven suites passed: SpikeChecks 31, SpikeFocusChecks 18, BahtinovChecks 57,
RoiInteractionChecks 20, FocusPreviewChecks 40, FocusStreamChecks 41, FocusChecks 11
(218 assertions total). FocusChecks now copies the installed NINA vcruntime140.dll
required by NOVAS; the catalogue test reads the existing database without equipment
commands. Installed CancelSVG/HourglassSVG resources and responsive card placement
at widths 950/480 also passed isolated WPF checks.

Autofocus checks the original camera identity and connection before each frame and
again after stream shutdown, before commanding the next focuser move. Exposure/ROI
changes clear the previous autofocus graph and metric. Native camera disconnection,
actual motor behavior, final optical focus and the complete UI inside NINA still
require real equipment/application checks; synthetic tests do not establish those.
The final Release DLL was backed up and deployed to the installed plugin directory;
SHA-256 matched the build output. NINA was launched without equipment connections:
PID 100 remained responsive, and startup log 20261006-025755-3.2.0.9001.100-202610.log
reported successfully loaded plugin Manual Focuser at 02:58:02 UTC. No plugin load
exception was logged. Startup did log a separate ToupTek toupcam.dll dependency error
during camera discovery; no QHY cameras were detected. Full in-host control rendering
and hardware operation were not exercised by this load check.
## Adaptive live autofocus (2026-10-06)

AF step is now the initial probe interval for Bahtinov and Spike. The original
origin +/- step*offsets range remains a hard limit; existing Linear AF is unchanged.
Bahtinov probes the local signed-error slope, predicts zero with secant travel capped
at twice the initial step, then refines inside measured opposite-sign brackets.
Near zero, it verifies after the same-direction final approach; tolerance is now
0.25 px. Already-focused measurements are confirmed without a motor move. Flat
slopes, exhausted bounds, invalid measurements and non-convergence abort.

Spike selects a downhill direction, grows its search distance to bracket a measured
minimum, and refines with bounded parabolic/golden-section steps. The required
bracket width is <= 2*max(1, initialStep/16), compared with the old step/4 fine grid.
Final same-direction remeasurement must reproduce the measured width within 10%.
Live frame handling still discards the first frame and takes the median of three;
streams close before movement and restart for measurements. No native SDK changes.

Representative synthetic comparisons (including preflight and final verification):
Bahtinov 6 -> 4 measurement batches; Spike 16 -> 10. These are capture-batch counts,
not measured camera elapsed-time improvements or proof of better telescope focus.
Noisy/nonlinear curves, reversed mask slopes, already-focused stars, scan limits,
cancellation, incorrect motor position and failed final verification are tested.
Validation: BahtinovChecks 76, SpikeFocusChecks 29, FocusPreviewChecks 40,
FocusStreamChecks 41 (186 assertions). Actual speed and optical accuracy versus NINA
Linear AF still need controlled same-star/exposure/focuser hardware comparisons.
## ToupTek streaming review (2026-10-06)

Read the installed NINA.Equipment.dll 3.2.0.9001 implementation of
NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera. CanShowLiveView returns false,
although StartLiveView sets OPTION_TRIGGER=0, DownloadLiveView receives video frames,
and ROI is supported. StopLiveView schedules trigger restoration via a continuation
on imageReadyTCS.Task and returns without awaiting completion. Therefore public
stream enumerator disposal alone does not establish completed native cleanup for
this driver. Do not bypass the capability flag or reflect into its SDK.

Current plugin behavior is single-frame preview for native ToupTek. ASCOM or other
connections are evaluated by their own advertised CanShowLiveView capability.
Three fake-mediator regressions verify native ToupTek capability rejection before
any streaming host call (FocusStreamChecks now 44). No ToupTek camera was captured
or physically validated. Existing startup logs also report toupcam.dll dependency
load failure in the installed NINA camera-discovery path; that environment problem
is separate from the disabled streaming capability.
## Native ToupTek streaming enabled (2026-10-07)

At user request, the native ToupTek_ device-ID prefix now enables public mediator
LiveView despite NINA's disabled UI capability. The live/auto workflows use the
same shared ROI/exposure and capture reservation as QHY. ASCOM and OEM categories
are not included in this override. GetDevice() must return the same ICamera ID.

After the host enumerator closes, cleanup waits on that original camera's public
Connected/LiveViewEnabled properties until video mode restoration is complete.
This avoids relying on stale CameraInfo polling. It ignores capture cancellation
during cleanup, exits on disconnect, and times out after max(15s, exposure*3+5s).
Timeout aborts before setting read mode/bin/ROI or allowing subsequent autofocus
movement; reconnect is required if video mode remains active. There is no SDK
handle, reflection access, mode forcing, or automatic retry.

Fake-device checks cover delayed cleanup, stale CameraInfo, restart, timeout,
disconnect, native-device mismatch, ASCOM exclusion and the existing QHY gates.
ToupTek has not been physically connected/captured for this change. The previously
observed installed toupcam.dll dependency error may still require an SDK/runtime
installation repair before NINA can discover a camera.
## Field diagnostics and automatic mask ROI (2026-10-08)

Bahtinov AF first captures a 1024-square scout around the user's approximate chosen
star (clamped to the sensor). It finds a supported compact core near the selection,
rejecting isolated hot pixels, recenters candidate squares of 128/192/256/384/512,
and selects the valid unsaturated three-line ROI with highest measured contrast.
The stream closes before applying the chosen hardware ROI. The ROI stays fixed for
the whole autofocus run; missing patterns fail before any motor movement. This is
validated on synthetic shifted stars, not proof that all physical masks will fit.
The initial three-frame instability limit remains. The near-focus follow-up below
adds bounded stationary confirmation when that initial batch is inconsistent.

Private archives are under %LOCALAPPDATA%\NINA\ManualFocuser\FocusDiagnostics.
Each run gets a timestamped directory. AF saves every frame consumed by the plugin,
including scout, discarded settling frames, valid samples and failed-measurement
frames, as unsigned 16-bit FITS before display stretching. Matching JSON stores
camera/mode/exposure/gain/offset, position, source ROI and phase. measurements.jsonl
stores analysis, batch spread/limit, automatic ROI choice and failure details.
Live focus and post-AF monitoring save one frame per five seconds. Frames dropped by
the latest-frame stream are not archived. No extra camera exposure is issued to save.
Disk writes are awaited on background work; write failure stops rather than claiming
an archive exists. Archives remain local and can be copied for offline review.

BahtinovChecks: 93 assertions including automatic center, preserved error geometry,
blank/cancellation rejection and exact FITS unsigned ADU roundtrip. Clean Release
build. Physical-mask ROI sizing and the reported field instability still need review
of recorded field images; the saturation/auto-stretch changes are included too.

## Near-focus field excursion (2026-10-08)

The archived Bahtinov run at position 97438 contained errors +0.260, -0.815,
and +0.208 px. All three frames had valid high-contrast mask geometry. The old
whole-range gate stopped on their 1.075 px spread. Replaying all 15 measured raw
FITS frames reproduces the recorded errors without changing the analyzer.

Stable batches still need only three frames. An inconsistent Bahtinov batch now
collects four additional frames at the same position. At least five of seven must
agree within +/-0.5 px around the signed median. Otherwise the run still stops.
Final focus tolerance remains +/-0.25 px with independent confirmation; invalid
geometry, changed orientation, saturation, and cancellation retain their guards.
Archives record every extra consumed frame, the entire range, agreeing-frame
count/range, and acceptance. This keeps noisy measurements visible for review.

Controlled follow-up tests cover the recorded triplet, opposite polarity, one/two
outliers, persistent noise, failed geometry, cancellation, and independent runner
confirmation without movement. The four additional confirming frames in that test
are synthetic measurements; the observed field archive ended after its third frame.
BahtinovChecks passes 106 normal checks, or 123 with both field replay options.
FocusPreviewChecks passes 47, SpikeFocusChecks 29. Release build has no warnings.
The new seven-frame workflow still needs a physical field retest after restart.

The separate Spike failure archive contains a real connected clipped plateau in
the central measurement area at 250 ms, gain 26 (peak 65534). Replay confirms the
saturation guard is appropriate for that frame. Reduce exposure for that star;
do not interpret a clipped core as a valid stellar-width minimum. No camera or
focuser was operated during these offline checks.

## Spike automatic exposure recovery (2026-10-08)

Spike AF now handles connected central clipping by reducing exposure to one quarter
and rechecking while stationary. It waits for the existing stream cleanup before
changing its capture settings. Exposure cannot go below max(1 ms, camera minimum),
with at most eight reductions. The shared exposure control shows the actual value;
gain and read mode are not changed. Still-clipped frames at that limit require a
lower gain or dimmer star rather than claiming a valid width measurement.

Clipping during a scan invalidates all previous widths and tracking state. At the
new exposure the current position must first yield valid spike geometry before
returning to the original origin and rebuilding the scan inside its original motor
bounds. This prevents comparison of widths from different exposures. The same
direction of final approach and independent final width verification are retained.
Ordinary camera failures, invalid geometry, and Stop do not trigger exposure retries.
Monitoring never reports widths from a clipped frame or issues additional motor moves.

All consumed frames and adjustments remain in the diagnostics archive, including
exposure, position and scan-reset events. Offline tests cover startup/mid-scan
clipping, exposure-dependent widths, minimum/retry limits, invalid spikes, failed
cleanup, delayed cleanup, cancellation, and an unreached restart target. These are
fake-device/metric checks, not a physical auto-exposure field result.
SpikeFocusChecks: 51 passed, including 22 automatic exposure checks.
FocusStreamChecks: 55 passed. Release build: zero warnings/errors.

## Visible parallel spikes rejected by AF (2026-10-08)

The next field run successfully changed 250 ms to 62.5 ms, then stopped on
"Clear diffraction spikes were not detected". The saved 000004 raw FITS contains
visible parallel diffraction lines. Its original central-ray estimate was 159.70
degrees, dominated by the bright stellar core rather than the outer pattern.
Display auto stretch was already active; applying that nonlinear stretch to width
measurements would change the metric and does not recover saturated information.

Single-star Spike AF now retains an internal window of at least 128 pixels where
the capture permits, excludes the estimated core footprint from angle estimation,
and searches parallel offsets up to 16 pixels. The directional score requires
signal on both sides. Its detection gate tests a consistent offset through three
radial bands on each side, with farther background strips to avoid sampling the
other split line. Band significance uses an engineering estimate of median
uncertainty, counting unique pixels; the 2x-background and five-sigma checks remain.
The multi-star/offline metric defaults retain the original path.

The actual archived raw frame now detects an 86.18-degree axis and valid 13.51 px
width. This proves recovery of detection for that frame, not autofocus convergence
on the telescope. Tests cover rotated narrow/split patterns, raw pixel preservation,
circular/elliptical cores, one-sided artifacts and noise. SpikeFocusChecks: 84 with
the optional field replay; SpikeChecks: 31. Clean Release build. Future archives
also include angle strength and per-star profile geometry, including failed frames.

The autofocus dropdown now lists Bahtinov, Spike, Linear AF (HFR), in that order.

## Automatic Spike acquisition ROI (2026-10-08)

Spike AF now uses the same 1024-square scout/restart flow as Bahtinov AF. Shared
supported-core detection locates the star near the user's approximate selection,
ignoring isolated hot pixels. It uses the actual selected sensor position even
when a sensor-edge scout is clamped. No valid core means no focuser movement.

Supported outer light out to 192 pixels determines a 256/384/512-square window,
with 24 pixels of margin. At image edges a smaller centered window may be used;
the crop is never shifted off the star just to fit. If no centered window fits,
selection fails. Outer extent is a sizing estimate, not a claim of valid spikes:
fresh final-ROI frames must still pass the existing geometry/saturation preflight.

The scout stream closes and native cleanup completes before the hardware ROI is
applied. Camera identity and cancellation are rechecked. The shared UI receives
the final aligned sensor rectangle. The ROI stays fixed through the scan and any
automatic exposure recovery. Scouts, center/background/noise, supported radius,
preferred size, selected crop and final aligned sensor ROI are saved locally.

SpikeFocusChecks: 100 with the existing raw field replay, including actual-star
centering and retained usable geometry after the automatic crop. Synthetic checks
cover adaptive sizes, hot pixels, clipped scout, blank/nonfinite input, Stop, and
sensor edges. BahtinovChecks: 106 after sharing the core locator. No camera or
focuser was operated in these offline checks; hardware ROI transitions need a
field retest after restart.

## Plate-solve focus-star centering (2026-10-08)

The focus-star GOTO button now slews, captures full sensor images using the active
NINA Plate Solving configuration, and calls NINA's public CenteringSolver to
correct/re-solve the selected coordinates. The configured tolerance is in
arcminutes; final status/log records the independent image residual in arcseconds.
The host correction loop is bounded to ten iterations. NoSync, blind fallback,
binning, gain, solve exposure and retries follow the profile; filter/offset are
kept. Focusing exposure/ROI are independent. Dome services use the same exported
dependencies as NINA's Center sequence item, including initial dome sync.

Camera reservation spans slew, solving and corrections. The guarded capture
solver checks cancellation and device state before/after each host capture-solve
call, preventing a newly canceled/disconnected result from causing sync/reslew.
Old field images/measurements are invalidated before movement. Live capture,
focus movement and ROI changes are locked until cleanup. All exits release an
acquired reservation; failure to acquire one never releases another owner's.
Successful final image verification resets the shared focus ROI to 50%/50%.

FocusCenteringChecks: 75 passing offline checks, including the actual NINA
CenteringSolver with deterministic pointing offset and the plugin VM lifecycle.
Release build has zero warnings/errors. No mount/camera was operated; physical
centering needs a field test after restart. Remove the Bahtinov mask for solving.
The latest saved QHY600 profile has ASTAP installed, a 2 s solve exposure and
1 arcminute tolerance. Its solve retry delay is 2 minutes, so failed solves can
wait noticeably; Stop GOTO is available throughout. Profile values were read,
not changed.

## Near-focus Spike measurement instability (2026-10-08)

Actual run `20261008-032235...5984` failed at 03:25 during refinement at position
99,348, before final verification: widths 7.55, 8.20, 10.25 px had a 2.70 px
spread versus the 1.64 px limit. The raw archive `20261007-182323-468Z-SpikeAF-89e75e07`
reproduces these values. Strongest-axis estimates switched between approximately
86 and 176 degrees throughout the scan, so different physical widths were mixed.

Spike AF now acquires a valid axis once and retains it through movements,
exposure/tracking restarts and monitoring. The multi-star/manual analyzer is
unchanged. The same diffraction geometry and SNR gates run on the locked axis.
All 18 archived unsaturated measurement frames retain valid geometry with this
change; however the failing position's fixed-axis widths still vary (8.22, 10.17,
11.04 px). Axis locking alone is not evidence of convergence.

An unstable three-frame width batch now collects four further stationary frames,
without stream restart or motor movement. Five of seven must agree around the
median within the original max(0.25 px, 20%-median) width tolerance. All raw
values, overall/inlier spread, count and the locked angle are saved. Invalid
geometry, persistent spread, cancellation and the existing independent final
minimum check still stop the run; no false success/focuser return is introduced.

SpikeFocusChecks: 142 with this raw-session replay, including exact failure
reproduction, fixed axis/lost-line checks, stationary outlier handling, persistent
instability, cancellation and adaptive convergence with an outlier at every
position. Real additional frames/convergence cannot be tested from an archive
that stopped after three frames. No connected equipment was operated for checks.

## Autofocus graph position axis (2026-10-08)

Bahtinov/Spike AF preview plots now place each frame at its measured focuser
position, with integer X-axis labels and metric-versus-position scatter points.
Repeated frames at one position remain visible as vertical variation. Points
are not connected in acquisition order, which would zigzag during a reversing
search. Ordinary Live focus retains its elapsed-seconds line graph; Linear AF
already uses the position-based main chart. The graph clear button is labeled
Clear graph for both modes. Release build verifies compiled XAML; visual layout
in the running NINA session needs a restart to load the updated DLL.

## Wide defocused Spike acquisition (2026-10-08)

Actual run `20261008-034156...10920` failed before any focuser movement, at
position 114,348. After automatic 250→62.5 ms recovery, the saved frame
`20261007-184239-732Z-SpikeAF-e800a252/000008.fits` had a valid raw width but failed
diffraction geometry. HFR seeded a 60 px stellar footprint at its bright rim;
the fixed 17 px tracking window stayed near (259.26,236.08), away from the full
stellar centroid. The prior acquired axis also remained locked after discarding
the high-exposure curve.

AF now opts into a seed-footprint centroid window (at least 2*BaseSize+1, up to
121 px with existing seed limits). After refinement, the internal crop is
re-extracted around that centroid, with fractional offsets preserved. If the
recentered crop cannot fit, that frame fails rather than using a clipped band.
Other analyzer callers retain the existing defaults. No geometry, SNR or
saturation thresholds were relaxed. The same raw failed frame now yields center
(266.38,245.58), axis 86.60 degrees and a valid 25.84 px width. Pure ring/noise
frames still fail the diffraction gates.

Exposure recovery explicitly clears axis acquisition only while discarding all
old curve samples. Tracking-only resets and ordinary focuser movement retain the
current scan's axis. This supersedes the earlier rule retaining an axis across a
whole-curve exposure restart. The current filter/ROI and motor bounds are unchanged.

SpikeFocusChecks: 139 with the defocused raw replay, 156 with the earlier near-focus
session replay. Checks cover rotated defocused rings with/without real bilateral
lines, rim-seeded centroid/crop recovery, per-scan axis acquisition, cancellation,
noise and adaptive focus. SpikeChecks: 31. Release build: zero warnings/errors.
No physical equipment was operated; raw-image classification recovery is verified,
while through-focus convergence from the new starting position needs a field test.

## Split-spike background reference and initial mode (2026-10-08)

The next two field runs at 114,348 still stopped at diffraction detection after
250 to 62.5 ms exposure recovery. The loaded DLL matched the centroid fix, so
this was a second detection issue rather than an outdated installation. Archived
`20261007-185711-246Z-SpikeAF-f46435cd/000006.fits` and
`20261007-185420-168Z-SpikeAF-f2cc45dd/000006.fits` show split diffraction lines
around 12 to 13 px from the axis. Fixed background strips 24 px from a candidate
line sampled the opposite line and its wings. The outer radial bands then failed
the existing five-sigma requirement.

Parallel-spike detection now spaces background strips at
`max(24, 2*abs(lineOffset)+16)` px, beyond the opposing line and its wings. The
same six bilateral radial bands, factor-of-two contrast and five-sigma thresholds
remain required. Both failed images now acquire a valid axis and raw FWHM
(26.30 and 25.63 px with a fresh seed). Existing narrow-line checks are retained;
rotated wide split patterns, pure rings, circular/elliptical stars, one-sided
artifacts and noise are checked offline. Whole-run motor convergence still needs
a new field run; the archive contains no successful through-focus sequence.

The panel now opens in Auto focus with Spike selected. Selection alone never
starts an operation or moves hardware.

Release build: zero warnings/errors. SpikeFocusChecks: 158 with the two recent
failure frames, 174 with the earlier near-focus session, 157 with the older
defocused-frame replay. SpikeChecks: 31. Installed and staging DLLs were copied
with NINA closed and verified against the Release SHA-256. No hardware was
operated during verification.

## Shrinking footprint during Spike AF (2026-10-08)

The next run, `20261007-190623-291Z-SpikeAF-cb443e94`, moved toward focus:
width decreased from about 29 to 16 to 11.48 px. Detection then failed at
position 101,848 before a minimum was bracketed. This was not a final-confirmation
failure. The acquisition footprint stayed at 60 px, so the evidence gate kept
inspecting radial bands from 60 to 103 px after the current HFR footprint had
shrunk to 25 px. Real nearer diffraction light was excluded.

AF now derives the evidence footprint from the current local HFR, capped at the
acquisition footprint. Its radial evidence range follows that footprint, using
the same minimum 128 px evidence aperture and existing six-band contrast/SNR
requirements. Only diffraction validation changes: the acquisition crop,
centroid window, width-profile aperture, motor bounds and per-scan axis remain
fixed. Current/acquisition footprints are written into frame diagnostics.

Offline sequential replay of all 23 analyzed raw frames reproduces the old
failure and validates all frames with the new evidence aperture. Every width
and used angle matches the old computation exactly, including 11.477310714 px
at the failed position. The clipped 24th measurement frame is excluded using
the production saturation check, as in the original run. Negative tests cover
small circular and elliptical stars with a retained large acquisition crop.
SpikeFocusChecks: 228 with this run, 180 with the earlier near-focus run.
SpikeChecks: 31. Release build: zero warnings/errors. The archive ends at the
first failed measurement; a successful bounded minimum and final motor-position
verification still require a new field run. Hardware was not operated by tests.

## Noise-aware initial travel for both AF methods (2026-10-08)

Live focus's manual In/Out step (e.g. 600) is separate from NINA's profile AF step
size (2500 in the field run). Neither value is overwritten. Both AF methods now
receive stationary-batch uncertainty from the application. Robust MAD of accepted
frames estimates uncertainty of the median, with a 0.05 px floor. Two measurements
need a difference exceeding twice their combined uncertainty to establish a
usable slope or coarse minimum bracket.

Spike probes both directions and doubles travel when the initial difference is
too small. Minimum bracketing skips indistinguishable neighboring samples until
both sides show a supported increase, then uses the existing safeguarded fine
search. Bahtinov uses the widest pair with a supported error change for secant
prediction; without one it makes larger bounded calibration probes. Prediction
travel scales with the calibrated span rather than always capping at twice the
initial step. All movement stays within the original AF step * offset bounds;
noise-only data cannot create a valid slope or coarse width bracket. Existing
mask-zero and final Spike-width verification requirements remain enforced.

Diagnostics include batch uncertainty and actual requested moves; the global
status displays the current step and target position. The latest Bahtinov run
`20261007-191546-770Z-BahtinovAF-80726aa0` stopped after a 2500-step move because
only 4/7 frames agreed within 1 px. Step calibration does not convert this
unstable batch into a valid measurement, and that run cannot prove convergence.

Offline checks cover 600/2500 initial steps, weak changes, a first probe with the
wrong apparent direction, bounded noise-only failure, invalid uncertainty,
cancellation and independent final verification. No connected hardware was
operated. Field effectiveness of the calibrated steps still needs a new run.

Release build: zero warnings/errors. SpikeFocusChecks: 239 with archived
convergence replay (169 algorithm/geometry checks plus the 70 field checks).
BahtinovChecks: 112. Updates are staged when NINA is running and require a
restart to load; the staged DLL hash is checked against the Release artifact.

## Continuous Live focus during manual movement (2026-10-08)

Live focus's In/Out previously disposed the stream, awaited motor movement and
opened another stream. QHY mode transitions added several seconds to each side
of every move. These manual moves now retain the existing camera reader and
run the independent focuser mediator task while previews continue to render.
Exposure, ROI, gain and readout settings remain fixed for the stream; no second
camera reader or SDK call path is introduced. The status and archived images
mark movement frames. In/Out remains locked until the active move completes.
Single-exposure fallback still waits for movement before the next capture.

Stop or a preview error cancels the shared token, awaits native stream cleanup
and the motor task, then releases camera reservation/UI ownership. Movement
faults are observed and end preview with cleanup. This supersedes the previous
blanket close-before-move policy for Live focus's manual In/Out; autofocus still
uses its own stationary measurement workflow.

The actual dockable VM is tested with strict fake camera/focuser mediators:
multiple displayed frames during a held motor move, one stream start with no
intermediate stop, controls restored on the same stream, cancellation, delayed
camera/motor cleanup retaining ownership, and movement failure. Run:
`dotnet run --project Tools/FocusCenteringChecks -c Release -- --live-move`.
No hardware was operated. Native QHY/ToupTek timing must be checked in the field.

Live VM integration: 19 checks. Stream lifecycle: 55 checks. Release build:
zero warnings/errors. DLL deployment is verified by SHA-256 against the build;
if NINA is running, the updated DLL is staged for the next startup.

## Live default and explicit shared Auto ROI (2026-10-08)

The panel now opens in Live focus; the separate AF method defaults to Spike.
Auto ROI is a common button beside Select ROI. It captures one 1024 px scout
around the shared selected center while holding camera reservation, finds a
supported star and applies its centered crop to the shared sensor coordinates.
Bahtinov AF/Live Bahtinov overlay uses the existing mask-line crop selection;
other modes use the supported star footprint. The crop preview is shown, and
the existing full-frame mouse editor remains available for correction. Detection,
cancellation, connection change or reservation rejection leaves the prior ROI
intact. No motor motion, stream start or autofocus is performed by this button.

Bahtinov/Spike AF startup no longer captures a 1024 px scout or silently applies
a newly found ROI. Both snapshot the selected width, height and center before
preflight. Live, manual and Linear AF already use this shared selection. Mode
switches keep it. Exposure recovery and stationary safety checks remain active.
This supersedes earlier descriptions of automatic ROI selection at AF startup.

Actual VM integration covers initial mode/method, one-shot button selection,
sensor-offset mapping, preview and ownership cleanup, mode persistence, AF
startup ROI without scouts, Live startup, invalid stars/masks, Stop and capture
reservation rejection. Run
`dotnet run --project Tools/FocusCenteringChecks -c Release -- --auto-roi`.
Hardware is not operated by the tests; a field run is still needed for optics.

Explicit ROI VM checks: 49; continuous Live movement checks: 19; focus-star
centering checks: 75. Release build verifies compiled XAML, with zero warnings
or errors. Default/mode/ROI behavior is exercised through the actual dockable VM
with strict mediator fakes; in-host visual layout needs a restart/field check.

## Disabled focus-star GOTO (2026-10-08)

The previous enable condition rejected any connected guider, including idle
PHD2. GOTO now allows that connection and uses the public guider mediator to
stop guiding before mount movement, inside the camera reservation/cancel scope.
PHD2 returns false when already Stopped, Looping or Selected; those confirmed
idle states are accepted through the public IGuider.State property. Other false
results abort before slew/capture. Guiding stays stopped for focusing. A new
NINA guiding operation cancels GOTO; PHD2 resuming during capture is detected
before the capture result can cause a correction or verified-success report.

Disabled GOTO has a dynamic tooltip, also visible while disabled, explaining
missing target/camera/mount, Park, slew, focus/live activity or another capture
owner. Capture ownership changes refresh availability even without connection
flag changes. Repeated unchanged device polls do not invalidate host commands.

Actual VM/host centering checks: 162; Live movement regression: 19; explicit ROI
regression: 49. Release/XAML build: zero warnings/errors. No hardware commands
were sent by this verification; physical GOTO/centering remains a field check.

## NINA-style curve autofocus with streaming (2026-10-08)

All panel AF commands now route through CurveAutofocus. The previous incremental
Bahtinov/Spike runners and coarse/fine Linear stopping logic are no longer used
by the panel. The three method choices and default Live mode/Spike method remain.
The initial scan spans ±NINA initial-offset count at its fixed AF step interval.
Insufficient side coverage adds positions at that same interval; it never doubles
the step. Maximum points: min(60, offsets × 10). Motor MaxStep/zero, camera/focuser
identity and NINA AF timeout guard the run. No automatic ROI selection is added.

Frames/position uses NINA's frame-count setting, clamped 5–50 for statistical
measurement (the UI replaces Single pass). Valid/inlier coverage must be at
least max(3, ceil(0.6 × frames)). Frame medians are MAD filtered, with a noise
floor; fitting uncertainty combines robust frame scatter and detector star
scatter/count, divided by inlier count. Box plots retain valid-frame quartiles,
Tukey whiskers/outliers and a median. Fitting uses the robust median/uncertainty.

Linear calls the selected IStarDetection behavior once per frame. It pins that
behavior and image-analysis settings for the run. Private BaseImageData is rendered
and auto-stretched using NINA APIs; source bit depth/Bayer state are retained.
HFR, HFRStdDev and star count come from the behavior, so a selected Hocus Focus
detector supplies those measurements. Metadata includes current filter, pixel
size/focal length, bin 1, gain/offset, acquisition position and exposure. This is
needed for Hocus Focus per-filter settings. Results and subtype star lists are
never mutated; UpdateAnalysis/SetImage are never called for derived AF values.

HFR/Spike fitting uses NINA QuadraticFitting, HyperbolicFitting, TrendlineFitting
or combined methods selected in the profile. Position coordinates are centered
before calling those host classes. NINA's R² threshold, bracketing, positive
curvature/noise-supported wings and independent final measurement gate success.
Bahtinov fits signed error linearly and finds its zero crossing; verification
requires abs(median error) + 2 × uncertainty ≤0.5 px. HFR/Spike verification must
be no worse than the sampled best plus max(15%, combined 3-sigma uncertainty).

One camera stream remains open across moves. Producer-side monotonic request/
availability timestamps gate the first accepted post-settle frame: its request
must start after settle and its arrival must be at least exposure + one measured
frame interval later. In-flight/buffered frames are discarded, not analyzed.
One queued raw frame bounds capture/analysis backpressure. Detector/tracking
analysis is serial, but overlaps camera acquisition and next-point movement.
ScanAsync returns only after outstanding CPU/motor work finishes, including
Stop or fault. The camera reservation is released after stream disposal. Without
stream support, the same statistics/scan use individual settled exposures.

Saturation reduces exposure by quarters (up to eight reductions / camera min),
drains work, stops the stream and restarts the entire curve; mixed-exposure fit
points are not retained. Raw FITS/JSON frames still go to FocusDiagnostics.
After verification the panel keeps preview running until Stop, without altering
the curve statistics. The AF timeout is disabled for that verified preview.
The sequencer's Linear request returns after verification/cleanup instead of
waiting for an interactive preview Stop.

The AF plot now has frame-distribution boxes, a yellow fitting curve and a
separate yellow final-verification box. Padding prevents boundary boxes from
being clipped. A synthetic chart export is available at bin/curve-af-chart.png;
it was visually checked, and is not a hardware field result.

Offline curve checks: 543 (host fitting/statistics, bounded pipeline, analysis
overlap, real dockable VM through a fake pluggable detector, continuous stream
across moves, moving-frame exclusion, failure/Stop cleanup, independent bad
verification rejection and single-frame fallback). Stream regression: 55.
Device operations in these tests are strict fakes; physical speed/accuracy and
the complete NINA panel layout still need a field run after restart.
### Graph focus positioning

Manual and AF focuser-position charts accept a click or horizontal drag on the
X-axis, issuing one absolute move on release. Cyan is current position; yellow
is the target preview. Escape/capture loss/view changes cancel before movement.
Plot-body interactions retain box trackers. Screen-to-position mapping follows
the rendered axis, including zoom and resize; pointer travel is clamped to the
visible plot width and public focuser travel limit.

Graph movement is blocked during scanning/verification, GOTO, existing motor
travel, disconnected devices and another camera operation. AF monitoring allows
manual graph moves with the original stream open. The old verification becomes
historical; no new moving frames are mixed into the curve. Stop cancels the motor
and waits for native cleanup before releasing the camera reservation.

Offline coverage: `FocusCenteringChecks --graph` checks coordinate transforms,
limits, busy/device gates, absolute movement, second-command rejection and Halt
cleanup. `--curve` exercises completed/canceled graph movement during the actual
VM's AF monitoring loop with fake camera/focuser mediators, including continuing
preview and single-stream ownership. `bin/graph-focus-control.png` is a synthetic
chart preview, not a running NINA screenshot. Physical focuser interaction and
host mouse gestures remain field-test items; no NINA or device was started.

### Native ZWO ASI streaming

Installed NINA 3.2.0.9001 ASICamera has CanShowLiveView=false but implements
StartLiveView / DownloadLiveView / StopLiveView using RAW16 video capture.
Native IDs use the ZWOptical_ driver category, including model and camera alias.
FocusCameraSupport now centralizes stream capability and ROI alignment policy:
ASI and ToupTek driver families are enabled across their models; normal drivers
follow CanShowLiveView, including future QHY drivers that explicitly advertise it.
The reviewed QHY600M override and 3x3-bin exclusion remain intact. Unsupported
ASCOM paths are not enabled from camera display names.

ASI stream start validates the current public ICamera identity/connection before
calling the mediator. Streaming, single capture and mouse selection share width
alignment of 8 pixels and height alignment of 2 pixels. Raw values, bit depth,
Bayer metadata and current gain/offset pass through the existing common pipeline.
Camera ownership, focuser-motion overlap and stream cleanup remain common to all
three AF methods and Live focus. No private SDK access or extra camera handle.

The installed ASI DownloadLiveView invokes GetVideoData with wait=-1 and does not
observe CancellationToken inside the native read. Stop therefore waits for the
in-flight read to return before host StopLiveView/settings restoration. A stalled
SDK can leave cleanup waiting; capture ownership stays reserved and no second
capture is started. This behavior has not been tested on physical ASI hardware.

FocusStreamChecks: 79 offline checks passed, including ASI model/alias recognition,
ASCOM/capability gates, identity changes, rectangular edge ROI, color metadata,
repeat start/stop and delayed-download cleanup. Existing QHY/ToupTek cases passed.
FocusCenteringChecks --auto-roi: 55 passed. --live-move: 19 passed with the generic
driver, and --live-move --asi: 19 passed with the native ASI category and disabled
LiveView capability. The latter exercises continued preview during focuser travel,
motor errors, Stop cleanup ordering and camera disconnection through the actual VM.
No NINA or physical device was started.

### Plugin autofocus curve overrides

Options > Manual Focuser now provides independent HFR and Spike curve selectors.
HfrCurveFit defaults to Hyperbolic (symmetric); SpikeCurveFit defaults to Parabolic.
Available alternatives are Parabolic, symmetric Hyperbolic, trend lines and their
combined models. Bahtinov displays the fixed Linear (zero crossing) signed-error
model. Settings.Settings, Settings.Designer.cs and app.config contain matching
user-scoped string settings and defaults. Invalid saved width-model strings fall
back to the respective recommended default.

RunCurveAutofocusAsync snapshots the plugin model once at startup and passes it
to FocusScanFit. It no longer reads/writes AutoFocusCurveFitting from the host
profile. The chosen model/source are recorded in diagnostics; actual fitting still
uses the existing NINA implementations. Scan step, offsets, frame count and R²
threshold retain their NINA sources. Changing options during a run applies next time.

Offline --curve passed 895 assertions including default/override resolution, all
five fit models, actual VM execution without a host curve-model property and model
pinning across an option change during detection. --fit-options passed 6 checks
using the actual plugin option bindings/setters, independent selection and settings
reload. bin/curve-fit-options.png is an offscreen render of the new options block,
not a running host screenshot. No NINA or physical hardware was started.

## Preview angle and display stretch (2026-10-08)

The angle label formerly read only the manual-exposure model, so Live and the
new Spike curve AF could detect diffraction without updating it. Both now
publish private detector results to the UI, with finite-angle/clear-spike gates.
Live and post-AF Spike monitoring analyze at most about once per second using
a central window of at most 1024 pixels; AF measurement frames publish their
own results. Invalid results, new runs and ROI changes invalidate old angles.
Use angle reads the displayed result; confidence moved into its tooltip.

An image-local footer offers a 0.25–2.5 display stretch slider and reset to 1.
FocusDisplayStretch retains automatic black/white normalization and scales the
target midtone background. Raw buffers and metric paths are not modified. Frozen
preview images retain raw pixels through weak keys, with no frame history. Idle
and ROI overview adjustments debounce for 80 ms and serialize display rerenders;
reference guards prevent replacing newer camera frames/ROI selections. Active
frames use the new strength without camera/motor restart. The controls sit outside
PreviewSurface, so its ROI mouse mapping excludes the footer.

Offline --preview-display passed 14 checks for angle detection/invalidation,
raw preservation, monotonic brightness, idle/full-frame rerender and actual footer
bindings/layout. bin/preview-stretch-240.png and -420.png are offscreen renders
of the real XAML preview section with synthetic pixels. Physical NINA UI and
camera verification were not performed.

## Commit validation (2026-10-08)

All eight offline check projects built in Release with zero warnings/errors and
DeployPlugin=false. The following runs passed 1,661 assertions in total:

| Project / run | Assertions |
| --- | ---: |
| FocusCenteringChecks (default) | 162 |
| FocusCenteringChecks --curve | 895 |
| FocusCenteringChecks --graph | 20 |
| FocusCenteringChecks --auto-roi | 55 |
| FocusCenteringChecks --live-move | 20 |
| FocusCenteringChecks --live-move --asi | 20 |
| FocusCenteringChecks --fit-options | 6 |
| FocusCenteringChecks --preview-display | 14 |
| FocusStreamChecks | 79 |
| FocusPreviewChecks | 47 |
| BahtinovChecks | 112 |
| SpikeFocusChecks | 169 |
| SpikeChecks | 31 |
| FocusChecks | 11 |
| RoiInteractionChecks | 20 |

Run each project with `dotnet run --project Tools/<project> -c Release
-p:DeployPlugin=false -- <optional arguments>`. These are fake-mediator,
synthetic-image and offline checks; they do not operate physical equipment.
Build logs, generated previews and raw diagnostic frames are excluded from Git.

## Diagnostic recording option (2026-10-08)

EnableFocusDiagnostics is a new user-scoped bool, default False, mirrored in all
three settings files. Options exposes one general Diagnostics checkbox covering
detail/timing logs, Live/AF/AutoROI raw FITS/JSON and manual spike CSV. Old
WriteSpikeDiagnostics settings remain readable for compatibility but no longer
enable recording. Error/warning logs and application status remain available.

Disabled sessions create no directory and skip raw validation/conversion,
serialization and file I/O. Enabling applies to sessions started afterwards;
turning off also stops subsequent writes from an enabled active session. Existing
archives are not removed. The offline FITS writer checks explicitly enable their
private sessions; regular mediator checks now run with recording off by default.

--fit-options passed 14 checks including the actual checkbox/save/reload, legacy
setting isolation, no-I/O disabled mode, enabled FITS/JSON/event output and live
disable. bin/diagnostics-options.png is an offscreen render of the actual option
section. Physical equipment and the NINA process were not operated for validation.
