#!/usr/bin/env bash
# nar-deploy — installe ou met à jour NounoursAutoRegie depuis la dernière release GitHub.
# Les binaires sont autonomes (runtime .NET inclus) : aucun SDK n'est nécessaire dans le conteneur.
#
#   nar-deploy check     → code 0 si une mise à jour existe, 2 si déjà à jour
#   nar-deploy install   → télécharge et installe la dernière release
set -euo pipefail

REPO="AngelGitHub17/NounoursAutoRegie"
NAR_HOME="${NAR_HOME:-/opt/nounoursautoregie}"
ASSET="nounoursautoregie-linux-x64.tar.gz"

latest_json() {
  curl -fsSL -H "Accept: application/vnd.github+json" "https://api.github.com/repos/${REPO}/releases/latest"
}

latest_tag() { latest_json | grep -m1 '"tag_name"' | cut -d'"' -f4; }

current_tag() { cat "${NAR_HOME}/version" 2>/dev/null || echo "aucune"; }

case "${1:-install}" in
check)
  TAG="$(latest_tag)"
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
  echo "$TAG" >"${NAR_HOME}/version"
  echo "NounoursAutoRegie ${TAG} installé dans ${NAR_HOME}/app"
  ;;
*)
  echo "Usage : nar-deploy [check|install]" >&2
  exit 1
  ;;
esac
