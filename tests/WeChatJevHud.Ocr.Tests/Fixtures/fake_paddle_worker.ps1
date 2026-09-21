param(
    [ValidateSet('normal', 'malformed', 'timeout', 'crash', 'gpu-fallback', 'stderr', 'wrong-id', 'detector-failure', 'recognizer-failure')]
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
    protocol_version = 2
    detector_model = 'PP-OCRv6_small_det'
    recognizer_model = 'PP-OCRv6_small_rec'
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
    if ($Mode -in @('detector-failure', 'recognizer-failure')) {
        [Console]::Out.WriteLine((@{type='error';request_id=$request.request_id;message=$Mode} | ConvertTo-Json -Compress))
        [Console]::Out.Flush()
        continue
    }
    $response = @{
        type = 'result'
        request_id = if ($Mode -eq 'wrong-id') { 'wrong' } else { $request.request_id }
        raw_text = [string][char]0x597D
        rec_score = 0.999
        inference_ms = 3.5
        detected_line_count = 0
        line_boxes = @()
        lines = @(@{raw_text=[string][char]0x597D;rec_score=0.999})
        detection_ms = 1.0
        recognition_ms = 2.5
        worker_total_ms = 4.0
    } | ConvertTo-Json -Depth 5 -Compress
    [Console]::Out.WriteLine($response)
    [Console]::Out.Flush()
}
