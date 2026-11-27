// Lecteurs HLS de la régie (hls.js, ou lecture native sur Safari / iOS).
// Pensé pour le direct : on reste près du bord du direct, on rattrape doucement le retard,
// on répare les erreurs sur place, et un chien de garde relance un lecteur figé.
window.narPlayer = {
  attach(video, url) {
    this.detach(video);
    if (!video) return;
    const state = { lastTime: -1, frozenSince: 0, restarts: 0 };
    video._nar = state;

    const restart = (delay) => {
      clearTimeout(state.retry);
      state.retry = setTimeout(() => { if (video._nar === state) build(); }, delay);
    };

    const build = () => {
      if (state.hls) { state.hls.destroy(); state.hls = null; }
      if (video.canPlayType("application/vnd.apple.mpegurl") && !(window.Hls && Hls.isSupported())) {
        video.src = url;
        video.play().catch(() => {});
        return;
      }
      if (!(window.Hls && Hls.isSupported())) return;
      const hls = new Hls({
        lowLatencyMode: false,
        // Qualité adaptative : pas plus que la taille du lecteur à l'écran (aperçu léger,
        // pleine qualité en plein écran), et départ prudent pour démarrer vite.
        capLevelToPlayerSize: true,
        abrEwmaDefaultEstimate: 1000000,
        startLevel: 0,
        liveSyncDurationCount: 3,        // ~3 segments derrière le direct
        liveMaxLatencyDurationCount: 8,  // au-delà : on revient au direct
        maxLiveSyncPlaybackRate: 1.05,   // petit retard : rattrapé en accélérant à peine
        maxBufferLength: 16,
        backBufferLength: 20,
        manifestLoadingMaxRetry: 6,
        levelLoadingMaxRetry: 6,
        fragLoadingMaxRetry: 6,
      });
      state.hls = hls;
      hls.loadSource(url);
      hls.attachMedia(video);
      hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => {}));
      hls.on(Hls.Events.FRAG_BUFFERED, () => { state.restarts = 0; });
      hls.on(Hls.Events.ERROR, (_, d) => {
        if (!d.fatal) {
          // Tampon à sec : on se recale sur le direct plutôt que d'attendre.
          if (d.details === Hls.ErrorDetails.BUFFER_STALLED_ERROR && hls.liveSyncPosition)
            video.currentTime = hls.liveSyncPosition;
          return;
        }
        if (d.type === Hls.ErrorTypes.MEDIA_ERROR && state.restarts < 3) {
          state.restarts++;
          hls.recoverMediaError();
        } else if (d.type === Hls.ErrorTypes.NETWORK_ERROR && state.restarts < 3) {
          state.restarts++;
          setTimeout(() => state.hls === hls && hls.startLoad(), 1500);
        } else {
          state.restarts = 0;
          restart(3000);
        }
      });
    };

    // Chien de garde : image figée 8 s alors que le lecteur devrait jouer → relance complète.
    state.watch = setInterval(() => {
      if (video.paused || video.seeking || document.hidden) { state.frozenSince = 0; return; }
      if (video.currentTime !== state.lastTime) { state.lastTime = video.currentTime; state.frozenSince = 0; return; }
      if (!state.frozenSince) state.frozenSince = Date.now();
      else if (Date.now() - state.frozenSince > 8000) { state.frozenSince = 0; build(); }
    }, 2000);

    build();
  },
  detach(video) {
    if (!video || !video._nar) return;
    const state = video._nar;
    clearTimeout(state.retry);
    clearInterval(state.watch);
    if (state.hls) state.hls.destroy();
    video._nar = null;
  },
};

// Défilement d'un conteneur (grille de la semaine).
window.narScrollTo = (id, y) => { const el = document.getElementById(id); if (el) el.scrollTop = y; };

// Grille de la semaine : hauteur de la souris dans la colonne du jour pendant le glisser-déposer.
window.narPlannerOver = (e, column) => {
  e.preventDefault();
  window._narPlannerY = e.clientY - column.getBoundingClientRect().top;
};
window.narPlannerY = () => window._narPlannerY ?? 0;
