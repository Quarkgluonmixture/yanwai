[CmdletBinding()]
param(
    [switch]$Capture
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'WeChatJevHud\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }

Push-Location $repositoryRoot
try {
    $arguments = @('run', '--project', 'src\WeChatJevHud.Diagnostics\WeChatJevHud.Diagnostics.csproj', '--')
    if ($Capture) { $arguments += '--capture' }
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
