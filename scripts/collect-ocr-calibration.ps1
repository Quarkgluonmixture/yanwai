[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExpectedFile,

    [ValidateSet('self', 'remote')]
    [string]$Side,

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
