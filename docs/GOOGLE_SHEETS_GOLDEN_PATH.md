# Google Sheets 의미 Workflow 골든 패스

## 안전 계약

이 골든 패스는 `Google 기본 화면 → Google 앱 메뉴 → Sheets 홈 → 빈 스프레드시트 → 생성 확인 → 생성 탭 닫기`만 지원한다. 비밀번호·OTP 입력, 공유, 삭제, 결제와 임의 Windows 프로그램 조작은 지원하지 않는다.

Workflow 생성 전 `DemonstrationEvidenceValidator`가 다음을 거부한다.

- 의미 있는 입력 이벤트가 없는 녹화
- 영상 범위 밖 이벤트 또는 가장 가까운 화면 증거와 1초 넘게 어긋난 이벤트
- 입력 이벤트나 화면 증거 중 한 종류라도 연결하지 않은 실행 단계
- 존재하지 않는 근거를 참조하는 단계
- 행동 증거 반영률이 80% 미만인 Workflow

`GoogleSheetsWorkflowCompiler`는 자연어 의도와 실제 MP4 파일을 함께 받아 원본 영상의 SHA-256을 Workflow에 저장한다. 네 골든 패스 단계가 모두 이벤트와 영상 프레임 증거를 참조할 때만 상태 그래프를 생성하므로, 이후 저널에서 단계 → 화면 증거/입력 이벤트 → 원본 영상 해시를 역추적할 수 있다.

## 상태 그래프와 의미 성공 조건

| 현재 의미 상태 | 전제조건과 유일 후보 | 행동 | 성공조건 |
| --- | --- | --- | --- |
| `GoogleHome` | Chrome의 활성 URL이 `google.com`, 정확히 하나의 `Google 앱` 버튼 | 클릭 | 메뉴/대화상자와 정확한 `Sheets` 항목이 함께 관찰됨 |
| `AppsMenuOpen` | 정확히 하나의 `Sheets` 링크·메뉴 항목·버튼 | 클릭 | 활성 URL이 Sheets 홈이고 정확한 빈 스프레드시트 템플릿이 관찰됨 |
| `SheetsHome` | 정확히 하나의 `빈 스프레드시트` 항목 | 클릭 | `docs.google.com/spreadsheets/d/...` URL, 제목 없음 문서 제목, 기본 `시트1` 탭이 함께 관찰됨 |
| `BlankSpreadsheetOpen` | 현재 활성 탭이 위 의미 상태로 검증됨 | 활성 탭 닫기 | 실행 직전 기록한 생성 탭 ID가 사라지고 다른 활성 탭이 관찰됨 |

매 전이 전후에 화면을 다시 관찰한다. 실행 후 의미 상태가 늦게 나타나는 경우 입력을 반복하지 않고 250ms 간격으로 최대 10회 재관찰한다. 이미 완료된 중간 상태에서 시작하면 앞 단계를 건너뛴다. 전경 앱·활성 탭·URL·역할·이름이 맞지 않거나 정확 후보가 0개/복수이면 입력하지 않고 `ABSTAINED`로 끝난다. 관찰 revision이 바뀌면 `STALE_ACTION`으로 중단한다.

## 실행 저널

각 실행 결과의 `Journal`에는 시각, 단계, 관찰 상태와 활성 탭/URL, 후보 ID 목록, 판단, 실행 결과, 성공 검증 결과가 기록된다. `GoldenPathRunResult.ToJson()`으로 원본 JSON을 보존할 수 있다. 접근성 트리가 계속 변해 관찰 자체가 실패한 경우도 `OBSERVATION_FAILED` 항목으로 남는다. 실패 시 마지막 항목만으로도 관찰·후보·판단·실행·검증 중 어느 지점에서 멈췄는지 확인할 수 있다.

## 자동 테스트

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test .\tests\Series4.Desktop.Tests\Series4.Desktop.Tests.csproj -c Release -p:Platform=x64
```

`GoogleSheetsGoldenPathTests`는 무입력, 영상/이벤트 시각 불일치, 80% 경계, 상태 건너뛰기, 잘못된 Chrome 탭, 복수 후보, 검증 실패 저널을 검증한다. 창 크기·요소 순서·브라우저 탭 순서·같은 탭/새 탭 내비게이션을 바꾼 10개 결정적 시나리오가 모두 성공하고 `WrongTargetExecutions == 0`인지 확인한다.

## 실제 Windows 통합 재현

사전 조건: Windows 10/11, Chrome, Google 로그인, 테스트 계정, 팝업을 가리지 않는 데스크톱. 실행 중 마우스와 키보드를 건드리지 않는다. 이 테스트는 빈 Google Sheets 파일을 만들지만 공유하거나 삭제하지 않는다. 테스트 계정의 Drive에 남은 빈 파일은 검토 후 사용자가 직접 정리한다.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\Series4.Desktop.csproj -c Release -p:Platform=x64 -o .\artifacts\bridge
.\scripts\test-google-sheets-golden-path.ps1 -Iterations 10
```

스크립트는 반복마다 Chrome 공식 시작 옵션으로 새 창의 위치·크기를 바꾸고, 무해한 미끼 탭의 순서를 교차시킨 뒤 Google 홈을 활성 탭으로 연다. 이어 실제 bridge의 `run_google_sheets_golden_path` 명령을 실행한다. 결과는 `artifacts/google-sheets-integration/run-N.json`과 `summary.json`에 저장한다. 통과 기준은 10회 중 9회 이상 `SUCCESS`, 잘못된 대상 실행 0회다. 불명확하거나 검증할 수 없는 화면에서는 성공률을 높이기 위해 클릭하지 않고 실패하는 것이 정상이다.

## 2026-10-04 실제 Windows bridge 검증

Chrome의 한국어 Google 계정 화면에서 다음 의미 증거를 확인했다.

- `Google 앱` 버튼이 접힘→펼침으로 바뀌고 앱 메뉴 WebArea가 나타남
- 메뉴 안에 유일한 `Sheets` 링크와 `docs.google.com/spreadsheets` 목적지가 나타남
- Sheets 홈에서 `빈 스프레드시트` 항목이 나타남
- 생성 후 `docs.google.com/spreadsheets/d/.../edit`, `제목 없는 스프레드시트`, 셀 그리드와 `시트1`이 나타남
- 생성에 사용한 Chrome 탭을 닫은 뒤 그 탭 ID가 탭 목록에서 사라짐

실제 `공공AX-업무매크로.exe --bridge`와 로그인된 Chrome에서 창 위치·크기 10종과 탭 순서 2종을 교차해 실행했다. 최종 코호트는 10/10 `SUCCESS`, 잘못된 대상 실행 0회, 안전성 미확인 0회였고 네 단계의 의미 성공조건이 모든 실행에서 확인됐다. 원본 저널은 민감할 수 있는 실시간 URL과 런타임 ID 때문에 `artifacts/google-sheets-integration-final-v2`에 로컬 보존하며, 비식별 검증 요약은 [google-sheets-windows-2026-10-04.json](verification/google-sheets-windows-2026-10-04.json)에 포함한다.
