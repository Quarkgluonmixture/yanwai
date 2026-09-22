[CmdletBinding()]
param(
    [ValidateRange(50, 5000)]
    [int]$IntervalMilliseconds = 200,

    [ValidateRange(1, 86400)]
    [int]$Seconds,

    [switch]$DebugText
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'Yanwai\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$arguments = @(
    'run',
    '--project',
    (Join-Path $repositoryRoot 'src\Yanwai.Diagnostics\Yanwai.Diagnostics.csproj'),
    '--',
    '--observe',
    '--interval-ms',
    $IntervalMilliseconds
)
if ($PSBoundParameters.ContainsKey('Seconds')) {
    $arguments += @('--observe-seconds', $Seconds)
}
if ($DebugText) {
    $arguments += '--debug-text'
}

& $dotnet @arguments
exit $LASTEXITCODE
