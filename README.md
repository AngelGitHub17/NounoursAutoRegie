# NounoursAutoRegie

**Régie finale de télévision pour NounoursCorp** : programmation, habillage, publicité, direct et diffusion multi-chaînes 24 h/24, sans écran noir.
Remplaçant de NounoursTelevisionAutomatic, conçu comme un vrai logiciel d'automation broadcast plutôt que comme un générateur de chaînes IPTV.

> **État : phase 0 — preuve de concept du moteur.** Une chaîne, un conducteur en JSON, une sortie HLS et une page de contrôle.
> Le cahier des charges complet et la feuille de route sont dans le document de conception du projet.

## Ce que fait déjà la phase 0

- **Antenne continue** : une horloge maîtresse produit exactement une image et son audio à chaque période (25, 50, 29,97 i/s…). L'encodeur FFmpeg n'est jamais relancé entre deux éléments, donc le flux HLS n'a aucune coupure ni discontinuité.
- **Enchaînement à l'image près** : l'élément suivant est préchargé (10 s avant par défaut) et démarre à l'image exacte.
- **Horaires fixes** : un élément avec `startAt` coupe ce qui passe à l'heure pile ; avant son heure, l'antenne passe en secours.
- **Normalisation à la volée** : résolution, rapport d'image (bandes noires), cadence, désentrelacement, son 48 kHz stéréo. Les médias hétérogènes s'enchaînent sans préparation.
- **Secours en cascade** : fichier absent ou corrompu → sauté sans perte d'image ; plus rien de prévu → bouche-trou en boucle ; bouche-trou en panne → mire générée en mémoire. Si une source cale, la dernière image est figée, puis la source est abandonnée au bout de 2 s.
- **Reprise après redémarrage** : au lancement, le moteur retrouve l'élément qui devrait être à l'antenne et le reprend en cours de route.
- **Encodeur auto-réparé** : s'il plante, il est relancé et la numérotation HLS continue (les lecteurs ne décrochent pas).
- **Journal de diffusion** (`asrun/<chaîne>-AAAAMMJJ.jsonl`) : chaque début, fin, coupure, passage en secours et erreur, à l'image près.
- **Page de contrôle** sur `http://<machine>:8090` : lecteur, timecode, élément en cours, temps restant, à suivre, santé (retard d'horloge, images figées, sources en échec).
- **Encodage matériel** possible sur Windows : `h264_nvenc`, `h264_qsv`, `h264_amf`, et `hevc_nvenc` pour la 4K.

## Démarrage rapide

Prérequis : [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) et **FFmpeg 6 ou plus récent** (`ffmpeg` et `ffprobe` dans le PATH, ou chemins dans la configuration).

```bash
# 1. Médias de test variés (720p30, 1080p25, 4:3 sans son, 50 i/s, bouche-trou)
scripts/make-test-media.sh                                        # Linux / macOS
powershell -ExecutionPolicy Bypass -File scripts\make-test-media.ps1   # Windows

# 2. Lancer la chaîne de test (640x360, en boucle)
dotnet run -c Release --project src/NAR.Engine -- "$(pwd)/samples/channel.test.json"

# 3. Ouvrir http://localhost:8090
```

Pour une vraie chaîne, copiez `samples/channel.example.json` (1080p25, commenté) et adaptez le conducteur.

### Sur le Proxmox : conteneur LXC en une commande (recommandé)

Le script suit le format des [Proxmox VE Helper-Scripts](https://community-scripts.github.io/ProxmoxVE/) : même menu, mêmes réglages (CPU, RAM, disque, réseau). Dans le **shell de l'hôte Proxmox** :

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/AngelGitHub17/NounoursAutoRegie/main/proxmox/ct/nounoursautoregie.sh)"
```

Le conteneur (Ubuntu 24.04, 4 cœurs, 4 Go, 20 Go par défaut) compile NounoursAutoRegie, génère des médias de test et démarre une **chaîne de test 1080p25** en service `systemd`. Ensuite :

| Quoi | Où |
| --- | --- |
| Page de contrôle | `http://<ip-du-conteneur>:8090` |
| Flux HLS | `http://<ip-du-conteneur>:8090/hls/index.m3u8` |
| Configuration de la chaîne | `/etc/nounoursautoregie/channel.json` puis `systemctl restart nounoursautoregie` |
| Médias, HLS, journal de diffusion | `/var/lib/nounoursautoregie/` |
| Journaux du moteur | `journalctl -u nounoursautoregie -f` |
| Mise à jour | relancer la commande ci-dessus **dans la console du conteneur** (ou taper `update`) |

Pour utiliser vos vrais médias, montez un dossier de l'hôte dans le conteneur (`pct set <id> -mp0 /chemin/hôte,mp=/media`) et pointez le conducteur dessus.

### Sur le Proxmox : Docker

```bash
cd docker
docker compose up -d --build
```

Placez `channel.json` dans `docker/config/nounourstv/`, avec des chemins `/media/...` et `"hls": { "directory": "/data/hls" }`. Une chaîne = un conteneur.

## Organisation du code

| Projet | Rôle |
| --- | --- |
| `src/NAR.Core` | Modèle métier sans dépendance vidéo : cadences exactes (`FrameRate`), format de chaîne, conducteur (`Conductor`), configuration. |
| `src/NAR.Engine` | Moteur d'une chaîne : horloge maîtresse, décodeurs, encodeur continu, mire, journal de diffusion, page de contrôle. |
| `tests/NAR.Core.Tests` | Tests de l'enchaînement image par image et des calculs de cadence (audio exact en 29,97 sur 24 h). |
| `samples/` | Configurations d'exemple et de test. |
| `scripts/` | Génération de médias de test. |
| `docker/` | Image du moteur et `docker-compose` pour le Proxmox. |

### Comment marche le moteur

```
 fichiers ──► SourceDecoder (FFmpeg : décode + normalise)  ─┐
                    vidéo brute YUV 4:2:0 (stdout)           │  préchargement,
                    audio PCM 48 kHz (socket locale)         │  file bornée
                                                             ▼
             Conductor ◄── PlayoutEngine (horloge maîtresse : 1 image / période)
             « quoi diffuser »        │  source, bouche-trou, image figée ou mire
                                      ▼
                         OutputEncoder (un seul FFmpeg, jamais relancé)
                                      ▼
                       HLS (index.m3u8 + segments) ──► NounoursTV, lecteurs
```

Les horodatages de sortie sont calculés à partir du nombre d'images envoyées, pas à partir des fichiers sources : c'est ce qui garantit un flux sans rupture.

## Tests

```bash
dotnet test
```

## Feuille de route

| Phase | Contenu | Porte de validation |
| --- | --- | --- |
| **0 — Fondations** *(en cours)* | Solution, modèle, moteur continu, sortie HLS | 24 h sans coupure, charge processeur mesurée |
| 1 — Une chaîne à l'antenne | Ingest et médiathèque, conducteur en base, NAR.Server, régie web | 7 jours sans intervention |
| 2 — Programmation et multi-chaînes | Grilles types, programmes types, remplissage, superviseur | NounoursTV bascule d'ErsatzTV |
| 3 — Publicité et habillage | Campagnes, écrans pub, moteur graphique HTML, chartes | |
| 4 — Direct | SRT, RTMP, NDI, prise d'antenne, retour grille | |
| 5 — Écosystème | NounoursAccount, NounoursBot, régie mobile, API publique | |

---
NounoursCorp — projet de DidiKaria.
