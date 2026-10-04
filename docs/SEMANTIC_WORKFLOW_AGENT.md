# 일반화 의미 Workflow 에이전트

## 목표와 현재 경계

`SemanticWorkflowCompiler`와 `SemanticWorkflowRunner`는 Google Sheets 같은 특정 앱이나 고정 단계 이름을 알지 못한다. 한 번의 시연에서 얻은 자연어 의도, 원본 영상, 동기화 입력 이벤트, 입력 전후의 의미 화면을 받아 상태 기반 Workflow를 만들고 현재 Windows 화면을 매 단계 다시 관찰해 실행한다.

의미 화면과 입력 대상까지 포함된 `SemanticDemonstration` JSON부터 실제 Windows 실행까지 연결되어 있다. 새 녹화의 왼쪽 클릭은 누르는 순간의 전경 창·UI Automation 요소·클릭 대상을 저장한다. 편집 필드의 연속 키 입력은 키 배열이나 자판 배열을 추측하지 않고 각 키의 입력 전·해제 후 필드 값으로 하나의 `type` 단계로 묶는다. 따라서 한국어 IME의 조합 중간값(`ㅎ`→`호`→`회` 등)이 여러 번 바뀌어도 최종 관찰값 `회의록`을 실행 값으로 사용한다. 버튼의 `Enter`/`Space`와 체크박스·라디오 버튼의 `Space`는 원시 키 재생이 아니라 각각 의미 `click`/`toggle`로 승격한다. 콤보박스·목록의 연속 방향키 탐색은 키 횟수가 아니라 최종 선택값 이름을 가진 `select-option`으로 만든다. 연속 `Tab`/`Shift+Tab` 탐색은 횟수 대신 마지막 실제 포커스 소유자를 가진 `focus` 단계로 만들고, UI Automation의 단일 `FocusedElement` 런타임 ID로 부모·내부 요소의 중복 포커스 보고를 정규화한다. 휠 입력은 좌표가 아니라 스크롤 가능한 의미 요소를 찾아 연속 제스처를 하나의 `scroll` 단계로 묶고, 정확한 스크롤 퍼센트 대신 새로 나타난 사용자 결과로 성공을 판정한다. 슬라이더 드래그는 궤적을 재생하지 않고 전후 범위 값을 확인해 `set-range` 단계로 만든다. 목록 항목 드래그는 시연 전후의 UI Automation 형제 순서를 비교해 `reorder-item`과 최종 ordinal로 만들며, 재실행 때 현재 항목 위치와 화면 좌표를 새로 계산하고 이동된 항목의 의미 순서를 성공조건으로 확인한다. 현재 녹화에서 직접 Workflow를 만드는 경로가 있으며 자동 추출 범위는 의미 대상이 확인된 왼쪽 클릭, 키보드 활성화, 방향키 선택, `Tab` 포커스 이동, 체크박스/라디오 토글, IME 포함 편집 필드 텍스트 입력, 휠 스크롤, 슬라이더 드래그와 목록 재정렬 드래그다. 단계가 참조하는 각 화면 시점은 실제 MP4에서 렌더링한 픽셀 SHA-256과 연결된다. 의미 순서를 관찰할 수 없는 자유형 캔버스 드래그와 픽셀만으로 의미를 추론하는 경로는 아직 연결되지 않았으므로 “임의 녹화 영상을 바로 자동화한다”고 표현해서는 안 된다.

## 시연 계약

시연은 다음 정보를 포함한다.

- 사용자의 자연어 의도
- 실제 원본 영상 경로와 길이
- 영상 시간축에 동기화된 입력 이벤트
- 각 입력 직전·직후 화면의 프로세스, 창 제목, URL, 의미 요소
- 실행할 의미 동작과 정확한 대상 역할·이름·Automation ID

컴파일 전에 다음을 거부한다.

- 영상 파일 또는 의미 입력 이벤트가 없음
- 이벤트와 화면 증거의 시간 차이가 허용 범위를 넘음
- 행동 증거 반영률이 80% 미만
- 참조 화면 중 실제 MP4 픽셀 해시가 연결된 비율이 80% 미만
- 입력 대상이 0개 또는 복수임
- 전후 화면 순서 또는 상태 연결이 끊김
- 실행 뒤 상태를 이전 상태와 의미적으로 구분할 수 없음
- 비밀번호 대상, 비활성 대상, 삭제·결제 등 비가역 의도

## 일반화 컴파일

컴파일러는 고정 템플릿 대신 시연의 각 전이를 분석한다.

1. 입력 직전 화면과 의미 대상을 전제조건으로 만든다.
2. 입력 뒤 새로 생기거나 값이 바뀐 요소, 창 제목, URL 범위를 성공조건으로 만든다.
3. 단계마다 모든 원시 이벤트 인덱스와 전후·중간 화면 ID를 보존한다.
4. 참조 화면 시점의 실제 MP4 렌더링 픽셀 SHA-256과 영상 근거 반영률을 저장한다.
5. 원본 영상 경로와 파일 SHA-256을 저장하고 실행 직전에 다시 계산해 변조·교체를 거부한다.

지원하는 안전 동작은 `click`, `type`, `select`, `select-option`, `toggle`, `scroll`, `set-range`, `focus`, `reorder-item`이다. 대상은 역할·이름·Automation ID로 재탐색하며 좌표·드래그 궤적·`Tab` 횟수를 Workflow에 저장하지 않는다.

## 상태 기반 실행

실행기는 매 단계 현재 전경 창을 다시 관찰한다. 이미 뒤 단계의 의미 상태라면 앞 단계를 반복하지 않는다. 현재 상태, 실행 대상 또는 성공 상태를 정확히 판정할 수 없으면 입력을 보내지 않고 `ABSTAINED`로 종료한다.

Windows 표면은 실행 직전에 다음을 다시 확인한다.

- 프로세스·창 제목·관찰 revision이 바뀌지 않음
- 의미 대상이 정확히 하나임
- 대상이 활성 상태이며 비밀번호 필드가 아님
- 화면 밖 대상은 UI Automation 스크롤로 다시 표시하고 재탐색됨

각 결과의 저널에는 관찰, 후보, 결정, 실행, 검증이 별도 단계로 기록된다.

## 브리지 사용

시연 manifest를 Workflow 파일로 컴파일한다.

```json
{
  "id": 1,
  "action": "compile_semantic_workflow",
  "demonstration_path": "D:\\recordings\\note.semantic.json",
  "workflow_path": "D:\\recordings\\note.workflow.json"
}
```

현재 전경 Windows 앱에서 Workflow를 실행한다.

```json
{
  "id": 2,
  "action": "run_semantic_workflow",
  "workflow_path": "D:\\recordings\\note.workflow.json",
  "source_video_path": "D:\\recordings\\note.mp4"
}
```

`source_video_path`는 Workflow와 영상을 함께 옮긴 경우에만 지정하는 선택 항목이다. 생략하면 Workflow에 기록된 원본 경로를 사용하며, 어느 경우든 저장된 SHA-256과 일치해야 실행된다.

새 녹화를 중지한 뒤 저장된 클릭 의미 증거에서 바로 Workflow를 만든다.

```json
{
  "id": 3,
  "action": "compile_current_semantic_workflow",
  "intent": "두 화면을 진행해 완료 상태로 만들어 줘",
  "workflow_path": "D:\\recordings\\recorded.workflow.json"
}
```

지원하지 않는 캔버스 드래그가 섞였거나 의미 대상 증거가 없으면 부분 동작을 억지로 재생하지 않고 컴파일을 거부한다. `Tab` 탐색도 입력 전후에 실제 포커스 소유자를 정확히 하나 확인할 수 없으면 거부한다. 비밀번호·OTP 계열 이름, UI Automation 비밀번호 필드, Ctrl/Alt/Win 조합은 자동 텍스트 단계로 만들지 않는다. 편집 필드 클릭은 뒤따르는 텍스트 입력의 전제조건으로 병합되며, 모든 원시 키·휠·드래그 이벤트와 전후 화면 ID가 한 단계의 근거로 보존된다. 녹화와 함께 저장된 의미 증거는 `.series4.json`을 저장하고 다시 열어도 유지된다.

## 사용자 경험 검증

`SemanticWorkflowExperienceTests`는 구현 클래스의 내부 호출 횟수 대신 최종 화면 상태를 평가한다.

- 메모 업무: 새 메모 화면이 열리고 `회의록`이 실제 편집 값으로 남으며 `저장됨`이 보임
- 한국어 IME 업무: 실제 두벌식 조합 키열을 최종 값 기반 `type`으로 실행해 새 창에서 `회의록`과 `한글 저장됨`이 보임
- 설정 업무: 어두운 모드 값이 `켬`이 되고 사용 중 상태가 보임
- 목록 업무: 녹화된 휠 입력을 새 창에 재실행해 `아래 항목 표시됨`이 보임
- 키보드 업무: 녹화된 `Space` 활성화를 의미 토글로 실행해 `On`과 `어두운 모드 사용 중`이 보임
- 범위 조절 업무: 녹화된 슬라이더 드래그를 의미 값 설정으로 실행해 `75`와 `음량 75`가 보임
- 목록 재정렬 업무: 녹화된 드래그를 최종 ordinal 이동으로 실행해 `업무 B`가 네 번째가 되고 `순서: A,C,D,B`가 보임
- 항목 선택 업무: 녹화된 방향키 2개를 최종 옵션 선택으로 실행해 `파랑`과 `선택: 파랑`이 보임
- 포커스 이동 업무: 녹화된 `Tab` 2개를 최종 의미 대상 포커스로 실행해 검색 버튼의 실제 키보드 포커스와 `포커스: 검색`이 보임
- 이미 완료된 화면: 입력을 반복하지 않고 성공
- 복수 후보: 사용자 화면을 바꾸지 않고 중단

실제 Windows UI Automation 통합 테스트는 별도 WPF 픽스처 창에서 같은 두 결과를 확인한다.

```powershell
.\scripts\test-semantic-workflow-windows.ps1
```

테스트는 실제 창을 열고 공용 Windows 실행 표면으로 조작한 뒤, 사용자가 보게 되는 텍스트·설정 상태를 다시 관찰한다. 로컬 실행 결과는 `artifacts/semantic-workflow-windows`에 저장되며 Git에는 포함하지 않는다.

2026-10-04 대화형 Windows 실행은 메모·한국어 IME·설정·스크롤·키보드 활성화·슬라이더·목록 재정렬·항목 선택·포커스 이동 9개 시나리오 모두 통과했다. 민감한 런타임 식별자를 제거한 결과는 [`verification/semantic-workflow-windows-2026-10-04.json`](verification/semantic-workflow-windows-2026-10-04.json)에 보존한다.

같은 날 실제 ScreenRecorderLib MP4와 SharpHook 키 이벤트 7개를 녹화하고, 자동 의미 추출로 입력 이벤트 100%와 참조 화면 16개 모두에 실제 MP4 픽셀 해시가 연결된 Workflow를 만든 뒤 새 빈 편집기에 재실행하는 종단 간 테스트도 통과했다. 실행 직전 원본 영상 SHA-256도 재검증했으며, 최종 사용자 화면에서 `meeting`과 `저장됨`을 다시 관찰했다. 결과는 [`verification/recording-to-semantic-workflow-2026-10-04.json`](verification/recording-to-semantic-workflow-2026-10-04.json)에 보존하며 다음 명령으로 재현한다.

```powershell
.\scripts\test-recording-to-semantic-workflow.ps1
```

실제 휠 녹화 2개를 하나의 의미 스크롤로 추출해 새 창에서 `아래 항목 표시됨`을 확인한 결과는 [`verification/recording-scroll-workflow-2026-10-04.json`](verification/recording-scroll-workflow-2026-10-04.json)에 보존한다.

```powershell
.\scripts\test-recording-to-semantic-workflow.ps1 -Scenario scroll -OutputDirectory artifacts/recording-scroll-workflow
```

실제 `Space` 키 녹화를 체크박스 의미 토글로 추출해 새 창에서 `On`과 `어두운 모드 사용 중`을 확인한 결과는 [`verification/recording-keyboard-workflow-2026-10-04.json`](verification/recording-keyboard-workflow-2026-10-04.json)에 보존한다.

```powershell
.\scripts\test-recording-to-semantic-workflow.ps1 -Scenario keyboard -OutputDirectory artifacts/recording-keyboard-workflow
```

실제 슬라이더 드래그 녹화를 의미 값 설정으로 추출해 새 창에서 `75`와 `음량 75`를 확인한 결과는 [`verification/recording-slider-workflow-2026-10-04.json`](verification/recording-slider-workflow-2026-10-04.json)에 보존한다.

```powershell
.\scripts\test-recording-to-semantic-workflow.ps1 -Scenario slider -OutputDirectory artifacts/recording-slider-workflow
```

실제 방향키 녹화를 최종 옵션 선택으로 추출해 새 창에서 `파랑`과 `선택: 파랑`을 확인한 결과는 [`verification/recording-selection-workflow-2026-10-04.json`](verification/recording-selection-workflow-2026-10-04.json)에 보존한다.

```powershell
.\scripts\test-recording-to-semantic-workflow.ps1 -Scenario selection -OutputDirectory artifacts/recording-selection-workflow
```

실제 `Tab` 키 2개를 최종 의미 대상 포커스로 추출해 새 창의 검색 버튼에서 실제 키보드 포커스와 `포커스: 검색`을 확인한 결과는 [`verification/recording-focus-workflow-2026-10-04.json`](verification/recording-focus-workflow-2026-10-04.json)에 보존한다.

```powershell
.\scripts\test-recording-to-semantic-workflow.ps1 -Scenario focus -OutputDirectory artifacts/recording-focus-workflow
```

실제 Microsoft 한국어 IME에서 두벌식 키열 `ghldmlfhr`로 `회의록`을 조합하는 장면을 녹화하고, 10개 의미 키 이벤트를 하나의 최종 값 기반 `type` 단계로 추출해 새 창에서 `회의록`과 `한글 저장됨`을 확인했다. 입력 이벤트와 22개 참조 MP4 프레임의 증거 반영률은 모두 100%다. 결과는 [`verification/recording-korean-ime-workflow-2026-10-04.json`](verification/recording-korean-ime-workflow-2026-10-04.json)에 보존한다.

```powershell
.\scripts\test-recording-to-semantic-workflow.ps1 -Scenario korean -OutputDirectory artifacts/recording-korean-workflow
```

실제 목록 항목 드래그를 최종 의미 순서 변경으로 추출해 새 창에서 `업무 B=ordinal=3`과 `순서: A,C,D,B`를 확인한 결과는 [`verification/recording-reorder-workflow-2026-10-04.json`](verification/recording-reorder-workflow-2026-10-04.json)에 보존한다.

```powershell
.\scripts\test-recording-to-semantic-workflow.ps1 -Scenario reorder -OutputDirectory artifacts/recording-reorder-workflow
```

## 다음 완료 게이트

전체 목표 완료에는 다음 연결이 추가로 필요하다.

1. 일본어 등 다른 언어 IME와 더 긴 조합·후보 선택 입력을 실제 사용자 녹화로 반복 검증한다.
2. 의미 순서를 제공하지 않는 자유형 캔버스 드래그를 영상 기반 동작과 성공조건으로 확장한다.
3. 접근성 정보가 일부 부족한 화면에서도 MP4 픽셀로 의미 후보를 제안하되 모호하면 중단하는 추론 계층을 추가한다.
4. 서로 다른 실제 사용자 앱의 녹화 시작부터 자동 추출·실행 결과까지 완전한 종단 간 검증을 반복한다.
