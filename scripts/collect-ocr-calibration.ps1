[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExpectedFile,

    [ValidateSet('self', 'remote')]
    [string]$Side,

    [ValidateRange(0, 1000)]
    [int]$Skip = 0,

    [string]$CalibrationDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'WeChatJevHud\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$resolvedExpectedFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExpectedFile)
$arguments = @(
    'run',
    '--project',
    (Join-Path $repositoryRoot 'src\WeChatJevHud.Diagnostics\WeChatJevHud.Diagnostics.csproj'),
    '--',
    '--collect-ocr-calibration',
    $resolvedExpectedFile
)
if ($PSBoundParameters.ContainsKey('Side')) {
    $arguments += @('--calibration-side', $Side)
}
if ($Skip -gt 0) {
    $arguments += @('--calibration-skip', $Skip)
}
if ($PSBoundParameters.ContainsKey('CalibrationDirectory')) {
    $arguments += @('--calibration-dir', $CalibrationDirectory)
}

Push-Location $repositoryRoot
try {
    & $dotnet @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
