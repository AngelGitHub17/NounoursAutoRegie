#!/usr/bin/env bash

# Copyright (c) 2026 NounoursCorp
# Author: DidiKaria
# License: MIT
# Source: https://github.com/AngelGitHub17/NounoursAutoRegie
#
# Exécuté À L'INTÉRIEUR du conteneur par le moteur des community-scripts.
# On installe les binaires autonomes de la dernière release GitHub : pas de SDK .NET,
# pas de compilation dans le conteneur (le SDK ne fonctionne pas dans tous les LXC).

source /dev/stdin <<<"$FUNCTIONS_FILE_PATH"
color
verb_ip6
catch_errors
setting_up_container
network_check
update_os

NAR_RAW="https://raw.githubusercontent.com/AngelGitHub17/NounoursAutoRegie/main/proxmox"
NAR_HOME="/opt/nounoursautoregie"
NAR_DATA="/var/lib/nounoursautoregie"
NAR_CONF="/etc/nounoursautoregie"

msg_info "Installing Dependencies (FFmpeg)"
$STD apt-get install -y ca-certificates curl ffmpeg
msg_ok "Installed Dependencies"

msg_info "Installing NounoursAutoRegie"
curl -fsSL "$NAR_RAW/nar-deploy.sh" -o /usr/local/bin/nar-deploy
chmod +x /usr/local/bin/nar-deploy
$STD nar-deploy install
msg_ok "Installed NounoursAutoRegie $(cat "$NAR_HOME/version")"

msg_info "Creating test channel"
useradd --system --home-dir "$NAR_DATA" --shell /usr/sbin/nologin nar 2>/dev/null || true
mkdir -p "$NAR_DATA"/{media,hls,asrun} "$NAR_CONF"
$STD bash "$NAR_HOME/app/scripts/make-test-media.sh" "$NAR_DATA/media"

# Chaîne de test en 1080p25 : c'est elle qui sert à valider la phase 0 (24 h sans coupure).
cat <<EOF >"$NAR_CONF/channel.json"
// Chaîne de test NounoursAutoRegie — modifiez puis : systemctl restart nounoursautoregie
// Exemple complet et commenté : $NAR_HOME/app/samples/channel.example.json
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
Environment=TMPDIR=$NAR_DATA
Environment=NAR_CONFIG=$NAR_CONF/channel.json
ExecStart=$NAR_HOME/app/NAR.Engine $NAR_CONF/channel.json
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
