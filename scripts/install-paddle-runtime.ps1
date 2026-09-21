[CmdletBinding()]
param(
    [ValidateSet('gpu', 'cpu')]
    [string]$Device = 'gpu',

    [string]$RuntimeRoot = (Join-Path $env:LOCALAPPDATA 'WeChatJevHud\paddle-ocr')
)

$ErrorActionPreference = 'Stop'
$uv = (Get-Command uv -ErrorAction Stop).Source
$venv = Join-Path $RuntimeRoot '.venv'
$python = Join-Path $venv 'Scripts\python.exe'

New-Item -ItemType Directory -Force -Path $RuntimeRoot | Out-Null
if (-not (Test-Path $python)) {
    & $uv venv --python 3.10 $venv
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Device -eq 'gpu') {
    # Paddle's current Windows guide publishes the CUDA 12.9 wheel from this index.
    & $uv pip install --python $python 'paddlepaddle-gpu==3.2.2' `
        --index 'https://www.paddlepaddle.org.cn/packages/stable/cu129/'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    & $uv pip install --python $python 'paddlepaddle==3.3.0' `
        --index 'https://www.paddlepaddle.org.cn/packages/stable/cpu/'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $uv pip install --python $python 'paddleocr==3.7.0' 'Pillow>=10,<13'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $python -c 'import paddle, paddleocr; paddle.utils.run_check(); print(paddle.__version__); print(paddleocr.__version__); print(paddle.device.get_device())'
exit $LASTEXITCODE
