[CmdletBinding()]
param(
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repositoryRoot '.ocr-cache\tessdata'
}

$modelRevision = '87416418657359cb625c412a48b6e1d6d41c29bd'
$baseUrl = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/$modelRevision"
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

foreach ($language in @('chi_sim', 'eng')) {
    $output = Join-Path $Destination "$language.traineddata"
    if (Test-Path $output) {
        Write-Host "OCR model already present: $output"
        continue
    }

    Write-Host "Downloading $language OCR model..."
    Invoke-WebRequest -Uri "$baseUrl/$language.traineddata" -OutFile $output
    Write-Host "Saved: $output"
}
