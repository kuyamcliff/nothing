(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const host = window.chrome && window.chrome.webview;
  const store = {
    get(k, d) { try { return localStorage.getItem('cft.' + k) ?? d; } catch { return d; } },
    set(k, v) { try { localStorage.setItem('cft.' + k, v); } catch { } },
  };
  const state = {
    snap: null, connected: false, everConnected: false,
    filter: store.get('filter', 'all'), search: '', sort: store.get('sort', 'order'),
    selected: null, detail: null, tab: store.get('tab', 'files'),
    theme: store.get('theme', 'system'), themeTouched: false,
    hist: [], peak: 0, rows: new Map(), lastDetailAt: 0, detailBusy: false,
  };

  // ------------------------------------------------------------ utils
  const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];
  function bytes(n) {
    if (!(n > 0)) return '0 B';
    let i = 0; while (n >= 1024 && i < UNITS.length - 1) { n /= 1024; i++; }
    return n.toFixed(i === 0 ? 0 : n < 10 ? 2 : n < 100 ? 1 : 0) + ' ' + UNITS[i];
  }
  const rate = (n) => (n > 0 ? bytes(n) : '0 B') + '/s';
  function eta(s) {
    if (s == null || !isFinite(s)) return '';
    s = Math.round(s);
    if (s < 60) return s + 's';
    if (s < 3600) return Math.floor(s / 60) + 'm ' + String(s % 60).padStart(2, '0') + 's';
    if (s < 86400) return Math.floor(s / 3600) + 'h ' + String(Math.floor((s % 3600) / 60)).padStart(2, '0') + 'm';
    const d = Math.floor(s / 86400); return d > 99 ? '∞' : d + 'd ' + Math.floor((s % 86400) / 3600) + 'h';
  }
  const pct = (p) => (p >= 1 ? '100' : p >= .999 ? '99.9' : (p * 100).toFixed(p < .1 ? 1 : 0)) + '%';
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const icon = (n) => `<svg class="i"><use href="#i-${n}"/></svg>`;
  const setText = (el, v) => { if (el.textContent !== v) el.textContent = v; };
  const date = (d) => d ? new Date(d).toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '';
  const KIND_ICON = { video: 'video', music: 'music', image: 'image', archive: 'archive', disc: 'disc', app: 'app', doc: 'doc', folder: 'folder', file: 'file' };
  const EXT = {
    video: 'mp4 mkv mov avi wmv flv webm m4v ts m2ts mpg mpeg vob', music: 'mp3 m4a aac flac wav ogg opus wma ape', image: 'jpg jpeg png gif webp bmp tif tiff heic psd',
    archive: 'zip rar 7z tar gz tgz bz2 xz zst cab', disc: 'iso img dmg bin cue nrg vhd vhdx vmdk', app: 'exe msi apk appx msix deb rpm pkg', doc: 'pdf epub mobi azw3 cbz cbr doc docx txt nfo srt sub ass',
  };
  function fileKind(name) {
    const ext = (name.split('.').pop() || '').toLowerCase();
    for (const [k, v] of Object.entries(EXT)) if (v.split(' ').includes(ext)) return k;
    return 'file';
  }

  async function api(method, path, body, signal) {
    const r = await fetch('/api' + path, {
      method, signal,
      headers: Object.assign({ 'X-ClipFx': '1' }, body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (!r.ok) { let m = ''; try { m = (await r.json()).error || ''; } catch { } throw new Error(m || 'Something went wrong.'); }
    const t = await r.text(); return t ? JSON.parse(t) : null;
  }
  function toast(msg, kind) {
    const el = document.createElement('div');
    el.className = 'toast' + (kind === 'error' ? ' error' : '');
    el.innerHTML = icon(kind === 'error' ? 'alert' : 'check') + '<span></span>';
    el.lastChild.textContent = msg;
    $('toasts').appendChild(el);
    setTimeout(() => el.remove(), kind === 'error' ? 4500 : 2400);
  }
  const fail = (e) => toast(e.message || String(e), 'error');

  // ------------------------------------------------------------ theme
  function applyTheme(t) {
    state.theme = t;
    if (t === 'system') document.documentElement.removeAttribute('data-theme'); else document.documentElement.setAttribute('data-theme', t);
    document.querySelectorAll('#themeSeg button').forEach((b) => b.setAttribute('aria-checked', String(b.dataset.themeValue === t)));
    store.set('theme', t);
    const dark = t === 'dark' || (t === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
    if (host) host.postMessage({ type: 'theme', dark });
    drawGraph();
  }
  $('themeSeg').addEventListener('click', (e) => {
    const b = e.target.closest('button'); if (!b) return;
    state.themeTouched = true;
    applyTheme(b.dataset.themeValue);
    api('POST', '/settings', { theme: b.dataset.themeValue }).catch(() => { });
  });
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => applyTheme(state.theme));

  // ------------------------------------------------------------ live state
  let es = null, pollTimer = null;
  function connect() {
    if (es) es.close();
    es = new EventSource('/api/stream');
    es.onmessage = (e) => { state.connected = true; state.everConnected = true; onSnap(JSON.parse(e.data)); };
    es.onerror = () => { state.connected = false; renderBanners(); if (!pollTimer) pollTimer = setTimeout(poll, 1500); };
  }
  async function poll() {
    pollTimer = null;
    try { const s = await api('GET', '/state'); state.connected = true; onSnap(s); } catch { state.connected = false; renderBanners(); }
  }

  function onSnap(s) {
    const first = !state.snap;
    state.snap = s;
    if (first) {
      $('app').removeAttribute('aria-busy');
      if (!state.themeTouched && s.settings.theme && s.settings.theme !== state.theme) applyTheme(s.settings.theme);
    }
    const g = s.global;
    state.hist.push([g.down, g.up]);
    if (state.hist.length > 120) state.hist.shift();
    state.peak = Math.max(state.peak, g.down);
    render();
    if (state.selected && Date.now() - state.lastDetailAt > 1400) loadDetail();
  }

  // ------------------------------------------------------------ rendering
  const STATUS = {
    metadata: ['Getting info', 'muted'], checking: ['Checking', 'warn'], queued: ['Queued', 'muted'], downloading: ['Downloading', ''],
    stalled: ['Waiting for peers', 'warn'], seeding: ['Seeding', 'ok'], finished: ['Done', 'ok'], paused: ['Paused', 'muted'], error: ['Error', 'bad'], moving: ['Moving', 'warn'],
  };
  const isActive = (t) => ['downloading', 'stalled', 'metadata', 'checking', 'queued', 'moving'].includes(t.status);
  const FILTERS = {
    all: () => true,
    downloading: (t) => isActive(t),
    seeding: (t) => t.status === 'seeding',
    done: (t) => t.progress >= 1,
    paused: (t) => t.status === 'paused',
    error: (t) => t.status === 'error',
  };

  function visible() {
    const s = state.snap; if (!s) return [];
    const q = state.search.trim().toLowerCase();
    let list = s.torrents.filter(FILTERS[state.filter] || FILTERS.all);
    if (q) list = list.filter((t) => t.name.toLowerCase().includes(q) || t.id.includes(q));
    const by = {
      order: (a, b) => a.order - b.order,
      added: (a, b) => new Date(b.addedAt) - new Date(a.addedAt),
      name: (a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }),
      size: (a, b) => b.wanted - a.wanted,
      progress: (a, b) => b.progress - a.progress,
      down: (a, b) => b.down - a.down,
      eta: (a, b) => (a.eta ?? 1e15) - (b.eta ?? 1e15),
    }[state.sort] || ((a, b) => a.order - b.order);
    return list.slice().sort(by);
  }

  function render() {
    const s = state.snap; if (!s) return;
    renderBanners();
    // filters
    for (const b of document.querySelectorAll('#filters button')) {
      const n = s.torrents.filter(FILTERS[b.dataset.filter]).length;
      setText(b.querySelector('b'), n ? String(n) : '');
      b.setAttribute('aria-current', String(b.dataset.filter === state.filter));
      if (b.dataset.filter === 'error') b.classList.toggle('has', n > 0);
    }
    // global
    const g = s.global;
    setText($('gDown'), rate(g.down));
    setText($('gUp'), rate(g.up));
    setText($('gPeak'), rate(state.peak));
    const net = $('fNet'); setText(net, g.online ? 'Online' : 'Offline'); net.className = g.online ? 'ok' : 'bad';
    const port = $('fPort'); setText(port, g.port + ' · ' + g.portStatus); port.className = g.portStatus === 'Open' || g.portStatus === 'Forwarded' ? 'ok' : g.portStatus === 'Closed' ? 'warn' : '';
    setText($('fDht'), s.settings.dht ? (g.dhtNodes ? g.dhtNodes + ' nodes' : 'Starting') : 'Off');
    setText($('fConn'), String(g.connections));
    const free = $('fFree'); setText(free, g.freeSpace >= 0 ? bytes(g.freeSpace) : '—'); free.className = g.freeSpace >= 0 && g.freeSpace < 2 * 1024 ** 3 ? 'bad' : '';
    if (document.activeElement !== $('whenDone')) $('whenDone').value = s.whenDone;
    $('whenDone').parentElement.classList.toggle('armed', s.whenDone !== 'nothing');
    document.title = g.down > 0 ? `↓ ${rate(g.down)} · ClipFx Torrent` : 'ClipFx Torrent';
    drawGraph();
    renderRows();
    renderSettingsIfOpen();
  }

  function renderBanners() {
    const el = $('banners');
    let html = '';
    if (state.everConnected && !state.connected) html = `<div class="banner banner-danger">${icon('alert')}<span class="msg">Reconnecting to the engine…</span></div>`;
    else if (state.snap && !state.snap.global.online) html = `<div class="banner banner-warn">${icon('alert')}<span class="msg">No network. Downloads continue as soon as you're back online.</span></div>`;
    if (el.dataset.html !== html) { el.innerHTML = html; el.dataset.html = html; }
  }

  function renderRows() {
    const list = visible();
    const rows = $('rows');
    const s = state.snap;
    const none = s.torrents.length === 0;
    $('empty').hidden = list.length > 0;
    rows.hidden = list.length === 0;
    document.querySelector('.cols').hidden = list.length === 0;
    setText($('emptyTitle'), none ? 'Drop .torrent files or paste a magnet link' : 'Nothing here');
    $('emptyActions').hidden = !none;

    const seen = new Set();
    let prev = null;
    for (const t of list) {
      seen.add(t.id);
      let el = state.rows.get(t.id);
      if (!el) { el = makeRow(t.id); state.rows.set(t.id, el); }
      updateRow(el, t);
      const want = prev ? prev.nextSibling : rows.firstChild;
      if (want !== el) rows.insertBefore(el, want);
      prev = el;
    }
    for (const [id, el] of state.rows) if (!seen.has(id)) { el.remove(); if (!s.torrents.some((t) => t.id === id)) state.rows.delete(id); }
    if (state.selected && !s.torrents.some((t) => t.id === state.selected)) select(null);
  }

  function makeRow(id) {
    const el = document.createElement('div');
    el.className = 'row';
    el.setAttribute('role', 'option');
    el.dataset.id = id;
    el.innerHTML = `
      <div class="r-name"><div class="kind-icon"></div><div class="r-text"><div class="r-head"><div class="r-title"></div><span class="r-flags"></span></div><div class="r-sub"></div></div></div>
      <div class="r-num r-size"></div>
      <div class="r-prog"><div class="bar"><i></i></div><span class="pct"></span></div>
      <div class="r-num r-down"></div><div class="r-num r-up"></div><div class="r-num r-eta"></div><div class="r-num r-peers"></div>
      <div class="r-act">
        <button type="button" class="btn btn-icon" data-act="toggle"></button>
        <button type="button" class="btn btn-icon" data-act="menu" title="More" aria-label="More">${icon('more')}</button>
      </div>`;
    return el;
  }

  function updateRow(el, t) {
    const sig = JSON.stringify([t.status, t.progress, t.down, t.up, t.eta, t.peers, t.seeds, t.name, t.kind, t.error, t.wanted, t.done, t.sequential, t.forced, t.ratio, state.selected === t.id, t.peersTotal, t.seedsTotal]);
    if (el.dataset.sig === sig) return;
    el.dataset.sig = sig;
    el.setAttribute('aria-selected', String(state.selected === t.id));
    const [label, cls] = STATUS[t.status] || [t.status, ''];
    const ki = el.querySelector('.kind-icon');
    const kind = t.hasMetadata ? (KIND_ICON[t.kind] || 'file') : 'magnet';
    if (ki.dataset.k !== kind) { ki.dataset.k = kind; ki.innerHTML = icon(kind); }
    setText(el.querySelector('.r-title'), t.name);
    el.querySelector('.r-title').title = t.name;

    let sub = `<span class="st ${cls}">${esc(t.status === 'error' && t.error ? t.error : label)}</span>`;
    if (t.status !== 'error') {
      if (t.progress < 1 && t.hasMetadata) sub += `<span>${bytes(t.done)} of ${bytes(t.wanted)}</span>`;
      else if (t.progress >= 1) sub += `<span>Ratio ${t.ratio.toFixed(2)}</span>`;
    }
    let flags = '';
    if (t.forced) flags += '<span class="flag">FORCED</span>';
    if (t.sequential) flags += '<span class="flag">IN ORDER</span>';
    if (t.private) flags += '<span class="flag">PRIVATE</span>';
    const fl = el.querySelector('.r-flags');
    if (fl.innerHTML !== flags) fl.innerHTML = flags;
    fl.style.display = flags ? 'contents' : 'none';
    const subEl = el.querySelector('.r-sub');
    if (subEl.innerHTML !== sub) subEl.innerHTML = sub;
    subEl.title = t.error || '';

    setText(el.querySelector('.r-size'), t.wanted > 0 ? bytes(t.wanted) : '—');
    const bar = el.querySelector('.bar'), fill = bar.firstChild;
    bar.classList.toggle('indet', t.status === 'metadata' || t.status === 'moving');
    fill.style.width = (t.progress * 100).toFixed(2) + '%';
    fill.className = t.progress >= 1 ? 'done' : t.status === 'error' ? 'bad' : t.status === 'paused' || t.status === 'queued' ? 'paused' : '';
    setText(el.querySelector('.pct'), t.hasMetadata ? pct(t.progress) : '');
    const d = el.querySelector('.r-down'); setText(d, t.down > 0 ? rate(t.down) : '');
    const u = el.querySelector('.r-up'); setText(u, t.up > 0 ? rate(t.up) : '');
    setText(el.querySelector('.r-eta'), t.progress < 1 ? eta(t.eta) : '');
    const running = !['paused', 'finished', 'queued', 'error'].includes(t.status);
    setText(el.querySelector('.r-peers'), running || t.peers ? `${t.peers}${t.peersTotal + t.seedsTotal > t.peers ? ' / ' + (t.peersTotal + t.seedsTotal) : ''}` : '');
    const tg = el.querySelector('[data-act="toggle"]');
    const paused = t.status === 'paused' || t.status === 'error' || t.status === 'finished';
    const ic = paused ? 'play' : 'pause';
    if (tg.dataset.ic !== ic) { tg.dataset.ic = ic; tg.innerHTML = icon(ic); tg.title = paused ? 'Resume' : 'Pause'; tg.setAttribute('aria-label', tg.title); }
  }

  // ------------------------------------------------------------ speed graph
  function drawGraph() {
    const c = $('graph'); if (!c) return;
    const dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth, h = c.clientHeight;
    if (!w) return;
    if (c.width !== Math.round(w * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
    const x = c.getContext('2d');
    x.setTransform(dpr, 0, 0, dpr, 0, 0);
    x.clearRect(0, 0, w, h);
    const cs = getComputedStyle(document.documentElement);
    const cDown = cs.getPropertyValue('--spark-down').trim(), cUp = cs.getPropertyValue('--spark-up').trim(), grid = cs.getPropertyValue('--border').trim();
    x.strokeStyle = grid; x.lineWidth = 1;
    x.beginPath(); x.moveTo(0, h - .5); x.lineTo(w, h - .5); x.stroke();
    const pts = state.hist; if (pts.length < 2) return;
    const max = Math.max(64 * 1024, ...pts.map((p) => Math.max(p[0], p[1]))) * 1.15;
    const N = 120, step = w / (N - 1), off = N - pts.length;
    const line = (k, color, fill) => {
      x.beginPath();
      pts.forEach((p, i) => { const px = (off + i) * step, py = h - 1 - (p[k] / max) * (h - 4); i ? x.lineTo(px, py) : x.moveTo(px, py); });
      if (fill) {
        x.lineTo((off + pts.length - 1) * step, h); x.lineTo(off * step, h); x.closePath();
        x.globalAlpha = .14; x.fillStyle = color; x.fill(); x.globalAlpha = 1;
        x.beginPath();
        pts.forEach((p, i) => { const px = (off + i) * step, py = h - 1 - (p[k] / max) * (h - 4); i ? x.lineTo(px, py) : x.moveTo(px, py); });
      }
      x.strokeStyle = color; x.lineWidth = 1.6; x.lineJoin = 'round'; x.stroke();
    };
    line(1, cUp, false);
    line(0, cDown, true);
  }
  window.addEventListener('resize', drawGraph);

  // ------------------------------------------------------------ selection & detail
  function select(id) {
    state.selected = id;
    state.detail = null;
    for (const [rid, el] of state.rows) { el.setAttribute('aria-selected', String(rid === id)); el.dataset.sig = ''; }
    $('detail').hidden = !id;
    if (id) { $('dBody').innerHTML = ''; $('dPieces').innerHTML = ''; loadDetail(); }
    if (state.snap) renderRows();
  }

  async function loadDetail() {
    const id = state.selected;
    if (!id || state.detailBusy) return;
    state.detailBusy = true; state.lastDetailAt = Date.now();
    try {
      const d = await api('GET', '/torrents/' + id);
      if (state.selected === id) { state.detail = d; renderDetail(); }
    } catch { } finally { state.detailBusy = false; }
  }

  function renderDetail() {
    const d = state.detail, t = state.snap?.torrents.find((x) => x.id === state.selected);
    if (!d || !t) return;
    setText($('dName'), t.name);
    document.querySelectorAll('#dTabs button').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.tab === state.tab)));
    // piece map
    const pm = $('dPieces');
    const cs = getComputedStyle(document.documentElement);
    const done = cs.getPropertyValue('--text-2').trim();
    const sig = d.pieceMap.join(',');
    if (pm.dataset.sig !== sig) {
      pm.dataset.sig = sig;
      pm.innerHTML = d.pieceMap.map((v) => `<i style="background:${v >= 100 ? done : v > 0 ? `color-mix(in srgb, ${done} ${Math.max(15, v)}%, transparent)` : 'transparent'}"></i>`).join('');
    }
    const body = $('dBody');
    const keepScroll = body.scrollTop;
    let html = '';
    if (state.tab === 'files') html = filesTable(d, t);
    else if (state.tab === 'peers') html = peersTable(d);
    else if (state.tab === 'trackers') html = trackersTable(d);
    else html = infoView(d, t);
    if (body.dataset.html !== html) {
      if (body.contains(document.activeElement) && document.activeElement.tagName === 'SELECT') return; // don't yank an open dropdown
      body.innerHTML = html; body.dataset.html = html; body.scrollTop = keepScroll;
    }
  }

  function splitPath(p) {
    const i = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
    return i < 0 ? ['', p] : [p.slice(0, i + 1), p.slice(i + 1)];
  }

  function filesTable(d) {
    if (!d.files.length) return `<div class="detail-empty">Getting file list</div>`;
    const rows = d.files.map((f) => {
      const [dir, name] = splitPath(f.path);
      return `<tr class="${f.priority === 'skip' ? 'skipped' : ''}" data-file="${f.index}">
        <td><input type="checkbox" class="chk" data-fcheck="${f.index}" ${f.priority === 'skip' ? '' : 'checked'} aria-label="Download"></td>
        <td class="grow" title="${esc(f.path)}"><span class="path-dir">${esc(dir)}</span>${esc(name)}</td>
        <td class="num">${bytes(f.size)}</td>
        <td><div class="mini-bar"><div class="bar"><i class="${f.progress >= 1 ? 'done' : ''}" style="width:${(f.progress * 100).toFixed(1)}%"></i></div><span>${pct(f.progress)}</span></div></td>
        <td><select class="select-plain" data-fprio="${f.index}" aria-label="Priority">
          ${['high', 'normal', 'low', 'skip'].map((p) => `<option value="${p}" ${p === f.priority ? 'selected' : ''}>${{ high: 'High', normal: 'Normal', low: 'Low', skip: 'Skip' }[p]}</option>`).join('')}
        </select></td>
        <td><button type="button" class="btn btn-icon btn-xs" data-fopen="${f.index}" title="Open" aria-label="Open" ${f.progress >= 1 ? '' : 'disabled'}>${icon('open')}</button></td>
      </tr>`;
    }).join('');
    return `<table class="tbl"><thead><tr><th></th><th>Name</th><th class="num">Size</th><th>Progress</th><th>Priority</th><th></th></tr></thead><tbody>${rows}</tbody></table>`;
  }

  function peersTable(d) {
    if (!d.peers.length) return `<div class="detail-empty">No peers connected</div>`;
    return `<table class="tbl"><thead><tr><th>Address</th><th class="grow">Client</th><th class="num">Down</th><th class="num">Up</th><th>Has</th><th>Flags</th></tr></thead><tbody>` +
      d.peers.map((p) => `<tr><td class="mono">${esc(p.address)}</td><td class="grow">${esc(p.client)}</td><td class="num">${p.down ? rate(p.down) : ''}</td><td class="num">${p.up ? rate(p.up) : ''}</td>
        <td><div class="mini-bar"><div class="bar"><i class="${p.progress >= 1 ? 'done' : ''}" style="width:${(p.progress * 100).toFixed(0)}%"></i></div><span>${pct(p.progress)}</span></div></td>
        <td class="mono" title="S seed · I incoming · E encrypted · D downloading · U uploading">${esc(p.flags)}</td></tr>`).join('') + '</tbody></table>';
  }

  function trackersTable(d) {
    if (!d.trackers.length) return `<div class="detail-empty">No trackers</div>`;
    const cls = { Working: 'ok', Offline: 'bad', Error: 'bad' };
    return `<table class="tbl"><thead><tr><th class="grow">Tracker</th><th>Status</th><th class="num">Seeds</th><th class="num">Peers</th><th class="num">Downloaded</th></tr></thead><tbody>` +
      d.trackers.map((t) => `<tr><td class="grow mono" title="${esc(t.message || t.url)}">${esc(t.url)}</td><td><span class="st-pill ${cls[t.status] || ''}">${esc(t.status)}</span></td>
        <td class="num">${t.seeds || ''}</td><td class="num">${t.peers || ''}</td><td class="num">${t.downloaded || ''}</td></tr>`).join('') + '</tbody></table>';
  }

  function infoView(d, t) {
    const cell = (k, v, cls = '') => `<div class="${cls}"><dt>${k}</dt><dd title="${esc(v)}">${v === '' || v == null ? '—' : esc(v)}</dd></div>`;
    return `<dl class="info">
      ${cell('Size', t.size ? bytes(t.size) : '')}${cell('Selected', bytes(t.wanted))}${cell('Downloaded', bytes(t.downloaded))}${cell('Uploaded', bytes(t.uploaded))}
      ${cell('Ratio', t.ratio.toFixed(2))}${cell('Files', t.files ? String(t.files) : '')}${cell('Pieces', d.pieces ? `${d.pieces} × ${bytes(d.pieceLength)}` : '')}${cell('Added', date(t.addedAt))}
      ${cell('Completed', date(t.completedAt))}${cell('Created', date(d.created))}${cell('Created by', d.createdBy || '', 'wide')}
      <div class="full"><dt>Saved in</dt><dd><code>${esc(t.savePath)}</code></dd></div>
      <div class="full"><dt>Info hash</dt><dd><code>${esc(d.hash)}</code></dd></div>
      ${d.comment ? `<div class="full"><dt>Comment</dt><dd>${esc(d.comment)}</dd></div>` : ''}
    </dl>
    <div class="info-actions">
      <button type="button" class="btn btn-sm" data-iact="copy-magnet">${icon('copy')}<span>Copy magnet link</span></button>
      <button type="button" class="btn btn-sm" data-iact="copy-hash">${icon('copy')}<span>Copy hash</span></button>
      <button type="button" class="btn btn-sm" data-iact="folder">${icon('folder')}<span>Open folder</span></button>
      <button type="button" class="btn btn-sm" data-iact="peers">${icon('users')}<span>Add peers</span></button>
    </div>`;
  }

  $('dTabs').addEventListener('click', (e) => {
    const b = e.target.closest('button'); if (!b) return;
    state.tab = b.dataset.tab; store.set('tab', state.tab);
    $('dBody').scrollTop = 0;
    renderDetail(); loadDetail();
  });
  $('dClose').addEventListener('click', () => select(null));

  $('dBody').addEventListener('change', async (e) => {
    const id = state.selected; if (!id) return;
    const prio = e.target.closest('[data-fprio]');
    const chk = e.target.closest('[data-fcheck]');
    try {
      if (prio) await api('POST', `/torrents/${id}/priority`, { files: [+prio.dataset.fprio], priority: prio.value });
      if (chk) await api('POST', `/torrents/${id}/priority`, { files: [+chk.dataset.fcheck], priority: chk.checked ? 'normal' : 'skip' });
      prio?.blur();
      loadDetail();
    } catch (err) { fail(err); }
  });
  $('dBody').addEventListener('click', (e) => {
    const t = state.snap?.torrents.find((x) => x.id === state.selected);
    const open = e.target.closest('[data-fopen]');
    if (open && state.detail) { const f = state.detail.files[+open.dataset.fopen]; if (f) api('POST', '/host/open', { path: f.fullPath }); return; }
    const a = e.target.closest('[data-iact]'); if (!a || !t) return;
    if (a.dataset.iact === 'copy-magnet') api('POST', `/torrents/${t.id}/copy-magnet`).then(() => toast('Magnet link copied'));
    if (a.dataset.iact === 'copy-hash') api('POST', '/host/copy', { text: state.detail.hash }).then(() => toast('Hash copied'));
    if (a.dataset.iact === 'folder') reveal(t);
    if (a.dataset.iact === 'peers') openPeers(t);
  });

  // ------------------------------------------------------------ list interaction
  $('rows').addEventListener('click', (e) => {
    const row = e.target.closest('.row'); if (!row) return;
    const id = row.dataset.id;
    const act = e.target.closest('[data-act]');
    if (act?.dataset.act === 'toggle') { toggle(id); return; }
    if (act?.dataset.act === 'menu') { const r = act.getBoundingClientRect(); openMenu(id, r.right, r.bottom + 4, true); return; }
    select(state.selected === id && !act ? id : id);
  });
  $('rows').addEventListener('dblclick', (e) => {
    const row = e.target.closest('.row'); if (!row || e.target.closest('[data-act]')) return;
    const t = state.snap.torrents.find((x) => x.id === row.dataset.id);
    if (t) { if (t.progress >= 1 && t.open) api('POST', '/host/open', { path: t.open }); else reveal(t); }
  });
  $('rows').addEventListener('contextmenu', (e) => {
    const row = e.target.closest('.row'); if (!row) return;
    e.preventDefault();
    select(row.dataset.id);
    openMenu(row.dataset.id, e.clientX, e.clientY);
  });
  $('rows').addEventListener('keydown', (e) => {
    const list = visible(); if (!list.length) return;
    const i = list.findIndex((t) => t.id === state.selected);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const n = list[Math.max(0, Math.min(list.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))];
      select(n.id); state.rows.get(n.id)?.scrollIntoView({ block: 'nearest' });
    } else if (state.selected && e.key === 'Delete') { e.preventDefault(); openRemove(state.selected, e.shiftKey); }
    else if (state.selected && e.key === ' ') { e.preventDefault(); toggle(state.selected); }
    else if (state.selected && e.key === 'Enter') { const t = list[i]; if (t) reveal(t); }
  });

  async function toggle(id) {
    const t = state.snap.torrents.find((x) => x.id === id); if (!t) return;
    const resume = ['paused', 'error', 'finished'].includes(t.status);
    try { await api('POST', `/torrents/${id}/${resume ? 'resume' : 'pause'}`); } catch (e) { fail(e); }
  }
  function reveal(t) {
    if (t.open) api('POST', '/host/reveal', { path: t.open }); else api('POST', '/host/reveal', { path: t.savePath });
  }

  // ------------------------------------------------------------ context menu
  function openMenu(id, x, y, alignRight) {
    const t = state.snap.torrents.find((v) => v.id === id); if (!t) return;
    const paused = ['paused', 'error', 'finished'].includes(t.status);
    const items = [
      paused ? ['resume', 'play', 'Resume'] : ['pause', 'pause', 'Pause'],
      ['force', 'zap', 'Force start', t.forced],
      '-',
      ['open', 'open', 'Open', null, !(t.open && t.progress >= 1)],
      ['folder', 'folder', 'Open folder'],
      ['magnet', 'copy', 'Copy magnet link'],
      '-',
      ['sequential', 'order', 'Download in order', t.sequential, !t.hasMetadata],
      ['recheck', 'refresh', 'Check files', null, !t.hasMetadata],
      ['move', 'move', 'Move files…', null, !t.hasMetadata],
      ['peers', 'users', 'Add peers…'],
      '-',
      ['top', 'chev-up', 'Move to top'],
      ['bottom', 'chev-down', 'Move to bottom'],
      '-',
      ['remove', 'x', 'Remove'],
      ['delete', 'trash', 'Remove and delete files', null, false, true],
    ];
    const m = $('menu');
    m.innerHTML = items.map((it) => it === '-' ? '<hr>' :
      `<button type="button" role="menuitem" data-m="${it[0]}" ${it[4] ? 'disabled style="opacity:.45;pointer-events:none"' : ''} class="${it[5] ? 'danger' : ''}">${icon(it[1])}<span>${it[2]}</span>${it[3] ? `<span class="on">${icon('check')}</span>` : ''}</button>`).join('');
    m.hidden = false;
    const r = m.getBoundingClientRect();
    let left = alignRight ? x - r.width : x, top = y;
    left = Math.max(8, Math.min(left, innerWidth - r.width - 8));
    if (top + r.height > innerHeight - 8) top = Math.max(8, y - r.height - (alignRight ? 36 : 0));
    m.style.left = left + 'px'; m.style.top = top + 'px';
    m.dataset.id = id;
    m.querySelector('button:not([disabled])')?.focus();
  }
  function closeMenu() { $('menu').hidden = true; }
  document.addEventListener('mousedown', (e) => { if (!$('menu').hidden && !e.target.closest('#menu')) closeMenu(); });
  window.addEventListener('blur', closeMenu);
  $('menu').addEventListener('keydown', (e) => {
    const btns = [...$('menu').querySelectorAll('button:not([disabled])')];
    const i = btns.indexOf(document.activeElement);
    if (e.key === 'Escape') { closeMenu(); $('rows').focus(); }
    if (e.key === 'ArrowDown') { e.preventDefault(); btns[(i + 1) % btns.length]?.focus(); }
    if (e.key === 'ArrowUp') { e.preventDefault(); btns[(i - 1 + btns.length) % btns.length]?.focus(); }
  });
  $('menu').addEventListener('click', async (e) => {
    const b = e.target.closest('[data-m]'); if (!b) return;
    const id = $('menu').dataset.id; closeMenu();
    const t = state.snap.torrents.find((v) => v.id === id); if (!t) return;
    try {
      switch (b.dataset.m) {
        case 'pause': await api('POST', `/torrents/${id}/pause`); break;
        case 'resume': await api('POST', `/torrents/${id}/resume`); break;
        case 'force': await api('POST', `/torrents/${id}/${t.forced ? 'resume' : 'force'}`); break;
        case 'open': api('POST', '/host/open', { path: t.open }); break;
        case 'folder': reveal(t); break;
        case 'magnet': await api('POST', `/torrents/${id}/copy-magnet`); toast('Magnet link copied'); break;
        case 'sequential': await api('POST', `/torrents/${id}/sequential`, { on: !t.sequential }); break;
        case 'recheck': await api('POST', `/torrents/${id}/recheck`); break;
        case 'move': { const dir = await api('POST', `/torrents/${id}/move`); if (dir) toast('Moving files'); break; }
        case 'peers': openPeers(t); break;
        case 'top': await api('POST', `/torrents/${id}/order`, { delta: -1e9 }); break;
        case 'bottom': await api('POST', `/torrents/${id}/order`, { delta: 1e9 }); break;
        case 'remove': openRemove(id, false); break;
        case 'delete': openRemove(id, true); break;
      }
    } catch (err) { fail(err); }
  });

  // ------------------------------------------------------------ dialogs
  for (const d of document.querySelectorAll('dialog')) {
    d.addEventListener('click', (e) => { if (e.target.closest('[data-close]')) d.close(); });
  }

  let removeId = null;
  function openRemove(id, withFiles) {
    const t = state.snap.torrents.find((v) => v.id === id); if (!t) return;
    removeId = id;
    setText($('rTitle'), `Remove “${t.name}”?`);
    $('rFiles').checked = !!withFiles;
    $('removeDialog').showModal();
    $('rGo').focus();
  }
  $('rGo').addEventListener('click', async () => {
    const id = removeId; $('removeDialog').close();
    try { await api('POST', `/torrents/${id}/remove`, { deleteFiles: $('rFiles').checked }); if (state.selected === id) select(null); } catch (e) { fail(e); }
  });

  let peersId = null;
  function openPeers(t) { peersId = t.id; $('pText').value = ''; $('peersDialog').showModal(); $('pText').focus(); }
  $('pGo').addEventListener('click', async () => {
    try {
      const r = await api('POST', `/torrents/${peersId}/peers`, { addresses: $('pText').value.split(/[\s,]+/).filter(Boolean) });
      $('peersDialog').close(); toast(`Added ${r.added} peer${r.added === 1 ? '' : 's'}`);
    } catch (e) { fail(e); }
  });

  // ------------------------------------------------------------ adding
  const add = { queue: [], src: null, preview: null, abort: null, dir: null, skip: new Set(), started: 0, timer: null };

  function looksLikeLink(s) {
    s = s.trim();
    return /^magnet:\?/i.test(s) || /^[0-9a-f]{40}$/i.test(s) || /^[a-z2-7]{32}$/i.test(s) || /^https?:\/\/\S+$/i.test(s) || /\.torrent$/i.test(s);
  }

  async function addSources(list) {
    list = list.map((s) => s.trim()).filter(Boolean);
    if (!list.length) return;
    const ask = state.snap?.settings.showAddDialog !== false;
    if (ask) { add.queue.push(...list); if (!$('addDialog').open) nextAdd(); return; }
    try {
      const r = await api('POST', '/add-many', { sources: list });
      announceAdded(r.added);
      for (const e of r.errors || []) toast(e, 'error');
    } catch (e) { fail(e); }
  }

  function announceAdded(added) {
    const fresh = added.filter((a) => !a.existing);
    if (fresh.length === 1) toast(`Added ${fresh[0].name}`);
    else if (fresh.length > 1) toast(`Added ${fresh.length} torrents`);
    if (added.some((a) => a.existing)) toast('Already in your list');
    if (added.length === 1) select(added[0].id);
  }

  async function nextAdd() {
    const src = add.queue.shift();
    if (!src) return;
    add.src = src; add.preview = null; add.skip = new Set();
    add.dir = state.snap?.settings.saveDir || '';
    $('aSeq').checked = !!state.snap?.settings.sequentialByDefault;
    $('aPaused').checked = false;
    setText($('aTitle'), 'Reading…'); setText($('aSub'), '');
    $('aIcon').innerHTML = icon('magnet');
    $('aFetching').hidden = true; $('aFilesBox').hidden = true; setText($('aWarn'), '');
    $('aAdd').disabled = true;
    renderAddDir();
    if (!$('addDialog').open) $('addDialog').showModal();
    try {
      const p = await api('POST', '/preview', { source: src, fetch: false });
      if (add.src !== src) return;
      showPreview(p);
      if (!p.hasMetadata && !p.existing) fetchMeta(src);
    } catch (e) {
      if (add.src !== src) return;
      $('addDialog').close(); fail(e); nextAdd();
    }
  }

  function showPreview(p) {
    add.preview = p;
    setText($('aTitle'), p.name);
    $('aTitle').title = p.name;
    const parts = [];
    if (p.size) parts.push(bytes(p.size));
    if (p.files.length) parts.push(p.files.length === 1 ? '1 file' : p.files.length + ' files');
    if (p.trackers) parts.push(p.trackers + (p.trackers === 1 ? ' tracker' : ' trackers'));
    if (p.private) parts.push('Private');
    setText($('aSub'), parts.join(' · '));
    const big = p.files.slice().sort((a, b) => b.size - a.size)[0];
    $('aIcon').innerHTML = icon(p.hasMetadata ? (big ? (p.files.length > 1 && fileKind(big.path) === 'file' ? 'folder' : fileKind(big.path)) : 'file') : 'magnet');
    $('aAdd').disabled = false;
    setText($('aAdd'), p.existing ? 'Show' : 'Add');
    if (p.existing) { setText($('aWarn'), 'Already in your list'); }
    if (p.hasMetadata) {
      $('aFetching').hidden = true;
      $('aFilesBox').hidden = p.files.length <= 1;
      renderAddFiles();
    }
    renderAddFree();
  }

  async function fetchMeta(src) {
    add.abort?.abort();
    const ctl = new AbortController(); add.abort = ctl;
    $('aFetching').hidden = false;
    add.started = Date.now();
    clearInterval(add.timer);
    add.timer = setInterval(() => setText($('aFetchTime'), eta((Date.now() - add.started) / 1000)), 1000);
    setText($('aFetchTime'), '');
    try {
      const p = await api('POST', '/preview', { source: src, fetch: true }, ctl.signal);
      if (add.src === src) showPreview(p);
    } catch (e) {
      if (e.name !== 'AbortError' && add.src === src) { $('aFetching').hidden = true; setText($('aWarn'), e.message); }
    } finally { clearInterval(add.timer); }
  }

  function renderAddFiles() {
    const p = add.preview; if (!p) return;
    const box = $('aFiles');
    box.innerHTML = p.files.map((f) => {
      const [dir, name] = splitPath(f.path);
      const on = !add.skip.has(f.index);
      return `<label class="file ${on ? '' : 'off'}"><input type="checkbox" data-i="${f.index}" ${on ? 'checked' : ''}>${icon(fileKind(name) === 'file' ? 'file' : fileKind(name))}
        <span class="file-name" title="${esc(f.path)}"><span class="path-dir">${esc(dir)}</span>${esc(name)}</span><span class="file-size">${bytes(f.size)}</span></label>`;
    }).join('');
    updateAddPick();
  }
  function wantedSize() {
    const p = add.preview; if (!p) return 0;
    if (!p.hasMetadata) return p.size || 0;
    return p.files.filter((f) => !add.skip.has(f.index)).reduce((s, f) => s + f.size, 0);
  }
  function updateAddPick() {
    const p = add.preview; if (!p) return;
    const n = p.files.length - add.skip.size;
    setText($('aPick'), `${n} of ${p.files.length} · ${bytes(wantedSize())}`);
    $('aAll').checked = add.skip.size === 0;
    $('aAll').indeterminate = add.skip.size > 0 && n > 0;
    $('aAdd').disabled = p.hasMetadata && n === 0 && !p.existing;
    renderAddFree();
  }
  $('aFiles').addEventListener('change', (e) => {
    const c = e.target.closest('input[data-i]'); if (!c) return;
    const i = +c.dataset.i;
    if (c.checked) add.skip.delete(i); else add.skip.add(i);
    c.closest('.file').classList.toggle('off', !c.checked);
    updateAddPick();
  });
  $('aAll').addEventListener('change', () => {
    add.skip = $('aAll').checked ? new Set() : new Set(add.preview.files.map((f) => f.index));
    renderAddFiles();
  });
  function renderAddDir() { setText($('aDir'), add.dir); $('aDir').title = add.dir; renderAddFree(); }
  let freeSeq = 0;
  async function renderAddFree() {
    const seq = ++freeSeq;
    try {
      const r = await api('POST', '/free', { dir: add.dir });
      if (seq !== freeSeq) return;
      const need = wantedSize();
      const el = $('aFree');
      if (r.free < 0) { setText(el, ''); return; }
      const short = need > r.free;
      el.className = 'free-line' + (short ? ' bad' : '');
      setText(el, short ? `Needs ${bytes(need)}, only ${bytes(r.free)} free` : `${bytes(r.free)} free`);
    } catch { }
  }
  $('aDirBtn').addEventListener('click', async () => {
    const dir = await api('POST', '/host/pick-folder', { title: 'Save to' }).catch(() => null);
    if (dir) { add.dir = dir; renderAddDir(); }
  });
  $('aAdd').addEventListener('click', async () => {
    const p = add.preview; if (!p) return;
    if (p.existing) { $('addDialog').close(); select(p.existing); return; }
    $('aAdd').classList.add('loading');
    try {
      const r = await api('POST', '/add', {
        source: add.src, previewId: p.id, saveDir: add.dir, paused: $('aPaused').checked, sequential: $('aSeq').checked,
        skip: p.hasMetadata ? [...add.skip] : null,
      });
      add.abort?.abort();
      $('addDialog').close();
      announceAdded([r]);
    } catch (e) { setText($('aWarn'), e.message); } finally { $('aAdd').classList.remove('loading'); }
  });
  $('addDialog').addEventListener('close', () => {
    add.abort?.abort(); clearInterval(add.timer); add.src = null; add.preview = null;
    if (add.queue.length) setTimeout(nextAdd, 50);
  });

  $('adder').addEventListener('submit', (e) => {
    e.preventDefault();
    const v = $('addInput').value.trim();
    if (!v) { $('addInput').focus(); return; }
    $('addInput').value = '';
    const links = v.match(/magnet:\?[^\s"'<>]+/gi);
    addSources(links && links.length ? links : v.split(/\n+/));
  });
  async function openFiles() {
    try { const paths = await api('POST', '/host/pick-torrents'); if (paths?.length) addSources(paths); } catch (e) { fail(e); }
  }
  async function pasteFromClipboard() {
    let text = '';
    try { text = (await api('POST', '/host/clipboard')).text || ''; } catch { }
    if (!text) try { text = await navigator.clipboard.readText(); } catch { }
    const links = text.match(/magnet:\?[^\s"'<>]+/gi);
    if (links?.length) addSources(links);
    else if (looksLikeLink(text)) addSources([text]);
    else toast('No magnet link on the clipboard', 'error');
  }
  $('openFileBtn').addEventListener('click', openFiles);
  $('emptyOpen').addEventListener('click', openFiles);
  $('emptyPaste').addEventListener('click', pasteFromClipboard);

  document.addEventListener('paste', (e) => {
    if (e.target.closest('input, textarea') || document.querySelector('dialog[open]')) return;
    const text = e.clipboardData.getData('text') || '';
    const links = text.match(/magnet:\?[^\s"'<>]+/gi);
    if (links?.length) { e.preventDefault(); addSources(links); }
    else if (looksLikeLink(text)) { e.preventDefault(); addSources([text]); }
  });

  // drag & drop
  let dragDepth = 0;
  const hasDroppable = (e) => [...(e.dataTransfer?.types || [])].some((t) => t === 'Files' || t === 'text/uri-list' || t === 'text/plain');
  document.addEventListener('dragenter', (e) => { if (!hasDroppable(e)) return; e.preventDefault(); dragDepth++; $('dropVeil').hidden = false; });
  document.addEventListener('dragover', (e) => { if (hasDroppable(e)) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
  document.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; $('dropVeil').hidden = true; } });
  document.addEventListener('drop', (e) => {
    e.preventDefault(); dragDepth = 0; $('dropVeil').hidden = true;
    const files = [...(e.dataTransfer.files || [])];
    if (files.length && host) { host.postMessageWithAdditionalObjects({ type: 'drop' }, e.dataTransfer.files); return; }
    const text = e.dataTransfer.getData('text/uri-list') || e.dataTransfer.getData('text/plain') || '';
    const links = text.match(/magnet:\?[^\s"'<>]+/gi) || text.split(/\n+/).filter(looksLikeLink);
    if (links.length) addSources(links);
  });

  // native shell entry points
  window.cft = {
    addPaths: (paths) => addSources(paths.filter((p) => /\.torrent$/i.test(p) || looksLikeLink(p))),
    addLinks: (links) => addSources(links),
  };

  // ------------------------------------------------------------ top/side controls
  $('filters').addEventListener('click', (e) => {
    const b = e.target.closest('button'); if (!b) return;
    state.filter = b.dataset.filter; store.set('filter', state.filter); render();
  });
  $('search').addEventListener('input', () => { state.search = $('search').value; renderRows(); });
  $('sortBy').value = state.sort;
  $('sortBy').addEventListener('change', () => { state.sort = $('sortBy').value; store.set('sort', state.sort); renderRows(); });
  $('pauseAllBtn').addEventListener('click', () => api('POST', '/pause-all').catch(fail));
  $('resumeAllBtn').addEventListener('click', () => api('POST', '/resume-all').catch(fail));
  $('whenDone').addEventListener('change', () => api('POST', '/when-done', { action: $('whenDone').value }).catch(fail));

  // ------------------------------------------------------------ settings
  $('settingsBtn').addEventListener('click', () => { renderSettings(true); $('settingsDialog').showModal(); });
  function renderSettingsIfOpen() { if ($('settingsDialog').open) renderSettings(false); }
  function renderSettings(force) {
    const s = state.snap?.settings; if (!s) return;
    setText($('sSaveDir'), s.saveDir); $('sSaveDir').title = s.saveDir;
    setText($('sMoveDir'), s.moveDoneTo || 'Off'); $('sMoveDir').title = s.moveDoneTo || '';
    $('sMoveClear').hidden = !s.moveDoneTo;
    for (const el of document.querySelectorAll('[data-set]')) {
      if (!force && el === document.activeElement) continue;
      const v = s[el.dataset.set];
      if (el.type === 'checkbox') el.checked = !!v;
      else if (el.type === 'number') { el.placeholder = el.dataset.zero || ''; el.value = v === 0 && el.dataset.zero ? '' : v; }
      else if (el.value !== String(v ?? '')) el.value = v ?? '';
    }
    document.querySelectorAll('#sSeed button').forEach((b) => b.setAttribute('aria-checked', String(b.dataset.v === s.seeding)));
    $('sRatioRow').hidden = s.seeding !== 'ratio';
    $('sDefault').hidden = !!state.snap.isDefault; $('sDefaultOk').hidden = !state.snap.isDefault;
    setText($('version'), 'ClipFx Torrent ' + state.snap.version);
  }
  async function saveSetting(patch) { try { await api('POST', '/settings', patch); } catch (e) { fail(e); } }
  for (const el of document.querySelectorAll('[data-set]')) {
    el.addEventListener('change', () => {
      const k = el.dataset.set;
      let v;
      if (el.type === 'checkbox') v = el.checked;
      else if (el.type === 'number') { v = el.value === '' ? 0 : Number(el.value); if (!isFinite(v)) return; }
      else v = el.value;
      saveSetting({ [k]: v });
    });
  }
  $('sSeed').addEventListener('click', (e) => { const b = e.target.closest('button'); if (b) saveSetting({ seeding: b.dataset.v }); });
  $('sSaveDirBtn').addEventListener('click', async () => {
    const d = await api('POST', '/host/pick-folder', { title: 'Save downloads to' }).catch(() => null); if (d) saveSetting({ saveDir: d });
  });
  $('sMoveBtn').addEventListener('click', async () => {
    const d = await api('POST', '/host/pick-folder', { title: 'Move finished downloads to' }).catch(() => null); if (d) saveSetting({ moveDoneTo: d });
  });
  $('sMoveClear').addEventListener('click', () => saveSetting({ moveDoneTo: '' }));
  $('sRandomPort').addEventListener('click', () => saveSetting({ randomPort: true }));
  $('sDefault').addEventListener('click', () => api('POST', '/host/default-app').then(() => toast('ClipFx Torrent now opens magnet links and .torrent files')).catch(fail));

  // ------------------------------------------------------------ keyboard
  document.addEventListener('keydown', (e) => {
    if (document.querySelector('dialog[open]')) return;
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'o') { e.preventDefault(); openFiles(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f') { e.preventDefault(); $('search').focus(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'n') { e.preventDefault(); $('addInput').focus(); }
    else if (e.key === 'Escape' && state.selected && !e.target.closest('input')) select(null);
  });

  applyTheme(state.theme);
  connect();
})();
