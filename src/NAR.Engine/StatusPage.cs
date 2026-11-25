using System.Net;

namespace NAR.Engine;

/// <summary>Mini-régie de contrôle de la phase 0 : lecteur HLS + état de l'antenne.</summary>
internal static class StatusPage
{
    public static string Html(string channelName) => Template.Replace("{{NAME}}", WebUtility.HtmlEncode(channelName));

    private const string Template = """
<!DOCTYPE html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{NAME}} — NounoursAutoRegie</title>
<script src="https://cdn.jsdelivr.net/npm/hls.js@1"></script>
<style>
  :root { color-scheme: dark; --bg:#0d0f14; --panel:#161a22; --line:#262c38; --text:#e8ebf1; --muted:#8a93a6; --live:#e5484d; --ok:#3fb950; --warn:#d29922; }
  * { box-sizing: border-box; }
  body { margin:0; font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background:var(--bg); color:var(--text); }
  header { display:flex; align-items:center; gap:12px; padding:14px 20px; border-bottom:1px solid var(--line); }
  header h1 { font-size:17px; margin:0; font-weight:600; }
  .onair { background:var(--live); color:#fff; font-size:12px; font-weight:700; padding:3px 8px; border-radius:4px; letter-spacing:.06em; }
  main { display:grid; grid-template-columns: minmax(0,2fr) minmax(260px,1fr); gap:16px; padding:16px 20px; }
  @media (max-width: 800px) { main { grid-template-columns: 1fr; } }
  video { width:100%; aspect-ratio:16/9; background:#000; border-radius:8px; }
  .panel { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:14px 16px; }
  .tc { font-family: ui-monospace, Consolas, monospace; font-size:30px; letter-spacing:.04em; }
  .label { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.06em; margin-top:14px; }
  .value { font-size:15px; margin-top:3px; }
  .state { display:inline-block; padding:2px 8px; border-radius:4px; font-size:13px; font-weight:600; }
  .Playing { background:rgba(63,185,80,.15); color:var(--ok); }
  .Filler, .Hold, .Starting { background:rgba(210,153,34,.15); color:var(--warn); }
  .Slate { background:rgba(229,72,77,.15); color:var(--live); }
  dl { display:grid; grid-template-columns:auto 1fr; gap:4px 12px; margin:6px 0 0; font-size:13px; }
  dt { color:var(--muted); } dd { margin:0; font-family: ui-monospace, Consolas, monospace; }
  .incident { color:var(--warn); font-size:13px; margin-top:6px; }
</style>
</head>
<body>
<header><span class="onair">ANTENNE</span><h1>{{NAME}}</h1><span id="format" style="color:var(--muted);font-size:13px"></span></header>
<main>
  <div>
    <video id="player" controls autoplay muted playsinline></video>
    <p style="color:var(--muted);font-size:13px">Flux HLS : <code id="url"></code> — le son est coupé par défaut (règle des navigateurs).</p>
  </div>
  <div class="panel">
    <div class="tc" id="tc">--:--:--:--</div>
    <div><span class="state" id="state">…</span></div>
    <div class="label">À l'antenne</div><div class="value" id="current">—</div>
    <div class="label">Reste</div><div class="value" id="remaining">—</div>
    <div class="label">À suivre</div><div class="value" id="next">—</div>
    <div class="label">Santé</div>
    <dl>
      <dt>Retard horloge</dt><dd id="late">—</dd>
      <dt>File encodeur</dt><dd id="queue">—</dd>
      <dt>Images figées</dt><dd id="underruns">—</dd>
      <dt>Sources en échec</dt><dd id="failures">—</dd>
      <dt>Redémarrages encodeur</dt><dd id="restarts">—</dd>
    </dl>
    <div class="incident" id="incident"></div>
  </div>
</main>
<script>
  const STATES = { Starting:"Démarrage", Playing:"Programme", Filler:"Bouche-trou", Slate:"Mire", Hold:"Image figée" };
  const src = location.origin + "/hls/index.m3u8";
  document.getElementById("url").textContent = src;
  const video = document.getElementById("player");
  function attach() {
    if (video.canPlayType("application/vnd.apple.mpegurl")) { video.src = src; return; }
    if (window.Hls && Hls.isSupported()) {
      const hls = new Hls({ liveSyncDurationCount: 3 });
      hls.loadSource(src); hls.attachMedia(video);
      hls.on(Hls.Events.ERROR, (_, d) => { if (d.fatal) { hls.destroy(); setTimeout(attach, 3000); } });
    }
  }
  setTimeout(attach, 1500);
  const fmt = s => { s = Math.max(0, Math.round(s)); return Math.floor(s/60) + " min " + String(s%60).padStart(2,"0") + " s"; };
  async function refresh() {
    try {
      const s = await (await fetch("/status", { cache: "no-store" })).json();
      document.getElementById("tc").textContent = s.timecode;
      document.getElementById("format").textContent = s.format;
      const st = document.getElementById("state"); st.textContent = STATES[s.state] || s.state; st.className = "state " + s.state;
      document.getElementById("current").textContent = s.currentTitle || "—";
      document.getElementById("remaining").textContent = s.remainingSeconds == null ? "—" : fmt(s.remainingSeconds);
      document.getElementById("next").textContent = s.nextTitle || "—";
      document.getElementById("late").textContent = s.lateMs.toFixed(0) + " ms";
      document.getElementById("queue").textContent = s.encoderQueue + " img";
      document.getElementById("underruns").textContent = s.underrunFrames;
      document.getElementById("failures").textContent = s.sourceFailures;
      document.getElementById("restarts").textContent = s.encoderRestarts;
      document.getElementById("incident").textContent = s.lastIncident ? "Dernier incident : " + s.lastIncident : "";
    } catch { }
  }
  refresh(); setInterval(refresh, 500);
</script>
</body>
</html>
""";
}
