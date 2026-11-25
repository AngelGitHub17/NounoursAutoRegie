#!/usr/bin/env bash
# nar-deploy — installe ou met à jour NounoursAutoRegie depuis la dernière release GitHub.
# Les binaires sont autonomes (runtime .NET inclus) : aucun SDK n'est nécessaire dans le conteneur.
#
#   nar-deploy check     → code 0 si une mise à jour existe, 2 si déjà à jour
#   nar-deploy install   → télécharge et installe la dernière release (+ met à jour le service)
#   nar-deploy service   → (ré)écrit le service systemd de la régie
set -euo pipefail

REPO="AngelGitHub17/NounoursAutoRegie"
NAR_HOME="${NAR_HOME:-/opt/nounoursautoregie}"
NAR_DATA="${NAR_DATA:-/var/lib/nounoursautoregie}"
NAR_PORT="${NAR_PORT:-8090}"
ASSET="nounoursautoregie-linux-x64.tar.gz"

latest_json() {
  curl -fsSL -H "Accept: application/vnd.github+json" "https://api.github.com/repos/${REPO}/releases/latest"
}

# On lit d'abord toute la réponse : un « grep -m1 » directement derrière curl coupe le flux
# et fait échouer curl (code 23) avec pipefail.
latest_tag() {
  local json
  json="$(latest_json)" || return 1
  grep -m1 '"tag_name"' <<<"$json" | cut -d'"' -f4
}

current_tag() { cat "${NAR_HOME}/version" 2>/dev/null || echo "aucune"; }

# Service systemd : depuis la v0.2, c'est la régie (NAR.Server) qui tourne ; elle lance
# et surveille elle-même un moteur par chaîne allumée.
write_service() {
  [[ -d /etc/systemd/system ]] || return 0
  id nar >/dev/null 2>&1 || useradd --system --home-dir "$NAR_DATA" --shell /usr/sbin/nologin nar
  mkdir -p "$NAR_DATA"
  chown -R nar:nar "$NAR_DATA"
  cat <<EOF >/etc/systemd/system/nounoursautoregie.service
[Unit]
Description=NounoursAutoRegie - régie d'antenne
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=nar
Group=nar
WorkingDirectory=$NAR_DATA
Environment=Nar__DataDirectory=$NAR_DATA
Environment=Nar__HttpPort=$NAR_PORT
Environment=TMPDIR=$NAR_DATA
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=$NAR_HOME/app/NAR.Server
Restart=always
RestartSec=3
KillMode=mixed
TimeoutStopSec=20
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload 2>/dev/null || true
}

case "${1:-install}" in
check)
  TAG="$(latest_tag)" || TAG=""
  [[ -n "$TAG" ]] || { echo "Aucune release trouvée sur ${REPO}" >&2; exit 1; }
  echo "Installée : $(current_tag) — disponible : ${TAG}"
  [[ "$TAG" != "$(current_tag)" ]] && exit 0 || exit 2
  ;;
install)
  JSON="$(latest_json)"
  TAG="$(grep -m1 '"tag_name"' <<<"$JSON" | cut -d'"' -f4)"
  URL="$(grep '"browser_download_url"' <<<"$JSON" | grep -F "$ASSET" | head -n1 | cut -d'"' -f4)"
  if [[ -z "$TAG" || -z "$URL" ]]; then
    echo "Release introuvable ou incomplète sur https://github.com/${REPO}/releases" >&2
    exit 1
  fi

  WORK="$(mktemp -d)"
  trap 'rm -rf "$WORK"' EXIT
  curl -fsSL "$URL" -o "$WORK/$ASSET"
  tar -xzf "$WORK/$ASSET" -C "$WORK"
  [[ -x "$WORK/nounoursautoregie-linux-x64/NAR.Engine" ]] || { echo "Archive invalide" >&2; exit 1; }

  mkdir -p "$NAR_HOME"
  rm -rf "${NAR_HOME}/app.old"
  [[ -d "${NAR_HOME}/app" ]] && mv "${NAR_HOME}/app" "${NAR_HOME}/app.old"
  mv "$WORK/nounoursautoregie-linux-x64" "${NAR_HOME}/app"
  chmod +x "${NAR_HOME}/app/NAR.Engine" "${NAR_HOME}"/app/scripts/*.sh
  [[ -x "${NAR_HOME}/app/NAR.Server" ]] && chmod +x "${NAR_HOME}/app/NAR.Server"
  echo "$TAG" >"${NAR_HOME}/version"
  [[ -x "${NAR_HOME}/app/NAR.Server" ]] && write_service
  echo "NounoursAutoRegie ${TAG} installé dans ${NAR_HOME}/app"
  ;;
service)
  write_service
  echo "Service nounoursautoregie écrit"
  ;;
*)
  echo "Usage : nar-deploy [check|install|service]" >&2
  exit 1
  ;;
esac
