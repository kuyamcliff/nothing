(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const MB = 1024 * 1024, GB = 1024 * MB;
  const PENDING_TIMEOUT = 12000;

  const store = {
    get(k, d) { try { return localStorage.getItem('clipfx.' + k) ?? d; } catch { return d; } },
    set(k, v) { try { localStorage.setItem('clipfx.' + k, v); } catch { } },
  };

  const state = {
    snap: null,
    connected: false,
    everConnected: false,
    pending: new Map(),       // id -> { blocked, at }
    rows: new Map(),          // id -> row record
    iconFailed: new Set(),
    search: '',
    sort: store.get('sort', 'used'),
    showSystem: null,         // local override until server confirms
    theme: store.get('theme', 'system'),
    themeTouched: false,
    dialogApp: null,
  };

  // ---------------------------------------------------------------- utils

  const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];
  function fmtBytes(n) {
    if (!(n > 0)) return '0 B';
    let i = 0;
    while (n >= 1024 && i < UNITS.length - 1) { n /= 1024; i++; }
    const d = i === 0 ? 0 : n < 10 ? 2 : n < 100 ? 1 : 0;
    return n.toFixed(d) + ' ' + UNITS[i];
  }
  const fmtRate = (n) => fmtBytes(n) + '/s';

  function fmtDate(iso, withTime) {
    if (!iso) return '—';
    const d = new Date(iso);
    const opts = { month: 'short', day: 'numeric' };
    if (d.getFullYear() !== new Date().getFullYear()) opts.year = 'numeric';
    let s = d.toLocaleDateString(undefined, opts);
    if (withTime && (d.getHours() || d.getMinutes())) s += ', ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
    return s;
  }

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const icon = (name, cls = '') => `<svg class="i ${cls}"><use href="#i-${name}"/></svg>`;
  const setText = (el, v) => { if (el.textContent !== v) el.textContent = v; };
  const setAttr = (el, k, v) => { if (el.getAttribute(k) !== v) el.setAttribute(k, v); };
  const toggleClass = (el, c, on) => { if (el.classList.contains(c) !== on) el.classList.toggle(c, on); };

  function kindIcon(kind) {
    switch (kind) {
      case 'Service': return 'server';
      case 'System': return 'cpu';
      case 'Packaged': return 'package';
      case 'Unknown': return 'help';
      default: return 'app';
    }
  }
  const kindLabel = { Program: 'Program', Service: 'Windows service', System: 'Windows kernel', Packaged: 'Store app', Unknown: 'Unknown' };

  async function api(method, path, body) {
    const res = await fetch('/api' + path, {
      method,
      headers: Object.assign({ 'X-ClipFx': '1' }, body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (!res.ok) {
      let msg = '';
      try { msg = (await res.json()).error || ''; } catch { }
      throw new Error(msg || (res.status === 404 ? 'No longer available.' : 'Something went wrong. Try again.'));
    }
  }

  async function withLoading(btn, fn) {
    if (btn.classList.contains('loading')) return;
    btn.classList.add('loading');
    try { return await fn(); }
    finally { btn.classList.remove('loading'); }
  }

  function toast(msg, kind) {
    const el = document.createElement('div');
    el.className = 'toast' + (kind === 'error' ? ' error' : '');
    el.innerHTML = icon(kind === 'error' ? 'alert' : 'check') + '<span></span>';
    el.lastChild.textContent = msg;
    $('toasts').appendChild(el);
    setTimeout(() => el.remove(), kind === 'error' ? 4200 : 2400);
  }

  // ---------------------------------------------------------------- theme

  const media = window.matchMedia('(prefers-color-scheme: dark)');
  const isDark = () => state.theme === 'dark' || (state.theme === 'system' && media.matches);

  function applyTheme(t, persist) {
    state.theme = t;
    store.set('theme', t);
    if (t === 'system') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', t);
    document.querySelectorAll('#themeSeg button').forEach((b) => setAttr(b, 'aria-checked', String(b.dataset.themeValue === t)));
    try { window.chrome?.webview?.postMessage({ type: 'theme', dark: isDark() }); } catch { }
    drawSpark();
    if (persist) {
      state.themeTouched = true;
      api('POST', '/settings', { theme: t }).catch(() => { });
    }
  }
  media.addEventListener('change', () => applyTheme(state.theme, false));

  // ---------------------------------------------------------------- stream

  let es = null, lastMsg = 0;
  function connect() {
    if (es) es.close();
    es = new EventSource('/api/stream');
    es.onmessage = (e) => {
      lastMsg = Date.now();
      if (!state.connected) { state.connected = true; state.everConnected = true; }
      try { render(JSON.parse(e.data)); } catch (err) { console.error(err); }
    };
    es.onerror = () => {
      state.connected = false;
      renderBanners();
      if (es.readyState === EventSource.CLOSED) setTimeout(connect, 1500);
    };
  }
  setInterval(() => {
    if (state.connected && Date.now() - lastMsg > 5000) {
      state.connected = false;
      renderBanners();
      connect();
    }
    expirePending();
  }, 1000);

  // ---------------------------------------------------------------- render

  function render(snap) {
    const first = !state.snap;
    state.snap = snap;
    if (first) {
      $('app').removeAttribute('aria-busy');
      $('simChip').hidden = !snap.status.simulated;
      if (!state.themeTouched && snap.settings.theme !== state.theme) applyTheme(snap.settings.theme, false);
    }
    if (state.showSystem !== null && state.showSystem === snap.settings.showSystem) state.showSystem = null;
    settlePending(snap);
    renderSummary(snap);
    renderBanners();
    syncSystemSwitch();
    renderRows(snap);
    if (state.dialogApp) renderAppDialog();
  }

  function renderSummary(snap) {
    const o = snap.overall;
    const used = $('usedValue');
    if (o.limit) {
      used.innerHTML = `${esc(fmtBytes(o.total))}<span class="of"> / ${esc(fmtBytes(o.limit))}</span>`;
    } else {
      setText(used, fmtBytes(o.total));
    }
    $('usedMeter').hidden = !o.limit;
    if (o.limit) {
      const pct = Math.min(100, (o.total / o.limit) * 100);
      const fill = $('usedMeterFill');
      fill.style.width = pct + '%';
      toggleClass(fill, 'warn', pct >= 80 && pct < 100);
      toggleClass(fill, 'full', pct >= 100);
    }
    $('usedSub').innerHTML = `<span class="pair">${icon('arrow-down')}${esc(fmtBytes(o.received))}</span><span class="pair">${icon('arrow-up')}${esc(fmtBytes(o.sent))}</span>`;
    setText($('overallLimitBtn').lastElementChild, o.limit ? 'Edit limit' : 'Set limit');

    setText($('rateDown'), fmtRate(o.down));
    setText($('rateUp'), fmtRate(o.up));
    drawSpark();

    const p = snap.period;
    setText($('periodStart'), fmtDate(p.start, true));
    setText($('periodNext'), p.mode === 'never' ? 'Never' : fmtDate(p.nextReset));
  }

  function renderBanners() {
    const snap = state.snap;
    const items = [];
    if (!state.connected) {
      items.push(state.everConnected
        ? { kind: 'muted', spin: true, msg: 'Reconnecting' }
        : { kind: 'muted', spin: true, msg: 'Connecting' });
    }
    if (snap) {
      const s = snap.status;
      if (snap.overall.limitReached) {
        items.push({
          kind: 'danger', icon: 'ban', msg: 'Data limit reached. All internet is blocked.',
          actions: [['Edit limit', 'edit-overall'], ['Reset usage', 'reset-all']],
        });
      }
      if (!s.firewallEnabled) items.push({ kind: 'danger', icon: 'shield', msg: 'Windows Firewall is off. Blocking is not active.', actions: [['Turn on', 'fw-on', true]] });
      if (s.firewallError) items.push({ kind: 'danger', icon: 'alert', msg: s.firewallError });
      if (!s.monitoring) items.push({ kind: 'warn', icon: 'alert', msg: s.monitorError || 'Monitoring is starting' });
    }
    if (!state.connected && state.everConnected) items.splice(1); // stale data: only show the reconnect line
    const html = items.map((b) => `
      <div class="banner banner-${b.kind}">
        ${b.spin ? '<span class="spinner"></span>' : icon(b.icon)}
        <span class="msg">${esc(b.msg)}</span>
        ${b.actions ? `<span class="actions">${b.actions.map(([t, a, primary]) => `<button type="button" class="btn btn-sm${primary ? ' btn-primary' : ''}" data-action="${a}">${esc(t)}</button>`).join('')}</span>` : ''}
      </div>`).join('');
    const el = $('banners');
    if (el.dataset.html !== html) { el.dataset.html = html; el.innerHTML = html; }
  }

  $('banners').addEventListener('click', (e) => {
    const btn = e.target.closest('[data-action]');
    if (!btn) return;
    const a = btn.dataset.action;
    if (a === 'edit-overall') openLimitDialog();
    else if (a === 'reset-all') resetAll();
    else if (a === 'fw-on') withLoading(btn, () => api('POST', '/firewall/enable').then(() => toast('Firewall turned on')).catch((err) => toast(err.message, 'error')));
  });

  // ---------------------------------------------------------------- rows

  function showSystem() {
    return state.showSystem ?? state.snap?.settings.showSystem ?? false;
  }

  function sorted(apps) {
    const q = state.search.trim().toLowerCase();
    const sys = showSystem();
    const list = apps.filter((a) => (sys || !a.isSystem) && (!q || a.name.toLowerCase().includes(q) || (a.path || '').toLowerCase().includes(q)));
    const byName = (a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
    if (state.sort === 'name') list.sort(byName);
    else if (state.sort === 'live') list.sort((a, b) => (b.up + b.down) - (a.up + a.down) || b.total - a.total || byName(a, b));
    else list.sort((a, b) => b.total - a.total || byName(a, b));
    return list;
  }

  function createRow(app) {
    const el = document.createElement('div');
    el.className = 'row';
    el.setAttribute('role', 'row');
    el.tabIndex = 0;
    el.dataset.id = app.id;
    el.innerHTML = `
      <div class="cell-app" role="cell">
        <div class="app-icon"></div>
        <div class="app-text"><span class="app-name"></span><span class="dot"></span><span class="tags"></span></div>
      </div>
      <div class="cell-live" role="cell"><span>${icon('arrow-down')}<b></b></span><span>${icon('arrow-up')}<b></b></span><em>—</em></div>
      <div class="num cell-used" role="cell"></div>
      <div class="cell-limit" role="cell"><button type="button" class="limit-btn" data-act="limit"></button></div>
      <div class="center" role="cell"><button type="button" role="switch" class="switch" data-act="toggle"><span class="knob"></span></button></div>`;
    const r = {
      el, id: app.id,
      icon: el.querySelector('.app-icon'),
      name: el.querySelector('.app-name'),
      dot: el.querySelector('.dot'),
      tags: el.querySelector('.tags'),
      live: el.querySelector('.cell-live'),
      down: el.querySelectorAll('.cell-live b')[0],
      up: el.querySelectorAll('.cell-live b')[1],
      used: el.querySelector('.cell-used'),
      limit: el.querySelector('.limit-btn'),
      sw: el.querySelector('.switch'),
      cache: {},
    };
    fillIcon(r.icon, app);
    return r;
  }

  function fillIcon(container, app) {
    const fallback = icon(kindIcon(app.kind));
    if ((app.kind === 'Program' || app.kind === 'Packaged') && !state.iconFailed.has(app.id) && !state.snap?.status.simulated) {
      const img = new Image();
      img.alt = '';
      img.decoding = 'async';
      img.onerror = () => { state.iconFailed.add(app.id); container.innerHTML = fallback; };
      img.src = `/api/apps/${app.id}/icon`;
      container.replaceChildren(img);
    } else {
      container.innerHTML = fallback;
    }
  }

  function switchState(app) {
    const p = state.pending.get(app.id);
    const blocked = p ? p.blocked : app.blocked;
    const busy = !!p || (app.canBlock && !app.applied);
    return { allowed: !blocked, busy };
  }

  function updateSwitch(sw, app, label) {
    const { allowed, busy } = switchState(app);
    setAttr(sw, 'aria-checked', String(allowed));
    setAttr(sw, 'aria-busy', String(busy));
    setAttr(sw, 'aria-label', (label || app.name) + ' internet');
    setAttr(sw, 'title', !app.canBlock ? 'Can\'t be blocked' : allowed ? 'Allowed' : 'Blocked');
    sw.disabled = !app.canBlock;
  }

  function tagsHtml(app) {
    let h = '';
    if (app.blocked) h += `<span class="tag tag-blocked">${icon('ban')}Blocked</span>`;
    else if (app.limitReached) h += `<span class="tag tag-limit">${icon('alert')}Limit reached</span>`;
    if (app.isSystem) h += '<span class="tag tag-system">System</span>';
    return h;
  }

  function limitHtml(app) {
    if (!app.limit) return `<span class="dash">—</span><span class="add">${icon('pencil')}Set limit</span>`;
    const pct = Math.min(100, (app.total / app.limit) * 100);
    const cls = pct >= 100 ? 'full' : pct >= 80 ? 'warn' : '';
    return `<span class="bar"><i class="${cls}" style="width:${pct.toFixed(1)}%"></i></span><span class="val">${esc(fmtBytes(app.limit))}</span>`;
  }

  function updateRow(r, app) {
    const c = r.cache;
    if (c.name !== app.name) { c.name = app.name; setText(r.name, app.name); r.el.title = app.path || app.name; }
    toggleClass(r.dot, 'on', app.active);
    const tags = tagsHtml(app);
    if (c.tags !== tags) { c.tags = tags; r.tags.innerHTML = tags; }

    const idle = app.up < 1 && app.down < 1;
    toggleClass(r.live, 'idle', idle);
    if (!idle) {
      setText(r.down, fmtRate(app.down));
      setText(r.up, fmtRate(app.up));
    }

    setText(r.used, fmtBytes(app.total));

    const lim = limitHtml(app);
    if (c.lim !== lim) {
      c.lim = lim;
      r.limit.innerHTML = lim;
      toggleClass(r.limit, 'none', !app.limit);
      setAttr(r.limit, 'aria-label', app.limit ? `Limit ${fmtBytes(app.limit)}` : 'Set limit');
      setAttr(r.limit, 'title', app.limit ? `${fmtBytes(app.total)} of ${fmtBytes(app.limit)}` : '');
    }
    updateSwitch(r.sw, app);
    toggleClass(r.el, 'is-blocked', app.effectiveBlocked);
  }

  function renderRows(snap) {
    const container = $('rows');
    const ids = new Set(snap.apps.map((a) => a.id));
    for (const [id, r] of state.rows) {
      if (!ids.has(id)) { r.el.remove(); state.rows.delete(id); }
    }
    const list = sorted(snap.apps);
    const els = [];
    for (const app of list) {
      let r = state.rows.get(app.id);
      if (!r) { r = createRow(app); state.rows.set(app.id, r); }
      updateRow(r, app);
      els.push(r.el);
    }
    // Reorder only when the order changed, so hover and focus stay put.
    const current = Array.from(container.children);
    const same = current.length === els.length && current.every((el, i) => el === els[i]);
    if (!same) {
      const focused = document.activeElement;
      container.replaceChildren(...els);
      if (focused && container.contains(focused)) focused.focus({ preventScroll: true });
    }
    const empty = els.length === 0;
    $('empty').hidden = !empty;
    if (empty) setText($('emptyText'), snap.apps.length === 0 ? 'No network activity yet' : 'No matches');
  }

  function renderSkeleton() {
    const rows = [];
    for (let i = 0; i < 7; i++) {
      rows.push(`<div class="row skeleton" aria-hidden="true">
        <div class="cell-app"><span class="skel skel-icon"></span><span class="skel skel-name" style="width:${110 + ((i * 37) % 90)}px"></span></div>
        <div><span class="skel skel-line" style="width:120px"></span></div>
        <div class="num"><span class="skel skel-line" style="width:60px"></span></div>
        <div><span class="skel skel-line" style="width:100px"></span></div>
        <div class="center"><span class="skel skel-sw"></span></div>
      </div>`);
    }
    $('rows').innerHTML = rows.join('');
  }

  $('rows').addEventListener('click', (e) => {
    const rowEl = e.target.closest('.row');
    if (!rowEl || rowEl.classList.contains('skeleton')) return;
    const app = findApp(rowEl.dataset.id);
    if (!app) return;
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (act === 'toggle') { e.stopPropagation(); toggleApp(app); return; }
    openAppDialog(app.id, act === 'limit');
  });
  $('rows').addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' || !e.target.classList.contains('row')) return;
    openAppDialog(e.target.dataset.id, false);
  });

  const findApp = (id) => state.snap?.apps.find((a) => a.id === id);

  // ---------------------------------------------------------------- blocking

  async function toggleApp(app) {
    if (!app.canBlock || state.pending.has(app.id)) return;
    const blocked = !app.blocked;
    state.pending.set(app.id, { blocked, at: Date.now() });
    refreshApp(app.id);
    try {
      await api('POST', `/apps/${app.id}/blocked`, { blocked });
    } catch (err) {
      state.pending.delete(app.id);
      refreshApp(app.id);
      toast(err.message, 'error');
    }
  }

  function settlePending(snap) {
    for (const [id, p] of state.pending) {
      const app = snap.apps.find((a) => a.id === id);
      if (!app) { state.pending.delete(id); continue; }
      if (app.blocked === p.blocked && app.applied) state.pending.delete(id);
    }
  }

  function expirePending() {
    for (const [id, p] of state.pending) {
      if (Date.now() - p.at > PENDING_TIMEOUT) {
        state.pending.delete(id);
        refreshApp(id);
        toast('The firewall did not confirm the change.', 'error');
      }
    }
  }

  function refreshApp(id) {
    const app = findApp(id);
    if (!app) return;
    const r = state.rows.get(id);
    if (r) updateSwitch(r.sw, app);
    if (state.dialogApp === id) renderAppDialog();
  }

  // ---------------------------------------------------------------- dialogs

  function openDialog(d) {
    if (!d.open) d.showModal();
  }
  document.querySelectorAll('dialog').forEach((d) => {
    d.addEventListener('click', (e) => {
      if (e.target === d) d.close();
      if (e.target.closest('[data-close]')) d.close();
    });
  });

  function setUnit(seg, unit) {
    seg.querySelectorAll('button').forEach((b) => setAttr(b, 'aria-checked', String(b.dataset.unit === unit)));
    seg.dataset.unit = unit;
  }
  document.querySelectorAll('#adUnit, #ldUnit').forEach((seg) => {
    seg.addEventListener('click', (e) => {
      const b = e.target.closest('button');
      if (b) setUnit(seg, b.dataset.unit);
    });
  });

  function fillLimit(input, seg, bytes, defaultUnit) {
    if (!bytes) { input.value = ''; setUnit(seg, defaultUnit); return; }
    const unit = bytes >= GB && (bytes % (GB / 100) === 0 || bytes >= 10 * GB) ? 'GB' : 'MB';
    const v = bytes / (unit === 'GB' ? GB : MB);
    input.value = String(Math.round(v * 100) / 100);
    setUnit(seg, unit);
  }

  function readLimit(input, seg) {
    const raw = input.value.trim();
    if (raw === '') return { ok: true, bytes: null };
    const v = Number(raw);
    if (!isFinite(v) || v <= 0) return { ok: false };
    const bytes = Math.round(v * (seg.dataset.unit === 'GB' ? GB : MB));
    return bytes > 0 ? { ok: true, bytes } : { ok: false };
  }

  function markInvalid(input) {
    input.classList.add('invalid');
    input.focus();
    setTimeout(() => input.classList.remove('invalid'), 1600);
  }

  // App dialog
  const ad = $('appDialog');

  function openAppDialog(id, focusLimit) {
    const app = findApp(id);
    if (!app) return;
    state.dialogApp = id;
    ad.dataset.limitDirty = '';
    fillIcon($('adIcon'), app);
    fillLimit($('adLimit'), $('adUnit'), app.limit, 'MB');
    renderAppDialog();
    openDialog(ad);
    if (focusLimit) setTimeout(() => $('adLimit').focus(), 30);
  }
  ad.addEventListener('close', () => { state.dialogApp = null; });
  $('adLimit').addEventListener('input', () => { ad.dataset.limitDirty = '1'; });

  function renderAppDialog() {
    const app = findApp(state.dialogApp);
    if (!app) { ad.close(); return; }
    setText($('adName'), app.name);
    const tags = tagsHtml(app);
    if ($('adTags').innerHTML !== tags) $('adTags').innerHTML = tags;
    updateSwitch($('adSwitch'), app, app.name);
    setText($('adDown'), fmtBytes(app.received));
    setText($('adUp'), fmtBytes(app.sent));
    setText($('adTotal'), fmtBytes(app.total));
    setText($('adLive'), fmtRate(app.up + app.down));
    setText($('adKind'), kindLabel[app.kind] || app.kind);
    setText($('adFirst'), fmtDate(app.firstSeen, true));
    setText($('adPath'), app.path || '—');
    $('adCopy').hidden = !app.path;
    $('adClear').hidden = !app.limit;
    $('adMeter').hidden = !app.limit;
    if (app.limit) {
      const pct = Math.min(100, (app.total / app.limit) * 100);
      const fill = $('adMeterFill');
      fill.style.width = pct + '%';
      toggleClass(fill, 'warn', pct >= 80 && pct < 100);
      toggleClass(fill, 'full', pct >= 100);
    }
    if (!ad.dataset.limitDirty && document.activeElement !== $('adLimit')) fillLimit($('adLimit'), $('adUnit'), app.limit, $('adUnit').dataset.unit || 'MB');
    const forget = $('adForget');
    forget.disabled = app.effectiveBlocked;
    forget.title = app.effectiveBlocked ? 'Allow it first' : '';
  }

  $('adSwitch').addEventListener('click', () => {
    const app = findApp(state.dialogApp);
    if (app) toggleApp(app);
  });

  $('appForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const app = findApp(state.dialogApp);
    if (!app) return;
    const lim = readLimit($('adLimit'), $('adUnit'));
    if (!lim.ok) { markInvalid($('adLimit')); return; }
    withLoading($('adSave'), async () => {
      try {
        await api('POST', `/apps/${app.id}/limit`, { bytes: lim.bytes });
        ad.dataset.limitDirty = '';
        $('adLimit').blur();
        toast(lim.bytes ? 'Limit saved' : 'Limit removed');
      } catch (err) { toast(err.message, 'error'); }
    });
  });

  $('adClear').addEventListener('click', () => {
    const app = findApp(state.dialogApp);
    if (!app) return;
    withLoading($('adClear'), async () => {
      try {
        await api('POST', `/apps/${app.id}/limit`, { bytes: null });
        $('adLimit').value = '';
        ad.dataset.limitDirty = '';
        toast('Limit removed');
      } catch (err) { toast(err.message, 'error'); }
    });
  });

  $('adReset').addEventListener('click', async () => {
    const app = findApp(state.dialogApp);
    if (!app) return;
    if (!(await confirmAction(`Reset usage for ${app.name}?`, 'Reset'))) return;
    withLoading($('adReset'), async () => {
      try { await api('POST', `/apps/${app.id}/reset`); toast('Usage reset'); }
      catch (err) { toast(err.message, 'error'); }
    });
  });

  $('adForget').addEventListener('click', async () => {
    const app = findApp(state.dialogApp);
    if (!app) return;
    if (!(await confirmAction(`Remove ${app.name} from the list?`, 'Remove'))) return;
    try { await api('DELETE', `/apps/${app.id}`); ad.close(); toast('Removed'); }
    catch (err) { toast(err.message, 'error'); }
  });

  $('adCopy').addEventListener('click', async () => {
    const app = findApp(state.dialogApp);
    if (!app?.path) return;
    try {
      await navigator.clipboard.writeText(app.path);
    } catch {
      const r = document.createRange(); r.selectNodeContents($('adPath'));
      const s = getSelection(); s.removeAllRanges(); s.addRange(r); document.execCommand('copy'); s.removeAllRanges();
    }
    const use = $('adCopy').querySelector('use');
    use.setAttribute('href', '#i-check');
    setTimeout(() => use.setAttribute('href', '#i-copy'), 1200);
  });

  // Overall limit dialog
  function openLimitDialog() {
    const o = state.snap?.overall;
    fillLimit($('ldValue'), $('ldUnit'), o?.limit, 'GB');
    $('ldClear').hidden = !o?.limit;
    openDialog($('limitDialog'));
    setTimeout(() => { $('ldValue').focus(); $('ldValue').select(); }, 30);
  }
  $('overallLimitBtn').addEventListener('click', openLimitDialog);

  $('limitForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const lim = readLimit($('ldValue'), $('ldUnit'));
    if (!lim.ok) { markInvalid($('ldValue')); return; }
    withLoading($('ldSave'), async () => {
      try {
        await api('POST', '/overall/limit', { bytes: lim.bytes });
        $('limitDialog').close();
        toast(lim.bytes ? 'Limit saved' : 'Limit removed');
      } catch (err) { toast(err.message, 'error'); }
    });
  });

  $('ldClear').addEventListener('click', () => withLoading($('ldClear'), async () => {
    try {
      await api('POST', '/overall/limit', { bytes: null });
      $('limitDialog').close();
      toast('Limit removed');
    } catch (err) { toast(err.message, 'error'); }
  }));

  // Reset all
  async function resetAll() {
    if (!(await confirmAction('Reset all usage to zero?', 'Reset'))) return;
    try { await api('POST', '/usage/reset'); toast('Usage reset'); }
    catch (err) { toast(err.message, 'error'); }
  }
  $('resetAllBtn').addEventListener('click', resetAll);

  // Settings dialog
  function syncDayField() { $('sdDayField').hidden = $('sdPeriod').value !== 'monthly'; }
  $('sdPeriod').addEventListener('change', syncDayField);
  $('settingsBtn').addEventListener('click', () => {
    const s = state.snap?.settings;
    $('sdPeriod').value = s?.resetPeriod || 'monthly';
    $('sdDay').value = s?.resetDay || 1;
    syncDayField();
    openDialog($('settingsDialog'));
  });
  $('settingsForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const period = $('sdPeriod').value;
    const day = Number($('sdDay').value);
    if (period === 'monthly' && !(Number.isInteger(day) && day >= 1 && day <= 28)) { markInvalid($('sdDay')); return; }
    withLoading($('sdSave'), async () => {
      try {
        await api('POST', '/settings', { resetPeriod: period, resetDay: period === 'monthly' ? day : undefined });
        $('settingsDialog').close();
        toast('Settings saved');
      } catch (err) { toast(err.message, 'error'); }
    });
  });

  // Confirm dialog
  function confirmAction(title, ok) {
    const d = $('confirmDialog');
    setText($('cdTitle'), title);
    setText($('cdOk'), ok);
    return new Promise((resolve) => {
      let result = false;
      const onSubmit = (e) => { e.preventDefault(); result = true; d.close(); };
      d.querySelector('form').addEventListener('submit', onSubmit, { once: true });
      d.addEventListener('close', () => {
        d.querySelector('form').removeEventListener('submit', onSubmit);
        resolve(result);
      }, { once: true });
      openDialog(d);
      $('cdOk').focus();
    });
  }

  // ---------------------------------------------------------------- toolbar

  $('search').addEventListener('input', (e) => { state.search = e.target.value; if (state.snap) renderRows(state.snap); });
  $('search').addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { e.target.value = ''; state.search = ''; if (state.snap) renderRows(state.snap); e.target.blur(); }
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === '/' && !e.ctrlKey && !e.metaKey && !/INPUT|SELECT|TEXTAREA/.test(document.activeElement?.tagName || '') && !document.querySelector('dialog[open]')) {
      e.preventDefault();
      $('search').focus();
    }
  });

  $('sort').value = ['used', 'live', 'name'].includes(state.sort) ? state.sort : 'used';
  $('sort').addEventListener('change', (e) => {
    state.sort = e.target.value;
    store.set('sort', state.sort);
    if (state.snap) renderRows(state.snap);
  });

  const sysSwitch = $('showSystem');
  function syncSystemSwitch() { setAttr(sysSwitch, 'aria-checked', String(showSystem())); }
  sysSwitch.closest('label').addEventListener('click', (e) => {
    e.preventDefault();
    const next = !showSystem();
    state.showSystem = next;
    syncSystemSwitch();
    if (state.snap) renderRows(state.snap);
    api('POST', '/settings', { showSystem: next }).catch((err) => { state.showSystem = null; syncSystemSwitch(); toast(err.message, 'error'); });
  });

  $('themeSeg').addEventListener('click', (e) => {
    const b = e.target.closest('button');
    if (b) applyTheme(b.dataset.themeValue, true);
  });

  // ---------------------------------------------------------------- sparkline

  function drawSpark() {
    const canvas = $('spark');
    const hist = state.snap?.overall.history;
    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (!w || !h) return;
    const dpr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    const css = getComputedStyle(document.documentElement);
    const cDown = css.getPropertyValue('--spark-down').trim();
    const cUp = css.getPropertyValue('--spark-up').trim();
    const cGrid = css.getPropertyValue('--border').trim();
    ctx.strokeStyle = cGrid; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(0, h - .5); ctx.lineTo(w, h - .5); ctx.stroke();
    if (!hist || hist.length < 2) return;
    const max = Math.max(4096, ...hist.map((p) => Math.max(p.down, p.up))) * 1.15;
    const step = w / (hist.length - 1);
    const y = (v) => h - 2 - (v / max) * (h - 6);
    const line = (key) => { ctx.beginPath(); hist.forEach((p, i) => (i ? ctx.lineTo(i * step, y(p[key])) : ctx.moveTo(0, y(p[key])))); };

    line('down');
    ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath();
    ctx.globalAlpha = .1; ctx.fillStyle = cDown; ctx.fill(); ctx.globalAlpha = 1;
    line('down'); ctx.strokeStyle = cDown; ctx.lineWidth = 1.6; ctx.lineJoin = 'round'; ctx.stroke();
    line('up'); ctx.strokeStyle = cUp; ctx.lineWidth = 1.4; ctx.stroke();
  }
  window.addEventListener('resize', drawSpark);

  // ---------------------------------------------------------------- boot

  applyTheme(state.theme, false);
  syncSystemSwitch();
  renderSkeleton();
  renderBanners();
  connect();
})();
