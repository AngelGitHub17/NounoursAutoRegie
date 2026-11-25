# Génère des médias de test variés pour NounoursAutoRegie (version Windows).
# Usage : powershell -ExecutionPolicy Bypass -File scripts\make-test-media.ps1 [-Out samples\media]
param([string]$Out = "samples/media")

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$ff = @("-hide_banner", "-loglevel", "error", "-y")

function Run-Ffmpeg([string[]]$Arguments) {
    & ffmpeg @ff @Arguments
    if ($LASTEXITCODE -ne 0) { throw "FFmpeg a échoué : $($Arguments -join ' ')" }
}

Run-Ffmpeg @("-f","lavfi","-i","testsrc2=size=1280x720:rate=30:duration=4","-f","lavfi","-i","sine=frequency=1000:duration=4",
             "-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac","-shortest","$Out/jingle.mp4")
Run-Ffmpeg @("-f","lavfi","-i","smptehdbars=size=1920x1080:rate=25:duration=8","-f","lavfi","-i","sine=frequency=440:duration=8",
             "-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac","-shortest","$Out/programme.mp4")
Run-Ffmpeg @("-f","lavfi","-i","mandelbrot=size=640x480:rate=25","-t","5","-c:v","libx264","-pix_fmt","yuv420p","$Out/spot-sans-son.mp4")
Run-Ffmpeg @("-f","lavfi","-i","testsrc=size=1280x720:rate=50:duration=6","-f","lavfi","-i","sine=frequency=660:duration=6",
             "-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac","-shortest","$Out/bande-annonce.mp4")
Run-Ffmpeg @("-f","lavfi","-i","color=c=0x1f3a93:size=1280x720:rate=25:duration=3","-f","lavfi","-i","sine=frequency=220:duration=3",
             "-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac","-shortest","$Out/bouche-trou.mp4")

Write-Host "Médias de test créés dans $Out"
Get-ChildItem $Out
