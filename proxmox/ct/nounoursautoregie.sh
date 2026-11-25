#!/usr/bin/env bash
# NounoursAutoRegie — création du conteneur LXC sur Proxmox VE,
# au format des Proxmox VE Helper-Scripts (community-scripts).
#
# À lancer dans le shell de l'hôte Proxmox :
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/AngelGitHub17/NounoursAutoRegie/main/proxmox/ct/nounoursautoregie.sh)"
#
# Mise à jour : relancer la même commande dans la console du conteneur (ou taper « update »).

# Le moteur des community-scripts va chercher install/<app>-install.sh à cette adresse :
# on le fait pointer vers ce dépôt plutôt que vers community-scripts/ProxmoxVE.
export COMMUNITY_SCRIPTS_URL="${COMMUNITY_SCRIPTS_URL:-https://raw.githubusercontent.com/AngelGitHub17/NounoursAutoRegie/main/proxmox}"

_cs_boot="${COMMUNITY_SCRIPTS_CORE_DIR:-$(dirname "${BASH_SOURCE[0]}")/../../core}/core/build.func"
source "$_cs_boot" 2>/dev/null || source <(curl -fsSL "${COMMUNITY_SCRIPTS_CORE_URL:-https://raw.githubusercontent.com/community-scripts/core/main}/core/build.func")
# Copyright (c) 2026 NounoursCorp
# Author: DidiKaria
# License: MIT
# Source: https://github.com/AngelGitHub17/NounoursAutoRegie

APP="NounoursAutoRegie"
var_tags="${var_tags:-media;tv;broadcast}"
var_cpu="${var_cpu:-4}"
var_ram="${var_ram:-4096}"
var_disk="${var_disk:-20}"
var_os="${var_os:-ubuntu}"
var_version="${var_version:-24.04}"
var_unprivileged="${var_unprivileged:-1}"

header_info "$APP"
variables
color
catch_errors

function update_script() {
  header_info
  check_container_storage
  check_container_resources
  if [[ ! -d /opt/nounoursautoregie/src/.git ]]; then
    msg_error "No ${APP} Installation Found!"
    exit
  fi

  cd /opt/nounoursautoregie/src || exit
  $STD git fetch --depth 1 origin main
  CURRENT="$(git rev-parse HEAD)"
  LATEST="$(git rev-parse origin/main)"
  if [[ "$CURRENT" == "$LATEST" ]]; then
    msg_ok "${APP} est déjà à jour (${CURRENT:0:7})"
    exit
  fi

  msg_info "Arrêt de l'antenne"
  systemctl stop nounoursautoregie
  msg_ok "Antenne arrêtée"

  msg_info "Mise à jour de ${APP} (${CURRENT:0:7} → ${LATEST:0:7})"
  $STD git reset --hard origin/main
  $STD dotnet publish src/NAR.Engine/NAR.Engine.csproj -c Release -o /opt/nounoursautoregie/app
  echo "${LATEST}" >/opt/nounoursautoregie/version
  msg_ok "${APP} mis à jour"

  msg_info "Redémarrage de l'antenne"
  systemctl start nounoursautoregie
  msg_ok "Antenne relancée"
  msg_ok "Updated successfully!"
  exit
}

start
build_container
description

msg_ok "Completed successfully!\n"
echo -e "${CREATING}${GN}${APP} setup has been successfully initialized!${CL}"
echo -e "${INFO}${YW}Page de contrôle de l'antenne :${CL}"
echo -e "${GATEWAY}${BGN}http://${IP}:8090${CL}"
echo -e "${INFO}${YW}Configuration de la chaîne : /etc/nounoursautoregie/channel.json${CL}"
