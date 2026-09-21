[CmdletBinding()]
param(
    [string]$Manifest = '.ocr-cache\phase4.5-calibration\manifest.json',

    [string]$Output = '.ocr-cache\phase4.5-production-ocr.md',

    [ValidateSet('cpu', 'gpu:0')]
    [string]$PaddleDevice = 'gpu:0',

    [string]$PaddlePython,

    [switch]$PaddleWorkerDebug
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'WeChatJevHud\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$arguments = @(
    'run', '--project',
    (Join-Path $repositoryRoot 'src\WeChatJevHud.Diagnostics\WeChatJevHud.Diagnostics.csproj'),
    '--', '--production-ocr-evaluate', $Manifest,
    '--ocr-output', $Output,
    '--paddle-device', $PaddleDevice
)
if ($PSBoundParameters.ContainsKey('PaddlePython')) {
    $arguments += @('--paddle-python', $PaddlePython)
}
if ($PaddleWorkerDebug) {
    $arguments += '--paddle-worker-debug'
}

Push-Location $repositoryRoot
try {
    & $dotnet @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
