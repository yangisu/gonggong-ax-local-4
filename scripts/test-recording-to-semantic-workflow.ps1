param(
    [string]$OutputDirectory = "artifacts/recording-to-semantic-workflow",
    [string]$EnginePath = "",
    [string]$FixturePath = "",
    [ValidateSet("text", "scroll", "keyboard", "slider", "selection", "focus")]
    [string]$Scenario = "text"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
$output = Join-Path $repository $OutputDirectory
New-Item -ItemType Directory -Force -Path $output | Out-Null

if (-not $EnginePath) {
    $engineDirectory = Join-Path $output "engine"
    & $dotnet build (Join-Path $repository "Series4.Desktop.csproj") -c Release -p:Platform=x64 --no-restore -o $engineDirectory
    if ($LASTEXITCODE -ne 0) { throw "통합 테스트용 엔진 빌드가 실패했습니다." }
    $EnginePath = Join-Path $engineDirectory "공공AX-업무매크로.exe"
}
if (-not $FixturePath) {
    & $dotnet build (Join-Path $repository "tests\SemanticWorkflowFixture\SemanticWorkflowFixture.csproj") -c Release -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw "사용자 경험 픽스처 빌드가 실패했습니다." }
    $FixturePath = Join-Path $repository "tests\SemanticWorkflowFixture\bin\x64\Release\net10.0-windows\SemanticWorkflowFixture.exe"
}
if (-not (Test-Path -LiteralPath $EnginePath -PathType Leaf)) { throw "엔진을 찾지 못했습니다: $EnginePath" }
if (-not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) { throw "픽스처를 찾지 못했습니다: $FixturePath" }

$engine = New-Object System.Diagnostics.Process
$engine.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
$engine.StartInfo.FileName = $EnginePath
$engine.StartInfo.Arguments = "--bridge --parent-pid $PID"
$engine.StartInfo.UseShellExecute = $false
$engine.StartInfo.RedirectStandardInput = $true
$engine.StartInfo.RedirectStandardOutput = $true
$engine.StartInfo.RedirectStandardError = $true
$engine.StartInfo.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
$engine.StartInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
$engine.StartInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
$engine.StartInfo.CreateNoWindow = $true
[void]$engine.Start()

$script:nextId = 0
$script:lastState = $null
function Invoke-BridgeCommand([hashtable]$payload, [int]$timeoutSeconds = 45) {
    $script:nextId++
    $payload.id = $script:nextId
    $engine.StandardInput.WriteLine(($payload | ConvertTo-Json -Compress -Depth 12))
    $engine.StandardInput.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $engine.StandardOutput.ReadLine()
        if (-not $line) { continue }
        $message = $line | ConvertFrom-Json
        if ($message.type -eq "state") { $script:lastState = $message.state; continue }
        if ($message.id -eq $script:nextId) {
            if (-not $message.ok) { throw "브리지 명령 $($payload.action) 실패: $($message.error)" }
            return $message.result
        }
    }
    throw "브리지 명령 $($payload.action) 응답 시간이 초과되었습니다."
}

$demonstration = $null
$replay = $null
try {
    [void](Invoke-BridgeCommand @{ action = "record" } 40)
    $demonstrationMode = switch ($Scenario) {
        "scroll" { "scroll-demo" }
        "keyboard" { "keyboard-demo" }
        "slider" { "slider-demo" }
        "selection" { "selection-demo" }
        "focus" { "focus-demo" }
        default { "editor-demo" }
    }
    $intent = switch ($Scenario) {
        "scroll" { "업무 목록을 내려 아래 항목을 보여 줘" }
        "keyboard" { "키보드로 어두운 모드를 켜 줘" }
        "slider" { "음량을 높여 줘" }
        "selection" { "색상을 파랑으로 선택해 줘" }
        "focus" { "Tab으로 검색 버튼까지 이동해 줘" }
        default { "새 메모를 만들고 meeting을 입력해 줘" }
    }
    $expectedAction = switch ($Scenario) {
        "scroll" { "scroll" }
        "keyboard" { "toggle" }
        "slider" { "set-range" }
        "selection" { "select-option" }
        "focus" { "focus" }
        default { "type" }
    }
    $demonstration = Start-Process -FilePath $FixturePath -ArgumentList $demonstrationMode -PassThru
    Start-Sleep -Seconds 9
    [void](Invoke-BridgeCommand @{ action = "stop_recording" } 30)
    Start-Sleep -Seconds 2

    $workflowPath = Join-Path $output "recorded-$Scenario.workflow.json"
    $compiled = Invoke-BridgeCommand @{
        action = "compile_current_semantic_workflow"
        intent = $intent
        workflow_path = $workflowPath
    } 30
    if ($compiled.stepCount -ne 1 -or $compiled.evidenceCoverage -ne 1 -or $compiled.videoEvidenceCoverage -ne 1) {
        throw "녹화 Workflow가 기대한 1단계·100% 입력/MP4 프레임 증거로 컴파일되지 않았습니다."
    }
    $workflowDocument = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
    $invalidVideoEvidence = @($workflowDocument.videoEvidence | Where-Object {
        $_.videoFrameSha256 -notmatch '^[0-9a-f]{64}$'
    })
    if (@($workflowDocument.videoEvidence).Count -lt 2 -or $invalidVideoEvidence.Count -ne 0) {
        throw "단계별 MP4 픽셀 해시가 Workflow에 보존되지 않았습니다."
    }
    if ($workflowDocument.steps[0].action.kind -ne $expectedAction) {
        throw "녹화 동작이 기대한 의미 단계($expectedAction)로 추출되지 않았습니다."
    }

    if ($demonstration -and -not $demonstration.HasExited) {
        $demonstration.CloseMainWindow() | Out-Null
        if (-not $demonstration.WaitForExit(3000)) { $demonstration.Kill($true) }
    }
    $replayMode = switch ($Scenario) {
        "scroll" { "scroll" }
        "keyboard" { "settings" }
        "slider" { "slider" }
        "selection" { "selection" }
        "focus" { "focus" }
        default { "editor" }
    }
    $replay = Start-Process -FilePath $FixturePath -ArgumentList $replayMode -PassThru
    $replay.WaitForInputIdle(10000) | Out-Null
    Start-Sleep -Milliseconds 700

    $run = Invoke-BridgeCommand @{ action = "run_semantic_workflow"; workflow_path = $workflowPath } 30
    $observed = Invoke-BridgeCommand @{ action = "observe_semantic"; max_elements = 200 } 15
    $editor = @($observed.elements | Where-Object { $_.automation_id -eq "note-editor" -and $_.value -eq "meeting" })
    $saved = @($observed.elements | Where-Object { $_.name -eq "저장됨" })
    $scrolled = @($observed.elements | Where-Object { $_.automation_id -eq "scroll-status" -and $_.name -eq "아래 항목 표시됨" })
    $toggled = @($observed.elements | Where-Object { $_.automation_id -eq "dark-mode" -and $_.value -eq "On" })
    $darkStatus = @($observed.elements | Where-Object { $_.automation_id -eq "mode-status" -and $_.name -eq "어두운 모드 사용 중" })
    $expectedRangeValue = if ($Scenario -eq "slider") { [string]$workflowDocument.steps[0].action.value } else { $null }
    $adjusted = @($observed.elements | Where-Object { $_.automation_id -eq "volume-slider" -and $_.value -eq $expectedRangeValue })
    $rangeStatus = @($observed.elements | Where-Object { $_.automation_id -eq "volume-status" -and $_.name -eq "음량 $expectedRangeValue" })
    $expectedOption = if ($Scenario -eq "selection") { [string]$workflowDocument.steps[0].action.value } else { $null }
    $selected = @($observed.elements | Where-Object { $_.automation_id -eq "color-selector" -and $_.value -eq $expectedOption })
    $selectionStatus = @($observed.elements | Where-Object { $_.automation_id -eq "color-status" -and $_.name -eq "선택: $expectedOption" })
    $focused = @($observed.elements | Where-Object { $_.automation_id -eq "focus-search" -and $_.keyboard_focused -eq $true })
    $focusStatus = @($observed.elements | Where-Object { $_.automation_id -eq "focus-status" -and $_.name -eq "포커스: 검색" })
    $outcomeVisible = switch ($Scenario) {
        "scroll" { $scrolled.Count -eq 1 }
        "keyboard" { $toggled.Count -eq 1 -and $darkStatus.Count -eq 1 }
        "slider" { $adjusted.Count -eq 1 -and $rangeStatus.Count -eq 1 }
        "selection" { $selected.Count -eq 1 -and $selectionStatus.Count -eq 1 }
        "focus" { $focused.Count -eq 1 -and $focusStatus.Count -eq 1 }
        default { $editor.Count -eq 1 -and $saved.Count -eq 1 }
    }
    $passed = $run.Status -eq "SUCCESS" -and $outcomeVisible
    $summary = [ordered]@{
        executedAt = [DateTimeOffset]::Now
        scenario = $Scenario
        source = "actual ScreenRecorderLib MP4 + SharpHook input events + automatic semantic capture"
        recordedEventCount = $script:lastState.events.Count
        workflowPath = $compiled.workflowPath
        sourceVideoSha256 = $compiled.sourceVideoSha256
        stepCount = $compiled.stepCount
        evidenceCoverage = $compiled.evidenceCoverage
        videoEvidenceCoverage = $compiled.videoEvidenceCoverage
        videoEvidenceFrameCount = @($workflowDocument.videoEvidence).Count
        runStatus = $run.Status
        visibleEditorValue = if ($editor.Count -eq 1) { $editor[0].value } else { $null }
        visibleControlValue = if ($toggled.Count -eq 1) { $toggled[0].value } elseif ($adjusted.Count -eq 1) { $adjusted[0].value } elseif ($selected.Count -eq 1) { $selected[0].value } elseif ($focused.Count -eq 1) { "keyboard-focused" } else { $null }
        visibleStatus = if ($Scenario -eq "scroll" -and $scrolled.Count -eq 1) { $scrolled[0].name } elseif ($Scenario -eq "keyboard" -and $darkStatus.Count -eq 1) { $darkStatus[0].name } elseif ($Scenario -eq "slider" -and $rangeStatus.Count -eq 1) { $rangeStatus[0].name } elseif ($Scenario -eq "selection" -and $selectionStatus.Count -eq 1) { $selectionStatus[0].name } elseif ($Scenario -eq "focus" -and $focusStatus.Count -eq 1) { $focusStatus[0].name } elseif ($saved.Count -eq 1) { $saved[0].name } else { $null }
        passed = $passed
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $output "summary.json")
    $run | ConvertTo-Json -Depth 16 | Set-Content -Encoding utf8 (Join-Path $output "run.json")
    $summary | ConvertTo-Json -Depth 8
    if (-not $passed) { throw "녹화→Workflow→사용자 결과 종단 간 검증이 실패했습니다." }
}
finally {
    foreach ($process in @($demonstration, $replay)) {
        if ($process -and -not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(3000)) { $process.Kill($true) }
        }
    }
    if (-not $engine.HasExited) {
        $engine.StandardInput.Close()
        if (-not $engine.WaitForExit(5000)) { $engine.Kill($true) }
    }
}
