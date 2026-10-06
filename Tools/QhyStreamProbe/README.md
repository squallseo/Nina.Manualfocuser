# QHY600M 스트리밍 진단

NINA가 배포한 QHY SDK로 카메라 capability, 256×256 16bit ROI 스트리밍,
취소 및 단일 촬영 복귀를 확인하는 Windows x64 독립 도구다. 플러그인에는 포함되지 않는다.
실장비 측정 결과와 NINA 공개 API 검토는 [인계 문서](../../Docs/HardwareHandoff.md)에 있다.

NINA, SharpCap 등 카메라를 사용하는 프로그램을 종료하고 실행한다. 도구는 이름에
NINA/SharpCap/EZCAP이 있는 실행 중 프로세스를 검사하지만, 모든 프로그램에 대한 독점 잠금을 제공하지는 않는다.
QHY600 한 대만 연결된 경우에만 카메라를 연다. SDK 경로는 표준 NINA 설치 경로다.

```powershell
# 설치된 NINA 공개 LiveView 메서드 및 QHY capability getter IL 확인. 카메라 접근 없음.
dotnet run --project Tools/QhyStreamProbe -c Release -- --host-api

# SDK 버전, 카메라 열거, capability와 read mode 조회. 촬영 없음.
dotnet run --project Tools/QhyStreamProbe -c Release

# 100/250/500ms 각각 23프레임. 첫 3프레임 제외 후 수신 간격 집계.
# 스트림 종료 후 100ms 단일 촬영 확인.
dotnet run --project Tools/QhyStreamProbe -c Release -- --stream

# 500ms 스트림 시작 후 1500ms에 취소, Stop 호출 시간 및 단일 촬영 복귀 확인.
dotnet run --project Tools/QhyStreamProbe -c Release -- --stream --cancel-test
```

`--stream`은 냉각이나 포커서 명령을 보내지 않는다. single-mode SDK 초기화 후 조회한
gain/offset/exposure/transfer bits/USB traffic과 read mode를 복원하고 전체 센서 ROI로 되돌린 뒤
핸들을 닫고 SDK 자원을 해제한다. 다른 프로그램이 이전에 사용한 설정이나 ROI를 보존하는 도구는 아니다.
NINA 프로필이나 설치된 DLL은 수정하지 않는다.

Ctrl+C는 프레임 수신 루프에 취소를 요청하고 정리 코드로 진행한다. 루프의 25초 제한은
SDK의 개별 native 호출이 영구적으로 막힌 경우를 중단시키지는 못한다.
반환된 픽셀은 크기와 통계만 확인하며 파일로 저장하지 않는다. 광학 초점이나 별 검출 정확도 시험은 별도로 필요하다.
출력에 카메라 식별자가 포함되므로 원본 로그 공유 전 확인한다.
