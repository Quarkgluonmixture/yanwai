[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Manifest,

    [string]$Output = '.ocr-cache\phase4.5-input-audit\report.md',

    [string]$ArtifactDirectory = '.ocr-cache\phase4.5-input-audit\artifacts',

    [string]$PaddlePython,

    [string]$Device = 'gpu:0',

    [ValidateRange(0, 20)]
    [int]$Padding = 4,

    [ValidateRange(1, 200)]
    [int[]]$TargetTextHeight = @(32, 40, 48),

    [ValidateRange(1, 200)]
    [int]$ArtifactTargetHeight = 40,

    [ValidateRange(0, 10)]
    [int]$WarmupCount = 1
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$defaultPython = Join-Path $env:LOCALAPPDATA 'WeChatJevHud\paddle-ocr\.venv\Scripts\python.exe'
$python = if ($PSBoundParameters.ContainsKey('PaddlePython')) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PaddlePython)
}
elseif (Test-Path $defaultPython) {
    $defaultPython
}
else {
    throw "Paddle Python was not found at '$defaultPython'. Pass -PaddlePython explicitly."
}

$resolvedManifests = @(
    $Manifest | ForEach-Object {
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($_)
    }
)
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$resolvedArtifacts = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArtifactDirectory)
$arguments = @(
    (Join-Path $repositoryRoot 'scripts\ocr_input_audit.py'),
    '--manifest'
) + $resolvedManifests + @(
    '--output',
    $resolvedOutput,
    '--artifact-dir',
    $resolvedArtifacts,
    '--device',
    $Device,
    '--padding',
    $Padding,
    '--target-text-heights'
) + $TargetTextHeight + @(
    '--artifact-target-height',
    $ArtifactTargetHeight,
    '--warmup-count',
    $WarmupCount
)

Push-Location $repositoryRoot
try {
    & $python @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
