param(
    [ValidateSet('normal', 'malformed', 'timeout', 'crash', 'gpu-fallback', 'stderr')]
    [string]$Mode = 'normal'
)

[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

if ($Mode -eq 'stderr') {
    [Console]::Error.WriteLine('private bubble text: secret-message')
    [Console]::Error.Flush()
}

$ready = @{
    type = 'ready'
    protocol_version = 1
    model_name = 'PP-OCRv6_small_rec'
    paddleocr_version = 'test'
    paddlepaddle_version = 'test'
    device_requested = if ($Mode -eq 'gpu-fallback') { 'gpu:0' } else { 'cpu' }
    device_active = 'cpu'
    startup_ms = 12.5
    warmup_ms = 4.5
} | ConvertTo-Json -Compress
[Console]::Out.WriteLine($ready)
[Console]::Out.Flush()

while (($line = [Console]::In.ReadLine()) -ne $null) {
    $request = $line | ConvertFrom-Json
    if ($request.type -eq 'shutdown') { exit 0 }
    if ($Mode -eq 'malformed') {
        [Console]::Out.WriteLine('not-json')
        [Console]::Out.Flush()
        continue
    }
    if ($Mode -eq 'timeout') {
        Start-Sleep -Seconds 10
        continue
    }
    if ($Mode -eq 'crash') { exit 7 }
    $response = @{
        type = 'result'
        request_id = $request.request_id
        raw_text = [string][char]0x597D
        rec_score = 0.999
        inference_ms = 3.5
    } | ConvertTo-Json -Compress
    [Console]::Out.WriteLine($response)
    [Console]::Out.Flush()
}
