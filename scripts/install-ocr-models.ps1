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

# PP-OCRv6 small recognition (the live OCR). Pinned to one Hugging Face revision and
# checked by hash: a different model would still produce plausible-looking text.
$paddleRevision = 'b8f84f0b80c529de40b4fbb3544b84fa7233a513'
$paddleBase = "https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/$paddleRevision"
$paddleDestination = Join-Path $repositoryRoot '.ocr-cache\paddle\PP-OCRv6_small_rec'
New-Item -ItemType Directory -Path $paddleDestination -Force | Out-Null
$paddleFiles = [ordered]@{
    'inference.onnx' = '5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634'
    'inference.yml'  = 'ab078671bb49f06228eadccd34f1bb501e157f7a047095ffb943ba81512c77d1'
}

foreach ($file in $paddleFiles.Keys) {
    $output = Join-Path $paddleDestination $file
    if (-not (Test-Path $output)) {
        Write-Host "Downloading PP-OCRv6 $file..."
        Invoke-WebRequest -Uri "$paddleBase/$file" -OutFile $output
    }

    $stream = [System.IO.File]::OpenRead($output)
    try { $actual = ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose() }
    if ($actual -ne $paddleFiles[$file]) {
        Remove-Item $output
        throw "PP-OCRv6 $file hash mismatch (got $actual). Deleted; run again."
    }

    Write-Host "OCR model verified: $output"
}
