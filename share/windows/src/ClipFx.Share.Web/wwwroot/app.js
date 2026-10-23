(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const host = window.chrome && window.chrome.webview;
  const store = {
    get(k, d) { try { return localStorage.getItem('cfs.' + k) ?? d; } catch { return d; } },
    set(k, v) { try { localStorage.setItem('cfs.' + k, v); } catch { } },
  };
  const state = {
    snap: null, connected: false, everConnected: false,
    selected: new Set(JSON.parse(store.get('selected', '[]'))),
    picked: [], text: null,
    tab: 'active', theme: store.get('theme', 'system'), themeTouched: false,
    shownIncoming: null, qrAddr: null,
  };

  // ------------------------------------------------------------ utils
  const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];
  function bytes(n) {
    if (!(n > 0)) return '0 B';
    let i = 0; while (n >= 1024 && i < UNITS.length - 1) { n /= 1024; i++; }
    return n.toFixed(i === 0 ? 0 : n < 10 ? 2 : n < 100 ? 1 : 0) + ' ' + UNITS[i];
  }
  const rate = (n) => bytes(n) + '/s';
  function eta(s) {
    if (s == null || !isFinite(s)) return '';
    s = Math.round(s);
    if (s < 60) return s + 's';
    if (s < 3600) return Math.floor(s / 60) + 'm ' + (s % 60) + 's';
    return Math.floor(s / 3600) + 'h ' + Math.floor((s % 3600) / 60) + 'm';
  }
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const icon = (n) => `<svg class="i"><use href="#i-${n}"/></svg>`;
  const setText = (el, v) => { if (el.textContent !== v) el.textContent = v; };

  const CATS = {
    video: 'mp4 mkv mov avi wmv flv webm m4v 3gp ts mts m2ts mpeg mpg vob', image: 'jpg jpeg png gif webp heic heif bmp tif tiff svg ico avif raw dng psd',
    music: 'mp3 m4a aac flac wav ogg opus wma amr mid midi', doc: 'pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf csv md epub json xml html htm log',
    app: 'apk apks xapk aab exe msi msix appx dmg pkg deb rpm', archive: 'zip rar 7z tar gz tgz bz2 xz zst cab iso img',
  };
  function fileIcon(name, isDir) {
    if (isDir) return 'folder';
    const ext = (name.split('.').pop() || '').toLowerCase();
    for (const [k, v] of Object.entries(CATS)) if (v.split(' ').includes(ext)) return k;
    return 'file';
  }
  const platformIcon = (p) => p === 'android' ? 'phone' : 'monitor';

  async function api(method, path, body) {
    const r = await fetch('/api' + path, { method, headers: Object.assign({ 'X-ClipFx': '1' }, body !== undefined ? { 'Content-Type': 'application/json' } : {}), body: body !== undefined ? JSON.stringify(body) : undefined });
    if (!r.ok) { let m = ''; try { m = (await r.json()).error || ''; } catch { } throw new Error(m || 'Something went wrong.'); }
    const t = await r.text(); return t ? JSON.parse(t) : null;
  }
  function toast(msg, kind) {
    const el = document.createElement('div');
    el.className = 'toast' + (kind === 'error' ? ' error' : '');
    el.innerHTML = icon(kind === 'error' ? 'alert' : 'check') + '<span></span>';
    el.lastChild.textContent = msg;
    $('toasts').appendChild(el);
    setTimeout(() => el.remove(), kind === 'error' ? 4200 : 2400);
  }
  async function busy(btn, fn) {
    if (btn.classList.contains('loading')) return;
    btn.classList.add('loading');
    try { return await fn(); } finally { btn.classList.remove('loading'); }
  }

  // ------------------------------------------------------------ theme
  const media = matchMedia('(prefers-color-scheme: dark)');
  function applyTheme(t, persist) {
    state.theme = t; store.set('theme', t);
    if (t === 'system') document.documentElement.removeAttribute('data-theme'); else document.documentElement.setAttribute('data-theme', t);
    document.querySelectorAll('#themeSeg button').forEach((b) => b.setAttribute('aria-checked', String(b.dataset.themeValue === t)));
    try { host?.postMessage({ type: 'theme', dark: t === 'dark' || (t === 'system' && media.matches) }); } catch { }
    if (persist) { state.themeTouched = true; api('POST', '/settings', { theme: t }).catch(() => { }); }
  }
  media.addEventListener('change', () => applyTheme(state.theme, false));
  $('themeSeg').addEventListener('click', (e) => { const b = e.target.closest('button'); if (b) applyTheme(b.dataset.themeValue, true); });

  // ------------------------------------------------------------ stream
  let es, last = 0;
  function connect() {
    es?.close();
    es = new EventSource('/api/stream');
    es.onmessage = (e) => { last = Date.now(); state.connected = state.everConnected = true; render(JSON.parse(e.data)); };
    es.onerror = () => { state.connected = false; banners(); if (es.readyState === 2) setTimeout(connect, 1500); };
  }
  setInterval(() => { if (state.connected && Date.now() - last > 5000) { state.connected = false; banners(); connect(); } }, 1000);

  function render(s) {
    const first = !state.snap;
    state.snap = s;
    if (first) {
      $('app').removeAttribute('aria-busy');
      if (!state.themeTouched && s.settings.theme !== state.theme) applyTheme(s.settings.theme, false);
    }
    renderSelf(s); renderPeers(s); renderSend(); renderTransfers(s); renderIncoming(s); banners();
  }

  function banners() {
    const el = $('banners');
    const html = !state.connected ? `<div class="banner banner-muted"><span class="spinner"></span><span class="msg">${state.everConnected ? 'Reconnecting' : 'Starting'}</span></div>` : '';
    if (el.dataset.html !== html) { el.dataset.html = html; el.innerHTML = html; }
  }

  // ------------------------------------------------------------ self + peers
  function renderSelf(s) {
    setText($('selfName'), s.self.name);
    setText($('selfAddr'), s.self.addresses.length ? s.self.addresses.join(' · ') : 'Not connected');
  }

  const peerEls = new Map();
  function renderPeers(s) {
    const list = $('peers');
    const ids = new Set(s.peers.map((p) => p.id));
    for (const [id, el] of peerEls) if (!ids.has(id)) { el.remove(); peerEls.delete(id); }
    const els = s.peers.map((p) => {
      let el = peerEls.get(p.id);
      if (!el) {
        el = document.createElement('button');
        el.type = 'button'; el.className = 'peer'; el.setAttribute('role', 'option'); el.dataset.id = p.id;
        el.innerHTML = `<span class="peer-icon"></span><span class="peer-text"><div class="peer-name"></div><div class="peer-addr"></div></span><span class="dot"></span><span class="tick">${icon('check')}</span>`;
        peerEls.set(p.id, el);
      }
      const html = icon(platformIcon(p.platform));
      if (el.dataset.icon !== p.platform) { el.dataset.icon = p.platform; el.querySelector('.peer-icon').innerHTML = html; }
      setText(el.querySelector('.peer-name'), p.name);
      setText(el.querySelector('.peer-addr'), p.address + (p.trusted ? ' · Trusted' : ''));
      el.querySelector('.dot').classList.toggle('on', p.online);
      el.setAttribute('aria-selected', String(state.selected.has(p.id)));
      el.title = p.online ? '' : 'Not seen recently';
      return el;
    });
    const cur = Array.from(list.children);
    if (cur.length !== els.length || cur.some((e, i) => e !== els[i])) list.replaceChildren(...els);
    $('peersEmpty').hidden = s.peers.length > 0;
  }
  $('peers').addEventListener('click', (e) => {
    const b = e.target.closest('.peer'); if (!b) return;
    const id = b.dataset.id;
    if (state.selected.has(id)) state.selected.delete(id); else state.selected.add(id);
    store.set('selected', JSON.stringify([...state.selected]));
    renderPeers(state.snap); renderSend();
  });
  $('scanBtn').addEventListener('click', (e) => busy(e.currentTarget, () => api('POST', '/peers/scan').catch((err) => toast(err.message, 'error'))));

  // ------------------------------------------------------------ picking
  async function addPaths(paths) {
    if (!paths || !paths.length) return;
    const info = await api('POST', '/inspect', { paths });
    for (const it of info) if (!state.picked.some((p) => p.path === it.path)) state.picked.push(it);
    renderSend();
  }
  window.cfs = { addPaths };
  $('pickFiles').addEventListener('click', async () => addPaths(await api('POST', '/host/pick-files')));
  $('pickFolder').addEventListener('click', async () => { const p = await api('POST', '/host/pick-folder'); if (p) addPaths([p]); });
  $('addText').addEventListener('click', () => { $('textValue').value = state.text || ''; open($('textDialog')); setTimeout(() => $('textValue').focus(), 30); });
  $('textForm').addEventListener('submit', (e) => { e.preventDefault(); state.text = $('textValue').value.trim() || null; $('textDialog').close(); renderSend(); });

  const drop = $('drop');
  ['dragenter', 'dragover'].forEach((t) => document.addEventListener(t, (e) => { e.preventDefault(); drop.classList.add('over'); }));
  ['dragleave', 'drop'].forEach((t) => document.addEventListener(t, (e) => { e.preventDefault(); if (t === 'drop' || !e.relatedTarget) drop.classList.remove('over'); }));
  document.addEventListener('drop', (e) => {
    const files = e.dataTransfer?.files;
    if (!files || !files.length) return;
    if (host?.postMessageWithAdditionalObjects) host.postMessageWithAdditionalObjects({ type: 'drop' }, files);
  });

  function renderSend() {
    const box = $('picked');
    const rows = state.picked.map((p, i) => `<div class="pick"><span class="peer-icon">${icon(fileIcon(p.name, p.isDir))}</span>
      <span class="pick-name" title="${esc(p.path)}">${esc(p.name)}</span>
      <span class="pick-size">${p.isDir ? `${p.files} files · ` : ''}${bytes(p.size)}</span>
      <button type="button" class="btn btn-icon btn-xs" data-remove="${i}" aria-label="Remove">${icon('x')}</button></div>`);
    if (state.text) rows.push(`<div class="pick"><span class="peer-icon">${icon('text')}</span><span class="pick-name">${esc(state.text.slice(0, 80))}</span>
      <span class="pick-size">${bytes(new Blob([state.text]).size)}</span><button type="button" class="btn btn-icon btn-xs" data-remove="text" aria-label="Remove">${icon('x')}</button></div>`);
    const html = rows.join('');
    if (box.dataset.html !== html) { box.dataset.html = html; box.innerHTML = html; }
    const count = state.picked.length + (state.text ? 1 : 0);
    const total = state.picked.reduce((n, p) => n + p.size, 0);
    setText($('sendSummary'), count ? `${count} selected · ${bytes(total)}` : 'Nothing selected');
    $('clearPicked').hidden = !count;
    const targets = [...state.selected].filter((id) => state.snap?.peers.some((p) => p.id === id));
    $('sendBtn').disabled = !count || !targets.length;
    setText($('sendLabel'), targets.length > 1 ? `Send to ${targets.length}` : 'Send');
  }
  $('picked').addEventListener('click', (e) => {
    const b = e.target.closest('[data-remove]'); if (!b) return;
    if (b.dataset.remove === 'text') state.text = null; else state.picked.splice(+b.dataset.remove, 1);
    renderSend();
  });
  $('clearPicked').addEventListener('click', () => { state.picked = []; state.text = null; renderSend(); });
  $('sendBtn').addEventListener('click', (e) => busy(e.currentTarget, async () => {
    const targets = [...state.selected].filter((id) => state.snap.peers.some((p) => p.id === id));
    try {
      await api('POST', '/send', { targets, paths: state.picked.map((p) => p.path), text: state.text });
      state.picked = []; state.text = null; state.tab = 'active'; renderSend(); renderTransfers(state.snap);
    } catch (err) { toast(err.message, 'error'); }
  }));

  // ------------------------------------------------------------ transfers
  const STATE = {
    waiting: (t) => t.direction === 'send' ? `Waiting for ${t.peer.name}` : 'Waiting for you', connecting: () => 'Connecting',
    active: (t) => t.direction === 'send' ? 'Sending' : 'Receiving', paused: () => 'Paused', pausedByPeer: (t) => `Paused by ${t.peer.name}`,
    reconnecting: () => 'Reconnecting', completed: (t) => t.direction === 'send' ? 'Sent' : 'Received', declined: () => 'Declined',
    cancelled: () => 'Cancelled', failed: () => 'Failed', interrupted: () => 'Interrupted',
  };
  const FINAL = new Set(['completed', 'declined', 'cancelled', 'failed']);

  function transferHtml(t) {
    const frac = t.totalBytes > 0 ? Math.min(1, t.doneBytes / t.totalBytes) : (t.state === 'completed' ? 1 : 0);
    const cls = t.state === 'completed' ? 'ok' : ['failed', 'declined', 'cancelled'].includes(t.state) ? 'bad' : ['reconnecting', 'interrupted', 'pausedByPeer'].includes(t.state) ? 'warn' : '';
    const meta = [t.peer.name];
    if (t.state === 'active') meta.push(`${bytes(t.doneBytes)} of ${bytes(t.totalBytes)}`, rate(t.rate), eta(t.eta));
    else meta.push(t.files === 1 ? '1 file' : `${t.files} files`, bytes(t.totalBytes));
    const act = [];
    if (t.canPause) act.push(`<button type="button" class="btn btn-icon btn-sm" data-act="pause" title="Pause" aria-label="Pause">${icon('pause')}</button>`);
    if (t.canResume || t.canRetry) act.push(`<button type="button" class="btn btn-icon btn-sm" data-act="resume" title="${t.canRetry ? 'Retry' : 'Resume'}" aria-label="Resume">${icon(t.canRetry ? 'refresh' : 'play')}</button>`);
    if (t.openPath) act.push(`<button type="button" class="btn btn-icon btn-sm" data-act="open" title="Open folder" aria-label="Open folder">${icon('open')}</button>`);
    if (t.text) act.push(`<button type="button" class="btn btn-icon btn-sm" data-act="copy" title="Copy text" aria-label="Copy text">${icon('copy')}</button>`);
    if (t.canCancel && !(t.direction === 'receive' && t.state === 'waiting')) act.push(`<button type="button" class="btn btn-icon btn-sm" data-act="cancel" title="Cancel" aria-label="Cancel">${icon('x')}</button>`);
    if (FINAL.has(t.state) || t.state === 'interrupted') act.push(`<button type="button" class="btn btn-icon btn-sm" data-act="remove" title="Remove" aria-label="Remove">${icon('trash')}</button>`);
    return `<span class="peer-icon">${icon(t.direction === 'send' ? 'arrow-up' : 'arrow-down')}</span>
      <span class="tf-title" title="${esc(t.title)}">${esc(t.title)}</span>
      <span class="tf-state ${cls}">${esc(STATE[t.state] ? STATE[t.state](t) : t.state)}</span>
      <span class="tf-meta">${esc(meta.filter(Boolean).join(' · '))}${t.currentFile ? ' · ' + esc(t.currentFile.split('/').pop()) : ''}</span>
      <span class="tf-actions">${act.join('')}</span>
      ${!FINAL.has(t.state) || t.state === 'completed' ? `<div class="tf-bar"><i class="${t.state === 'completed' ? 'done' : ''}" style="width:${(frac * 100).toFixed(1)}%"></i></div>` : ''}
      ${t.error && t.state !== 'completed' ? `<div class="tf-err">${esc(t.error)}</div>` : ''}
      ${t.text ? `<div class="tf-text">${esc(t.text)}</div>` : ''}`;
  }

  const tfEls = new Map();
  function renderTransfers(s) {
    const active = s.transfers.filter((t) => !FINAL.has(t.state));
    const history = s.transfers.filter((t) => FINAL.has(t.state));
    setText($('activeCount'), active.length ? String(active.length) : '');
    document.querySelectorAll('#tfTabs [role="tab"]').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.tab === state.tab)));
    $('clearHistory').hidden = state.tab !== 'history' || !history.length;
    const list = state.tab === 'active' ? active : history;
    const seen = new Set();
    const els = list.map((t) => {
      seen.add(t.id);
      let el = tfEls.get(t.id);
      if (!el) { el = document.createElement('div'); el.className = 'tf'; el.dataset.id = t.id; tfEls.set(t.id, el); }
      const html = transferHtml(t);
      if (el.dataset.html !== html) { el.dataset.html = html; el.innerHTML = html; }
      return el;
    });
    for (const [id, el] of tfEls) if (!seen.has(id)) { el.remove(); tfEls.delete(id); }
    const box = $('tfList');
    const cur = Array.from(box.children);
    if (cur.length !== els.length || cur.some((e, i) => e !== els[i])) box.replaceChildren(...els);
    $('tfEmpty').hidden = list.length > 0;
    setText($('tfEmptyText'), state.tab === 'active' ? 'No active transfers' : 'No history');
  }
  $('tfTabs').addEventListener('click', (e) => { const b = e.target.closest('[data-tab]'); if (!b) return; state.tab = b.dataset.tab; renderTransfers(state.snap); });
  $('clearHistory').addEventListener('click', async () => { if (await confirm('Clear history?', 'Clear')) api('POST', '/history/clear'); });
  $('tfList').addEventListener('click', async (e) => {
    const b = e.target.closest('[data-act]'); if (!b) return;
    const id = b.closest('.tf').dataset.id;
    const t = state.snap.transfers.find((x) => x.id === id); if (!t) return;
    const act = b.dataset.act;
    try {
      if (act === 'open') await api('POST', '/host/reveal', { path: t.openPath });
      else if (act === 'copy') { await api('POST', '/host/copy', { text: t.text }); toast('Copied'); }
      else if (act === 'cancel') { if (await confirm(`Cancel ${t.title}?`, 'Cancel transfer')) await api('POST', `/transfers/${id}/cancel`); }
      else await api('POST', `/transfers/${id}/${act}`);
    } catch (err) { toast(err.message, 'error'); }
  });

  // ------------------------------------------------------------ incoming
  function renderIncoming(s) {
    const d = $('incomingDialog');
    const t = s.transfers.find((x) => x.direction === 'receive' && x.state === 'waiting');
    if (!t) { if (d.open) d.close(); state.shownIncoming = null; return; }
    if (state.shownIncoming !== t.id) {
      state.shownIncoming = t.id;
      $('inIcon').innerHTML = icon(platformIcon(t.peer.platform));
      setText($('inName'), t.peer.name);
      $('inTrust').checked = false;
      setText($('inTrustLabel'), `Always accept from ${t.peer.name}`);
      try { host?.postMessage({ type: 'attention' }); } catch { }
    }
    setText($('inSummary'), `${t.files === 1 ? '1 file' : t.files + ' files'}${t.folders ? ` · ${t.folders} folders` : ''} · ${bytes(t.totalBytes)}`);
    $('inList').innerHTML = t.preview.map((p) => `<li>${esc(p)}</li>`).join('');
    $('inText').hidden = !t.text; if (t.text) setText($('inText'), t.text);
    $('inWarn').hidden = !t.warning; setText($('inWarn'), t.warning || '');
    $('inAccept').disabled = !t.spaceOk || !!t.warning;
    if (!d.open) d.showModal();
  }
  $('inAccept').addEventListener('click', () => api('POST', `/transfers/${state.shownIncoming}/accept`, { trust: $('inTrust').checked }).catch((e) => toast(e.message, 'error')));
  $('inDecline').addEventListener('click', () => api('POST', `/transfers/${state.shownIncoming}/decline`).catch(() => { }));
  $('incomingDialog').addEventListener('cancel', (e) => e.preventDefault());

  // ------------------------------------------------------------ QR, add, settings
  function drawQr() {
    const s = state.snap; if (!s) return;
    const sel = $('qrAddr');
    const addrs = s.self.addresses;
    sel.innerHTML = addrs.map((a) => `<option value="${a}">${a}:${s.self.port}</option>`).join('');
    if (state.qrAddr && addrs.includes(state.qrAddr)) sel.value = state.qrAddr;
    sel.hidden = addrs.length <= 1;
    const addr = sel.value || addrs[0];
    setText($('qrTitle'), s.self.name);
    if (!addr) { $('qrBox').innerHTML = '<span class="peers-empty">Not connected</span>'; return; }
    const code = `CFS:${addr}:${s.self.port};N:${s.self.name.replace(/[\;]/g, (c) => '\\' + c)};`;
    const qr = qrcode(0, 'M'); qr.addData(code); qr.make();
    // Always dark on white: phone scanners can't read inverted codes.
    $('qrBox').innerHTML = qr.createSvgTag({ cellSize: 6, margin: 3, scalable: true });
  }
  $('qrBtn').addEventListener('click', () => { drawQr(); open($('qrDialog')); });
  $('qrAddr').addEventListener('change', (e) => { state.qrAddr = e.target.value; drawQr(); });

  $('addBtn').addEventListener('click', () => { $('addAddr').value = ''; $('addErr').hidden = true; open($('addDialog')); setTimeout(() => $('addAddr').focus(), 30); });
  $('addForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const address = $('addAddr').value.trim(); if (!address) return;
    busy($('addSubmit'), async () => {
      try {
        const p = await api('POST', '/peers/add', { address });
        state.selected.add(p.id); store.set('selected', JSON.stringify([...state.selected]));
        $('addDialog').close(); toast(`Added ${p.name}`);
      } catch { $('addErr').hidden = false; }
    });
  });

  $('settingsBtn').addEventListener('click', () => {
    const s = state.snap?.settings; if (!s) return;
    $('sName').value = s.deviceName; $('sDiscoverable').checked = s.discoverable;
    setText($('sSaveDir'), shortPath(s.saveDir)); $('sSaveDir').title = s.saveDir;
    $('sOrganize').checked = s.organizeByType; $('sAsk').checked = s.askBeforeReceive; $('sConflict').value = s.conflict;
    $('sOpenDone').checked = s.openFolderWhenDone; $('sStreams').value = String(s.streams); $('sVerify').checked = s.verify;
    $('sStartup').checked = s.startWithWindows; $('sTray').checked = s.closeToTray;
    const names = new Map(state.snap.peers.map((p) => [p.id, p.name]));
    $('sTrustedRow').hidden = !s.trusted.length;
    $('sTrusted').innerHTML = s.trusted.map((id) => `<button type="button" class="btn btn-sm" data-untrust="${id}" title="Remove">${esc(names.get(id) || id.slice(0, 8))} ${icon('x')}</button>`).join(' ');
    open($('settingsDialog'));
  });
  $('sTrusted').addEventListener('click', async (e) => {
    const b = e.target.closest('[data-untrust]'); if (!b) return;
    await api('POST', `/peers/${b.dataset.untrust}/trust`, { trusted: false }); b.remove();
  });
  $('sPickSave').addEventListener('click', async () => { const d = await api('POST', '/host/pick-save-dir'); if (d) { setText($('sSaveDir'), shortPath(d)); $('sSaveDir').title = d; } });
  function shortPath(p) {
    const parts = p.split(/[\\/]/).filter(Boolean);
    return parts.length <= 3 ? p : '…' + (p.includes('\\') ? '\\' : '/') + parts.slice(-3).join(p.includes('\\') ? '\\' : '/');
  }
  $('settingsForm').addEventListener('submit', (e) => {
    e.preventDefault();
    if (!$('sName').value.trim()) { $('sName').focus(); return; }
    busy($('sSave'), async () => {
      await api('POST', '/settings', {
        deviceName: $('sName').value.trim(), discoverable: $('sDiscoverable').checked, organizeByType: $('sOrganize').checked,
        askBeforeReceive: $('sAsk').checked, conflict: $('sConflict').value, openFolderWhenDone: $('sOpenDone').checked,
        streams: +$('sStreams').value, verify: $('sVerify').checked, startWithWindows: $('sStartup').checked, closeToTray: $('sTray').checked,
      });
      $('settingsDialog').close(); toast('Settings saved');
    });
  });

  // ------------------------------------------------------------ dialogs
  function open(d) { if (!d.open) d.showModal(); }
  document.querySelectorAll('dialog').forEach((d) => d.addEventListener('click', (e) => {
    if (e.target.closest('[data-close]')) d.close();
    else if (e.target === d && d.id !== 'incomingDialog') d.close();
  }));
  function confirm(title, ok) {
    const d = $('confirmDialog');
    setText($('cdTitle'), title); setText($('cdOk'), ok);
    return new Promise((resolve) => {
      let r = false;
      const f = d.querySelector('form');
      const onSubmit = (e) => { e.preventDefault(); r = true; d.close(); };
      f.addEventListener('submit', onSubmit, { once: true });
      d.addEventListener('close', () => { f.removeEventListener('submit', onSubmit); resolve(r); }, { once: true });
      open(d);
    });
  }

  applyTheme(state.theme, false);
  renderSend();
  banners();
  connect();
})();
