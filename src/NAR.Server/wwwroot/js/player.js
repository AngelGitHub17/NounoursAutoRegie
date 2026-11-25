// Lecteurs HLS de la régie (hls.js, ou lecture native sur Safari / iOS).
window.narPlayer = {
  attach(video, url) {
    this.detach(video);
    if (!video) return;
    const start = () => {
      if (video.canPlayType("application/vnd.apple.mpegurl")) {
        video.src = url;
        video.play().catch(() => {});
        return;
      }
      if (!(window.Hls && Hls.isSupported())) return;
      const hls = new Hls({ liveSyncDurationCount: 3, maxBufferLength: 10 });
      hls.loadSource(url);
      hls.attachMedia(video);
      hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => {}));
      hls.on(Hls.Events.ERROR, (_, d) => {
        if (!d.fatal) return;
        hls.destroy();
        video._narHls = null;
        video._narRetry = setTimeout(start, 3000);
      });
      video._narHls = hls;
    };
    start();
  },
  detach(video) {
    if (!video) return;
    clearTimeout(video._narRetry);
    if (video._narHls) { video._narHls.destroy(); video._narHls = null; }
  },
};
