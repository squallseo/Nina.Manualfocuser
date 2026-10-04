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

현재 Live focus는 `CaptureImage`로 한 장씩 순차 촬영한다. 실제 연속 영상 스트림은 아직 구현하지 않았다.
노출 ms와 실제 화면 갱신 ms는 다르다. 카메라가 ROI를 지원하지 않거나 요청을 무시하면 전체 영상을 다운로드한 뒤 크롭한다.
모드를 변경하면 Live 촬영을 취소한다. 촬영 중 In/Out은 프레임 사이에 실행된다.

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
필요하면 NINA QHY 드라이버 테스트 빌드에서 시작/프레임 수신/취소/종료, ROI, 설정 복원과 일반 촬영 복귀를 검증한다.
같은 카메라를 플러그인이 별도의 QHY SDK 연결로 동시에 열지 않는다. 실행 중 카메라에 reflection으로 LiveView를 강제로 켜는 코드도 넣지 않았다.
연속 스트림에서는 최신 프레임 우선 표시와 분석 주기 분리가 필요하다. 이 부분은 미구현이며 NINA upstream 변경이 필요할 수 있다.

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
