#!/usr/bin/env bash
# Génère des médias de test variés (résolutions, cadences, avec ou sans son)
# pour vérifier que le moteur enchaîne tout sans coupure.
# Usage : scripts/make-test-media.sh [dossier]   (défaut : samples/media)
set -euo pipefail
OUT="${1:-samples/media}"
mkdir -p "$OUT"
FF="ffmpeg -hide_banner -loglevel error -y"

# Jingle 720p30 avec bip à 1 kHz
$FF -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=4" -f lavfi -i "sine=frequency=1000:duration=4" \
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$OUT/jingle.mp4"

# Programme 1080p25 avec son à 440 Hz
$FF -f lavfi -i "smptehdbars=size=1920x1080:rate=25:duration=8" -f lavfi -i "sine=frequency=440:duration=8" \
    -vf "drawtext=text='PROGRAMME %{pts\:hms}':fontsize=64:fontcolor=white:x=(w-tw)/2:y=h-120:box=1:boxcolor=black" \
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$OUT/programme.mp4" 2>/dev/null \
  || $FF -f lavfi -i "smptehdbars=size=1920x1080:rate=25:duration=8" -f lavfi -i "sine=frequency=440:duration=8" \
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$OUT/programme.mp4"

# Spot pub en 4:3, sans piste audio (le moteur doit mettre du silence et des bandes noires)
$FF -f lavfi -i "mandelbrot=size=640x480:rate=25" -t 5 -c:v libx264 -pix_fmt yuv420p "$OUT/spot-sans-son.mp4"

# Bande-annonce en 50 images/s
$FF -f lavfi -i "testsrc=size=1280x720:rate=50:duration=6" -f lavfi -i "sine=frequency=660:duration=6" \
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$OUT/bande-annonce.mp4"

# Bouche-trou (boucle) bleu avec son grave
$FF -f lavfi -i "color=c=0x1f3a93:size=1280x720:rate=25:duration=3" -f lavfi -i "sine=frequency=220:duration=3" \
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$OUT/bouche-trou.mp4"

echo "Médias de test créés dans $OUT"
ls -la "$OUT"
