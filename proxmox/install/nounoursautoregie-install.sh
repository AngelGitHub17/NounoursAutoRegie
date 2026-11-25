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

msg_info "Installing Dependencies (FFmpeg)"
$STD apt-get install -y ca-certificates curl ffmpeg
msg_ok "Installed Dependencies"

msg_info "Installing NounoursAutoRegie"
curl -fsSL "$NAR_RAW/nar-deploy.sh" -o /usr/local/bin/nar-deploy
chmod +x /usr/local/bin/nar-deploy
useradd --system --home-dir "$NAR_DATA" --shell /usr/sbin/nologin nar 2>/dev/null || true
$STD nar-deploy install
msg_ok "Installed NounoursAutoRegie $(cat "$NAR_HOME/version")"

# Médias de test : au premier démarrage, la régie en fait une médiathèque et une chaîne
# de démonstration « NAR Test » déjà à l'antenne.
msg_info "Creating test media"
mkdir -p "$NAR_DATA/media"
$STD bash "$NAR_HOME/app/scripts/make-test-media.sh" "$NAR_DATA/media"
chown -R nar:nar "$NAR_DATA"
msg_ok "Created test media"

msg_info "Starting NounoursAutoRegie"
systemctl enable -q --now nounoursautoregie
msg_ok "Started NounoursAutoRegie"

motd_ssh
customize
cleanup_lxc
