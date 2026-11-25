#!/usr/bin/env bash

# Copyright (c) 2026 NounoursCorp
# Author: DidiKaria
# License: MIT
# Source: https://github.com/AngelGitHub17/NounoursAutoRegie
#
# Exécuté À L'INTÉRIEUR du conteneur par le moteur des community-scripts.

source /dev/stdin <<<"$FUNCTIONS_FILE_PATH"
color
verb_ip6
catch_errors
setting_up_container
network_check
update_os

NAR_REPO="https://github.com/AngelGitHub17/NounoursAutoRegie.git"
NAR_HOME="/opt/nounoursautoregie"
NAR_DATA="/var/lib/nounoursautoregie"
NAR_CONF="/etc/nounoursautoregie"

msg_info "Installing Dependencies (.NET 8, FFmpeg)"
$STD apt-get install -y git ca-certificates curl ffmpeg dotnet-sdk-8.0
msg_ok "Installed Dependencies"

msg_info "Building NounoursAutoRegie"
mkdir -p "$NAR_HOME"
$STD git clone --depth 1 "$NAR_REPO" "$NAR_HOME/src"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
$STD dotnet publish "$NAR_HOME/src/src/NAR.Engine/NAR.Engine.csproj" -c Release -o "$NAR_HOME/app"
git -C "$NAR_HOME/src" rev-parse HEAD >"$NAR_HOME/version"
msg_ok "Built NounoursAutoRegie ($(cut -c1-7 "$NAR_HOME/version"))"

msg_info "Creating test channel"
useradd --system --home-dir "$NAR_DATA" --shell /usr/sbin/nologin nar 2>/dev/null || true
mkdir -p "$NAR_DATA"/{media,hls,asrun} "$NAR_CONF"
$STD bash "$NAR_HOME/src/scripts/make-test-media.sh" "$NAR_DATA/media"

# Chaîne de test en 1080p25 : c'est elle qui sert à valider la phase 0 (24 h sans coupure).
cat <<EOF >"$NAR_CONF/channel.json"
// Chaîne de test NounoursAutoRegie — modifiez puis : systemctl restart nounoursautoregie
// Exemple complet et commenté : $NAR_HOME/src/samples/channel.example.json
{
  "name": "NAR Test",
  "format": { "width": 1920, "height": 1080, "frameRate": "25" },
  "encoder": { "video": "libx264", "preset": "veryfast", "videoBitrateKbps": 6000, "audioBitrateKbps": 160 },
  "hls": { "directory": "$NAR_DATA/hls", "segmentSeconds": 2, "listSize": 15 },
  "statusPort": 8090,
  "preloadSeconds": 10,
  "filler": "$NAR_DATA/media/bouche-trou.mp4",
  "asRunDirectory": "$NAR_DATA/asrun",
  "playlist": {
    "loop": true,
    "items": [
      { "id": "J1", "title": "Jingle d'ouverture", "path": "$NAR_DATA/media/jingle.mp4" },
      { "id": "P1", "title": "Programme test", "path": "$NAR_DATA/media/programme.mp4" },
      { "id": "S1", "title": "Spot sans son (4:3)", "path": "$NAR_DATA/media/spot-sans-son.mp4" },
      { "id": "B1", "title": "Bande-annonce 50 i/s", "path": "$NAR_DATA/media/bande-annonce.mp4" }
    ]
  }
}
EOF
chown -R nar:nar "$NAR_DATA"
chown -R root:nar "$NAR_CONF"
chmod 0750 "$NAR_CONF"
chmod 0640 "$NAR_CONF/channel.json"
msg_ok "Created test channel"

msg_info "Creating Service"
cat <<EOF >/etc/systemd/system/nounoursautoregie.service
[Unit]
Description=NounoursAutoRegie - moteur de diffusion
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=nar
Group=nar
WorkingDirectory=$NAR_DATA
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_NOLOGO=1
Environment=NAR_CONFIG=$NAR_CONF/channel.json
ExecStart=/usr/bin/dotnet $NAR_HOME/app/NAR.Engine.dll $NAR_CONF/channel.json
Restart=always
RestartSec=3
TimeoutStopSec=20
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF
systemctl enable -q --now nounoursautoregie
msg_ok "Created Service"

motd_ssh
customize
cleanup_lxc
