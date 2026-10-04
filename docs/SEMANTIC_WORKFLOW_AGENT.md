# 일반화 의미 Workflow 에이전트

## 목표와 현재 경계

`SemanticWorkflowCompiler`와 `SemanticWorkflowRunner`는 Google Sheets 같은 특정 앱이나 고정 단계 이름을 알지 못한다. 한 번의 시연에서 얻은 자연어 의도, 원본 영상, 동기화 입력 이벤트, 입력 전후의 의미 화면을 받아 상태 기반 Workflow를 만들고 현재 Windows 화면을 매 단계 다시 관찰해 실행한다.

의미 화면과 입력 대상까지 포함된 `SemanticDemonstration` JSON부터 실제 Windows 실행까지 연결되어 있다. 또한 새 녹화의 왼쪽 클릭은 누르는 순간의 전경 창·UI Automation 요소·클릭 대상을 `RecordedEvent`에 자동 저장하며, 현재 녹화에서 직접 Workflow를 만드는 경로가 있다. 현재 자동 추출 범위는 의미 대상이 확인된 왼쪽 클릭과 체크박스/라디오 토글이다. 키 입력을 텍스트 동작으로 묶는 과정과 MP4 픽셀 기반 의미 추출은 아직 연결되지 않았으므로 “임의 녹화 영상을 바로 자동화한다”고 표현해서는 안 된다.

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
- 입력 대상이 0개 또는 복수임
- 전후 화면 순서 또는 상태 연결이 끊김
- 실행 뒤 상태를 이전 상태와 의미적으로 구분할 수 없음
- 비밀번호 대상, 비활성 대상, 삭제·결제 등 비가역 의도

## 일반화 컴파일

컴파일러는 고정 템플릿 대신 시연의 각 전이를 분석한다.

1. 입력 직전 화면과 의미 대상을 전제조건으로 만든다.
2. 입력 뒤 새로 생기거나 값이 바뀐 요소, 창 제목, URL 범위를 성공조건으로 만든다.
3. 단계마다 이벤트 인덱스와 전후 화면 ID를 보존한다.
4. 원본 영상 SHA-256과 증거 반영률을 Workflow에 저장한다.

지원하는 안전 동작은 `click`, `type`, `select`, `toggle`이다. 대상은 역할·이름·Automation ID로 재탐색하며 좌표를 Workflow에 저장하지 않는다.

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
  "workflow_path": "D:\\recordings\\note.workflow.json"
}
```

새 녹화를 중지한 뒤 저장된 클릭 의미 증거에서 바로 Workflow를 만든다.

```json
{
  "id": 3,
  "action": "compile_current_semantic_workflow",
  "intent": "두 화면을 진행해 완료 상태로 만들어 줘",
  "workflow_path": "D:\\recordings\\recorded.workflow.json"
}
```

지원하지 않는 키·드래그·휠 입력이 섞였거나 클릭 대상 증거가 없으면 부분 동작을 억지로 재생하지 않고 컴파일을 거부한다. 녹화와 함께 저장된 의미 증거는 `.series4.json`을 저장하고 다시 열어도 유지된다.

## 사용자 경험 검증

`SemanticWorkflowExperienceTests`는 구현 클래스의 내부 호출 횟수 대신 최종 화면 상태를 평가한다.

- 메모 업무: 새 메모 화면이 열리고 `회의록`이 실제 편집 값으로 남으며 `저장됨`이 보임
- 설정 업무: 어두운 모드 값이 `켬`이 되고 사용 중 상태가 보임
- 이미 완료된 화면: 입력을 반복하지 않고 성공
- 복수 후보: 사용자 화면을 바꾸지 않고 중단

실제 Windows UI Automation 통합 테스트는 별도 WPF 픽스처 창에서 같은 두 결과를 확인한다.

```powershell
.\scripts\test-semantic-workflow-windows.ps1
```

테스트는 실제 창을 열고 공용 Windows 실행 표면으로 조작한 뒤, 사용자가 보게 되는 텍스트·설정 상태를 다시 관찰한다. 로컬 실행 결과는 `artifacts/semantic-workflow-windows`에 저장되며 Git에는 포함하지 않는다.

2026-10-04 대화형 Windows 실행은 2개 시나리오 모두 통과했다. 민감한 런타임 식별자를 제거한 결과는 [`verification/semantic-workflow-windows-2026-10-04.json`](verification/semantic-workflow-windows-2026-10-04.json)에 보존한다.

## 다음 완료 게이트

전체 목표 완료에는 다음 연결이 추가로 필요하다.

1. 연속 키 입력을 비밀번호·IME 안전 규칙 아래 하나의 의미 텍스트 입력으로 묶는다.
2. 드래그·휠·키보드 탐색을 의미 동작과 성공조건으로 확장한다.
3. MP4 프레임 픽셀 근거를 접근성 관찰과 함께 단계별로 해시·연결한다.
4. 서로 다른 실제 사용자 앱의 녹화 시작부터 자동 추출·실행 결과까지 완전한 종단 간 검증을 반복한다.
