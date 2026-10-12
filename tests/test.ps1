param([string]$Executable = (Join-Path $PSScriptRoot '../dist/SvgViewer/SvgViewer.exe'))
$ErrorActionPreference = 'Stop'
$testDirectory = Join-Path $PSScriptRoot '../artifacts/published-test'
$null = New-Item -ItemType Directory -Force $testDirectory
$testDirectory = (Resolve-Path $testDirectory).Path
$resultFile = Join-Path $testDirectory 'result.json'
if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile }
$testProcess = Start-Process -FilePath (Resolve-Path $Executable).Path -ArgumentList '--self-test', ('"' + $testDirectory + '"') -WindowStyle Hidden -PassThru
if (!$testProcess.WaitForExit(45000)) {
    Stop-Process -Id $testProcess.Id -ErrorAction SilentlyContinue
    throw 'Test timed out. See artifacts/published-test/startup.log.'
}
if (!(Test-Path -LiteralPath $resultFile)) { throw 'No test result was produced.' }
$testResult = Get-Content -LiteralPath $resultFile -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$testResult.passed) { throw $testResult.error }
Write-Host ($testResult.count.ToString() + ' checks passed.')
