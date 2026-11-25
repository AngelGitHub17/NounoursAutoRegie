# NounoursAutoRegie

**Régie finale de télévision pour NounoursCorp** : programmation, habillage, publicité, direct et diffusion multi-chaînes 24 h/24, sans écran noir.
Remplaçant de NounoursTelevisionAutomatic, conçu comme un vrai logiciel d'automation broadcast plutôt que comme un générateur de chaînes IPTV.

> **État : phase 1 — régie web, médiathèque (dont partages Windows) et conducteurs en base (v0.3).**
> Le cahier des charges complet et la feuille de route sont dans le document de conception du projet.

## Ce que fait NounoursAutoRegie aujourd'hui

**La régie (NAR.Server)** — interface web en français, sur PC comme sur téléphone :
- **Antenne** : mosaïque de toutes les chaînes en direct (lecteur, élément en cours, temps restant, à suivre, santé), allumer / éteindre / redémarrer, journal du moteur.
- **Conducteurs** : ajouter, insérer, réordonner, retirer des éléments ; horaire fixe (coupe ce qui passe) ou enchaîné ; points d'entrée et de sortie. Heures prévues calculées en direct, alertes de coupure et de bouche-trou. **Chaque modification part à l'antenne en ~2 s, sans coupure.**
- **Médiathèque** : dossiers locaux ou **partages Windows trouvés automatiquement sur le réseau** (rescan automatique), analyse de chaque média une fois pour toutes (durée, format, volume EBU R128 → correction vers −23 LUFS, vignette), type deviné d'après le nom (jingle, spot, bande-annonce, épisode…).
- **Chaînes** : format (720p, 1080p25/50, 4K), encodeur (processeur ou NVENC / Quick Sync / VA-API / AMF), débit, bouche-trou, boucle.
- **Comptes locaux** (mot de passe haché PBKDF2), création du compte administrateur à la première visite.
- **Flux publics** sans connexion : `/live/<id>/index.m3u8` (HLS de chaque chaîne) et `/live/chaines.m3u` (liste IPTV pour NounoursTV, Jellyfin…).
- **Supervision** : un moteur (processus séparé) par chaîne allumée ; s'il plante, il est relancé seul et seule sa chaîne est touchée.

**Le moteur (NAR.Engine)** — une chaîne :
- **Antenne continue** : une horloge maîtresse produit exactement une image et son audio par période (25, 50, 29,97 i/s…). L'encodeur n'est jamais relancé entre deux éléments : le flux HLS n'a aucune coupure.
- **Enchaînement à l'image près**, élément suivant préchargé, **horaires fixes** à l'image près.
- **Normalisation à la volée** : résolution, bandes noires, cadence, désentrelacement, son 48 kHz stéréo, gain par média.
- **Secours en cascade** : fichier absent ou corrompu sauté sans perte d'image → bouche-trou → mire. Image figée si une source cale, abandonnée après 2 s.
- **Rechargement à chaud** du conducteur ; un changement de format relance proprement le moteur.
- **Reprise après redémarrage** en cours de route, **encodeur auto-réparé**, **journal de diffusion** à l'image près.

## Installation

### Proxmox : conteneur LXC en une commande (recommandé)

Le script suit le format des [Proxmox VE Helper-Scripts](https://community-scripts.github.io/ProxmoxVE/). Dans le **shell de l'hôte Proxmox** :

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/AngelGitHub17/NounoursAutoRegie/main/proxmox/ct/nounoursautoregie.sh)"
```

Le conteneur (Ubuntu 24.04, 4 cœurs, 4 Go, 20 Go par défaut) installe la dernière release (binaires autonomes, sans SDK .NET), génère des médias de test et démarre la régie. Au premier démarrage, elle crée une médiathèque et une chaîne de démonstration « NAR Test » déjà à l'antenne.

| Quoi | Où |
| --- | --- |
| Régie (créer le compte admin à la première visite) | `http://<ip-du-conteneur>:8090` |
| Flux HLS public d'une chaîne | `http://<ip-du-conteneur>:8090/live/<id>/index.m3u8` |
| Liste IPTV | `http://<ip-du-conteneur>:8090/live/chaines.m3u` |
| Données (base, vignettes, HLS, journaux) | `/var/lib/nounoursautoregie/` |
| Journaux | `journalctl -u nounoursautoregie -f` (et bouton « Journal » de chaque chaîne) |
| Mise à jour | relancer la commande ci-dessus **dans la console du conteneur** (ou taper `update`) |

**Vos vrais médias** :
- **Sur un PC Windows** : partagez le dossier (clic droit → Propriétés → Partage → Partage avancé), puis dans la régie, Médiathèque → « Rechercher les PC du réseau », choisissez le PC, entrez le compte Windows et le partage. La régie lit les fichiers par le réseau (SMB 2/3), sans rien monter sur le Proxmox. Le mot de passe est chiffré dans la base.
- **Sur le Proxmox** : montez le dossier dans le conteneur, puis ajoutez son chemin dans la médiathèque.

```bash
pct set <id> -mp0 /chemin/sur/l-hote,mp=/media     # puis redémarrer le conteneur
```

### Windows

Téléchargez `nounoursautoregie-win-x64.zip` dans les [releases](https://github.com/AngelGitHub17/NounoursAutoRegie/releases), dézippez, installez [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) (dans le PATH, ou chemins dans `appsettings.json`), puis lancez `NAR.Server.exe` et ouvrez `http://localhost:8090`. Les données vont dans le dossier `data` à côté du dossier de l'application.

### Docker

```bash
cd docker && docker compose up -d --build     # régie sur :8090, données dans docker/data, médias dans /srv/media
```

### Moteur seul (sans régie)

Le moteur fonctionne aussi tout seul avec un fichier JSON, relu à chaud dès qu'il change :

```bash
scripts/make-test-media.sh
dotnet run -c Release --project src/NAR.Engine -- "$(pwd)/samples/channel.test.json"   # page de contrôle sur :8090
```

## Développement

Prérequis : [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) et **FFmpeg 6+**.

```bash
dotnet build
dotnet test
scripts/make-test-media.sh data/media                              # médias de démonstration
Nar__DataDirectory="$(pwd)/data" dotnet run --project src/NAR.Server  # régie sur http://localhost:8090 (trouve le moteur compilé)
```

Nouvelle version : augmenter `<Version>` dans `Directory.Build.props` et pousser sur `main` ; GitHub Actions publie la release (binaires Linux et Windows) que le conteneur Proxmox récupère.

| Projet | Rôle |
| --- | --- |
| `src/NAR.Core` | Modèle sans dépendance vidéo : cadences exactes, conducteur image par image (`Conductor`), planificateur d'horaires, configuration. |
| `src/NAR.Engine` | Moteur d'une chaîne : horloge maîtresse, décodeurs, encodeur continu, mire, rechargement à chaud, journal de diffusion. |
| `src/NAR.Server` | Régie : base SQLite (EF Core), médiathèque et analyse, superviseur des moteurs, API moteur, flux publics, interface Blazor. |
| `tests/NAR.Core.Tests` | Tests de l'enchaînement, du rechargement à chaud et des cadences. |
| `proxmox/` | Script LXC (format community-scripts), installation et mises à jour (`nar-deploy`). |

### Comment ça s'assemble

```
 Navigateur ──► NAR.Server (régie, :8090) ── base SQLite, médiathèque, analyse FFmpeg
                    │   ▲ config + conducteur (révision, interrogé toutes les 2 s)
        lance et    │   │
        surveille   ▼   │
               NAR.Engine (1 processus par chaîne, 127.0.0.1)
                    fichiers ──► décodeurs FFmpeg ──► horloge maîtresse ──► encodeur unique ──► HLS
                                                                                     │
 NounoursTV, IPTV ◄── /live/<id>/index.m3u8 (relayé par la régie) ◄─────────────────┘
```

## Feuille de route

| Phase | Contenu | Porte de validation |
| --- | --- | --- |
| 0 — Fondations ✅ | Moteur continu, sortie HLS, conteneur Proxmox | 24 h sans coupure, charge processeur mesurée |
| **1 — Une chaîne à l'antenne** *(en cours)* | ✅ Médiathèque et analyse, conducteur en base, NAR.Server, régie web · à venir : EPG XMLTV, journal de diffusion consultable | 7 jours sans intervention |
| 2 — Programmation et multi-chaînes | Grilles types, programmes types, remplissage | NounoursTV bascule d'ErsatzTV |
| 3 — Publicité et habillage | Campagnes, écrans pub, moteur graphique HTML, chartes | |
| 4 — Direct | SRT, RTMP, NDI, prise d'antenne, retour grille | |
| 5 — Écosystème | NounoursAccount, NounoursBot, régie mobile, API publique | |

---
NounoursCorp — projet de DidiKaria.
