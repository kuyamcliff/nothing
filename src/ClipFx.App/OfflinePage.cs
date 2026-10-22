namespace ClipFx.App;

static class OfflinePage
{
    public static string Html(bool dark) => $$"""
<!doctype html>
<html><head><meta charset="utf-8"><title>ClipFx Manager</title>
<style>
  :root { --bg:{{(dark ? "#18181b" : "#f3f3f4")}}; --surface:{{(dark ? "#212124" : "#ffffff")}}; --border:{{(dark ? "#333338" : "#e2e2e5")}};
          --text:{{(dark ? "#ececef" : "#1d1d20")}}; --muted:{{(dark ? "#a1a1a9" : "#5f5f66")}}; --btn:{{(dark ? "#e4e4e7" : "#2b2b30")}}; --btn-text:{{(dark ? "#18181b" : "#ffffff")}}; }
  html,body { height:100%; margin:0; background:var(--bg); color:var(--text); font:14px/1.4 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif; }
  body { display:grid; place-items:center; user-select:none; }
  .card { background:var(--surface); border:1px solid var(--border); border-radius:10px; padding:28px 32px; min-width:320px; text-align:center; }
  h1 { font-size:16px; font-weight:600; margin:14px 0 18px; }
  .row { display:flex; gap:10px; justify-content:center; align-items:center; color:var(--muted); font-size:13px; }
  .spin { width:14px; height:14px; border:2px solid var(--border); border-top-color:var(--muted); border-radius:50%; animation:s .8s linear infinite; }
  @keyframes s { to { transform:rotate(360deg) } }
  button { margin-top:20px; height:32px; padding:0 14px; border-radius:6px; border:0; background:var(--btn); color:var(--btn-text); font:inherit; font-weight:600; cursor:pointer; }
  svg { color:var(--muted) }
</style></head>
<body><div class="card">
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v4"/><path d="m4.93 4.93 2.83 2.83"/><path d="M2 12h4"/><circle cx="12" cy="12" r="3"/><path d="M12 18v4"/><path d="M18 12h4"/></svg>
  <h1>Service not running</h1>
  <div class="row"><div class="spin"></div><span>Connecting</span></div>
  <button onclick="window.chrome.webview.postMessage({type:'start-service'})">Start service</button>
</div></body></html>
""";
}
