param(
    [ValidateRange(1, 10)]
    [int]$Iterations = 10,
    [string]$OutputDirectory = "artifacts/google-sheets-integration",
    [string]$EnginePath = ""
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repository $OutputDirectory
New-Item -ItemType Directory -Force -Path $output | Out-Null
$engine = if ($EnginePath) { $EnginePath } else { Join-Path $repository "artifacts/bridge/공공AX-업무매크로.exe" }
if (-not (Test-Path -LiteralPath $engine)) {
    throw "먼저 scripts/build-release.ps1 또는 README의 bridge 빌드를 실행하세요."
}

$chromeCandidates = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
)
$chrome = $chromeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $chrome) { throw "Google Chrome을 찾지 못했습니다." }

$results = @()
for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    # Chrome's own startup options vary the real window bounds without using
    # coordinate automation. A harmless decoy changes tab order; Google is
    # opened last so the intended start state is the active tab.
    $x = 40 + (($iteration - 1) % 5) * 55
    $y = 35 + (($iteration - 1) % 3) * 45
    $width = 920 + (($iteration - 1) % 4) * 110
    $height = 700 + (($iteration - 1) % 3) * 80
    $decoy = "https://example.com/?golden-path=$iteration"
    $urls = if ($iteration % 2 -eq 0) { @("https://www.google.com/", $decoy) } else { @("https://www.google.com/", "chrome://newtab/", $decoy) }
    $chromeArguments = @("--new-window", "--window-position=$x,$y", "--window-size=$width,$height") + $urls
    Start-Process -FilePath $chrome -ArgumentList $chromeArguments
    Start-Sleep -Seconds 3

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $process.StartInfo.FileName = $engine
    $process.StartInfo.Arguments = "--bridge --parent-pid $PID"
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.CreateNoWindow = $true
    [void]$process.Start()
    $command = @{ id = 1; action = "run_google_sheets_golden_path"; window_bounds = @($x, $y, $width, $height) } | ConvertTo-Json -Compress
    $process.StandardInput.WriteLine($command)
    $process.StandardInput.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $response = $null
    while ([DateTime]::UtcNow -lt $deadline -and -not $response) {
        $line = $process.StandardOutput.ReadLine()
        if (-not $line) { continue }
        $message = $line | ConvertFrom-Json
        if ($message.id -eq 1) { $response = $message }
    }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
    if (-not $response) { throw "반복 ${iteration}: 엔진 응답 시간이 초과되었습니다." }
    $safeStopStatuses = @("SUCCESS", "ABSTAINED", "TRANSITION_LIMIT", "OBSERVATION_FAILED")
    $semanticStatus = if ($response.ok) { [string]$response.result.Status } else { "BRIDGE_ERROR" }
    $record = [ordered]@{
        iteration = $iteration
        window = @{ x = $x; y = $y; width = $width; height = $height }
        actualWindowBounds = $response.result.InitialWindowBounds
        tabOrderVariant = if ($iteration % 2 -eq 0) { "google-decoy" } else { "google-newtab-decoy" }
        passed = [bool]($response.ok -and $semanticStatus -eq "SUCCESS")
        # ABSTAINED and TRANSITION_LIMIT only contain verified prior actions;
        # execution or postcondition failures remain unknown, never zeroed.
        wrongTargetExecutions = if ($response.ok -and $semanticStatus -in $safeStopStatuses) { 0 } else { $null }
        result = $response.result
        error = $response.error
        errorType = $response.errorType
    }
    $results += [pscustomobject]$record
    $record | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 (Join-Path $output "run-$iteration.json")
}

$distinctWindowBounds = @($results | Where-Object actualWindowBounds | ForEach-Object { $_.actualWindowBounds -join "," } | Select-Object -Unique).Count
$requiredWindowVariants = [Math]::Min(5, $Iterations)
$summary = [ordered]@{
    executedAt = [DateTimeOffset]::Now
    iterations = $Iterations
    successes = @($results | Where-Object passed).Count
    wrongTargetExecutions = ($results | Measure-Object -Property wrongTargetExecutions -Sum).Sum
    unknownExecutionSafety = @($results | Where-Object { $null -eq $_.wrongTargetExecutions }).Count
    distinctWindowBounds = $distinctWindowBounds
    passed = (@($results | Where-Object passed).Count -ge [Math]::Ceiling($Iterations * 0.9)) -and (($results | Measure-Object -Property wrongTargetExecutions -Sum).Sum -eq 0) -and (@($results | Where-Object { $null -eq $_.wrongTargetExecutions }).Count -eq 0) -and ($distinctWindowBounds -ge $requiredWindowVariants)
}
$summary | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $output "summary.json")
$summary | ConvertTo-Json
if (-not $summary.passed) { exit 1 }
