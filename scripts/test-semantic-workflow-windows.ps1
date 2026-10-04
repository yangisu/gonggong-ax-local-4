param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw ".NET SDK를 찾지 못했습니다." }

$results = Join-Path $repository "artifacts\semantic-workflow-windows"
New-Item -ItemType Directory -Force -Path $results | Out-Null
$env:SERIES4_WINDOWS_UI_INTEGRATION = "1"
try {
    & $dotnet test (Join-Path $repository "tests\Series4.Desktop.Tests\Series4.Desktop.Tests.csproj") `
        -c $Configuration `
        -p:Platform=x64 `
        --filter "FullyQualifiedName~SemanticWorkflowWindowsIntegrationTests" `
        --logger "trx;LogFileName=semantic-workflow-windows.trx" `
        --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "Windows 사용자 경험 통합 테스트가 실패했습니다." }
}
finally {
    Remove-Item Env:SERIES4_WINDOWS_UI_INTEGRATION -ErrorAction SilentlyContinue
}

Write-Host "Windows 사용자 경험 통합 테스트 통과: $results"
