[CmdletBinding()]
param(
    [switch]$Capture,
    [switch]$CaptureDetect,
    [string]$Detect,
    [string]$Output,
    [string]$OcrEvaluate,
    [string]$Tessdata,
    [string]$OcrOutput
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'WeChatJevHud\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }

Push-Location $repositoryRoot
try {
    $arguments = @('run', '--project', 'src\WeChatJevHud.Diagnostics\WeChatJevHud.Diagnostics.csproj', '--')
    if ($Capture) { $arguments += '--capture' }
    if ($CaptureDetect) { $arguments += '--capture-detect' }
    if ($Detect) { $arguments += @('--detect', $Detect) }
    if ($Output) { $arguments += @('--output', $Output) }
    if ($OcrEvaluate) { $arguments += @('--ocr-evaluate', $OcrEvaluate) }
    if ($Tessdata) { $arguments += @('--tessdata', $Tessdata) }
    if ($OcrOutput) { $arguments += @('--ocr-output', $OcrOutput) }
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
