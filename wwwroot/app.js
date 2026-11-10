'use strict';

let pairingKey = '';
try { pairingKey = sessionStorage.getItem('flow-pairing-key') || ''; } catch { }
if (location.hash.startsWith('#pair=')) {
  pairingKey = location.hash.slice(6);
  try { sessionStorage.setItem('flow-pairing-key', pairingKey); } catch { }
  history.replaceState(null, '', location.pathname + '#overview');
}
const pairingHeaders = () => pairingKey ? { 'X-Flow-Key': pairingKey } : {};

const $ = id => document.getElementById(id);
const list = value => Array.isArray(value) ? value : [];
const num = value => Number.isFinite(value) ? Math.max(0, value) : 0;
const total = app => num(app.receivedBytes) + num(app.sentBytes);
const rate = app => num(app.downloadBps) + num(app.uploadBps);
const NS = 'http://www.w3.org/2000/svg';
let state = null, latest = null, paused = false, stopped = false, polling = false;
let pollTimer, lastSuccess = null, pollError = '', historyData = null, historyRange = 'day';
let historyRequest = 0, historyLoading = false, historyError = '', budgetDirty = false;
let historyFrom = '', historyTo = '', historyRenderedKey = '';
let selectedApp = null, adminDismissed = false, routerBusy = false;
let page = 'overview';
const pages = {
  overview: ['Network overview', 'A clear view of what moves through your laptop.'],
  applications: ['Applications', 'See which programs use your connection.'],
  connections: ['Connections', 'Inspect the endpoints your applications are talking to.'],
  diagnostics: ['Network diagnostics', 'Measure real-time latency, jitter, packet loss, DNS, and speed.'],
  controls: ['Application controls', 'Manage daily allowances, network access, and running programs.'],
  history: ['Usage history', 'Understand your locally collected network usage.'],
  network: ['Network adapters', 'Your physical and virtual connections, in one place.'],
  router: ['Router', 'A separate, read-only view of your whole network.'],
  settings: ['Settings', 'Manage your budget and understand collection.']
};

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value !== null && value !== undefined) node.setAttribute(key, String(value));
  }
  for (const child of children.flat()) {
    if (child !== null && child !== undefined) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}
function svg(tag, attrs = {}, ...children) {
  const node = document.createElementNS(NS, tag);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
  for (const child of children) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  return node;
}
function icon(name) { return svg('svg', { class: 'icon', 'aria-hidden': 'true' }, svg('use', { href: '#i-' + name })); }
function put(id, ...children) {
  const container = $(id), nodes = children.flat();
  if ((id === 'applications-table' || id === 'overview-apps') && nodes.length === 1 && reconcileApps(container, nodes[0])) return;
  container.replaceChildren(...nodes);
}
function reconcileApps(container, next) {
  const body = container.querySelector('tbody'), incoming = next.querySelector?.('tbody');
  if (!body || !incoming) return false;
  const rows = new Map(Array.from(body.rows, row => [row.querySelector('[data-app-id]')?.dataset.appId, row]));
  const keep = new Set();
  Array.from(incoming.rows).forEach((fresh, index) => {
    const id = fresh.querySelector('[data-app-id]')?.dataset.appId;
    let row = rows.get(id);
    if (row) {
      // Keep buttons alive during sampling, including between pointer-down and click.
      row.querySelector('.app-secondary').textContent = fresh.querySelector('.app-secondary').textContent;
      for (let i = 1; i < row.cells.length; i++) {
        if (row.cells[i].classList.contains('app-actions')) {
          row.cells[i].querySelectorAll('[data-row-action]').forEach(button => {
            const nextButton = fresh.cells[i].querySelector('[data-row-action="' + button.dataset.rowAction + '"]');
            button.disabled = nextButton.disabled; button.title = nextButton.title;
          });
        } else row.cells[i].replaceChildren(...fresh.cells[i].childNodes);
      }
    } else row = fresh;
    keep.add(row);
    if (body.children[index] !== row) body.insertBefore(row, body.children[index] || null);
  });
  Array.from(body.rows).forEach(row => { if (!keep.has(row)) row.remove(); });
  return true;
}
function text(id, value) { $(id).textContent = value ?? '—'; }
function bytes(value, perSecond = false) {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—';
  let amount = num(value), unit = 0;
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  while (amount >= 1000 && unit < units.length - 1) { amount /= 1000; unit++; }
  return amount.toLocaleString(undefined, { maximumFractionDigits: amount < 10 && unit ? 2 : amount < 100 && unit ? 1 : 0 }) + ' ' + units[unit] + (perSecond ? '/s' : '');
}
function count(value) { return value === undefined || value === null ? '—' : num(value).toLocaleString(); }
function time(value, options = { hour: '2-digit', minute: '2-digit', second: '2-digit' }) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleTimeString([], options);
}
function empty(title, detail, name = 'activity') {
  return el('div', { class: 'empty-state' }, el('span', { class: 'empty-icon' }, icon(name)), el('strong', {}, title), el('p', {}, detail));
}
function pending(noun) {
  return empty(stopped ? 'Monitor stopped' : state ? 'No ' + noun + ' yet' : 'Connecting to Flow', stopped ? 'Restart using Start Monitor.cmd.' : state ? 'New activity will appear here as it is collected.' : 'Waiting for the local monitoring service.');
}
function badge(label, kind = '') { return el('span', { class: 'badge ' + kind }, label); }
function message(id, value, kind = '') { text(id, value); $(id).classList.toggle('error', kind === 'error'); $(id).classList.toggle('success', kind === 'success'); }
function metric(label, value, note, name, color = '') {
  return el('div', { class: 'metric' }, el('div', { class: 'metric-label' }, icon(name), label), el('div', { class: 'metric-value ' + color }, value), el('div', { class: 'metric-note' }, note));
}
function table(headers, rows, mobileLayout = '') {
  // Keep column associations in browsers that flatten table semantics for CSS grids.
  if (mobileLayout) {
    for (const row of rows) {
      row.setAttribute('role', 'row');
      Array.from(row.cells).forEach((td, i) => {
        td.setAttribute('role', 'cell');
        td.dataset.label = headers[i];
      });
    }
  }
  const head = el('tr', { role: 'row' }, headers.map((h, i) => el('th', { scope: 'col', role: 'columnheader', class: i > 0 ? 'number' : '' }, h)));
  return el('div', { class: 'table-scroll ' + mobileLayout, tabindex: '0', role: 'region', 'aria-label': headers[0] + ' table' },
    el('table', { role: 'table' }, el('thead', { role: 'rowgroup' }, head), el('tbody', { role: 'rowgroup' }, rows)));
}
function cell(value, cls = 'number') { return el('td', { class: cls }, value); }
function details(entries) {
  return el('dl', { class: 'details-list' }, entries.flatMap(([name, value]) => [el('dt', {}, name), el('dd', {}, value === '' || value === null || value === undefined ? 'Not reported' : value)]));
}
async function api(url, options = {}) {
  const response = await fetch(url, { cache: 'no-store', ...options, signal: options.signal || AbortSignal.timeout(12000), headers: { ...pairingHeaders(), ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(options.method === 'POST' ? { 'X-Flow-Request': '1' } : {}), ...options.headers } });
  if (response.status === 403 && !['localhost', '127.0.0.1', '[::1]'].includes(location.hostname)) throw new Error('Open a fresh private phone link from Settings on the laptop. Only verified process termination is permitted from a paired phone.');
  let data;
  try { data = await response.json(); } catch { throw new Error('The service returned an unreadable response.'); }
  if (!response.ok) throw new Error(data.message || 'Request failed (' + response.status + ').');
  return data;
}
function showHealth() {
  const source = latest || state;
  const remote = source?.access?.isRemote;
  $('remote-alert').hidden = !remote;
  if (remote) ['budget-save', 'budget-input', 'router-connect', 'router-username', 'router-password', 'router-disconnect', 'stop-monitor'].forEach(id => { $(id).disabled = true; });
  const mobileUrl = source?.access?.mobileUrl;
  $('mobile-link').value = mobileUrl || '';
  $('mobile-link').hidden = !mobileUrl;
  $('copy-mobile-link').hidden = !mobileUrl;
  text('mobile-help', remote ? 'You are controlling this laptop through a private link. You can end verified programs; router login, network rules and startup changes stay on the laptop.' : mobileUrl ? 'Open this private control link on a phone on the same trusted Wi-Fi. It permits ending verified processes. Keep it private. A new link is generated after every restart.' : 'To enable phone access, run Start Mobile Monitor.cmd on this laptop, then return here for a private control link. Use sharing only on trusted Wi-Fi.');
  const collector = source?.collector;
  const failed = !!pollError;
  const warning = collector && collector.status !== 'running';
  $('health').className = 'health ' + (stopped ? '' : failed ? 'error' : warning ? 'warning' : source ? 'good' : '');
  text('health-text', stopped ? 'Stopped' : failed ? 'Reconnecting' : warning ? collector.status === 'needs-admin' ? 'Adapter totals only' : 'Capture unavailable' : source ? 'Live monitoring' : 'Connecting');
  $('connection-alert').hidden = !failed;
  text('connection-alert-detail', failed ? pollError + (lastSuccess ? ' Showing stale data from ' + time(lastSuccess) + '.' : ' Check that Start Monitor.cmd is running.') : '');
  text('last-updated', stopped ? 'Monitor stopped. Restart using Start Monitor.cmd.' : paused ? 'Display paused · collection continues' : failed ? 'Stale · last update ' + (lastSuccess ? time(lastSuccess) : 'unavailable') : lastSuccess ? 'Updated ' + time(lastSuccess) : 'Waiting for local service');
  if (source?.startedAt) {
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(source.startedAt).getTime()) / 1000));
    text('uptime', 'Session ' + (seconds >= 3600 ? Math.floor(seconds / 3600) + 'h ' : '') + Math.floor(seconds / 60) % 60 + 'm');
  }
  $('admin-alert').hidden = !source || source.isAdministrator !== false || adminDismissed;
  if ($('diagnostics-alert')) {
    const warnings = [];
    if (collector?.status === 'error') warnings.push(collector.message || 'Application capture failed. Restart Start Monitor.cmd.');
    if (num(collector?.eventsLost)) warnings.push(count(collector.eventsLost) + ' capture events lost. Application totals may be incomplete.');
    if (source?.diagnostics?.historyError) warnings.push('History: ' + source.diagnostics.historyError);
    if (source?.diagnostics?.connectionError) warnings.push('Connections: ' + source.diagnostics.connectionError);
    $('diagnostics-alert').hidden = !warnings.length;
    text('diagnostics-text', warnings.join(' '));
  }
}
async function poll() {
  if (stopped || polling) return;
  clearTimeout(pollTimer);
  polling = true;
  try {
    const data = await api('/api/state');
    if (!data || !data.summary || !Array.isArray(data.apps) || !Array.isArray(data.series)) throw new Error('The service returned an incomplete snapshot.');
    if (stopped) return;
    latest = data;
    lastSuccess = new Date();
    pollError = '';
    if (!paused) { state = data; render(); }
  } catch (error) { if (!stopped) pollError = error.name === 'TimeoutError' ? 'The local service did not respond in time.' : error.message || 'The local service is unavailable.'; }
  finally { polling = false; showHealth(); if (!stopped) pollTimer = setTimeout(poll, 2000); }
}
function navigate() {
  const next = location.hash.slice(1);
  const previous = page;
  page = Object.hasOwn(pages, next) ? next : 'overview';
  for (const name of Object.keys(pages)) $('page-' + name).hidden = name !== page;
  document.querySelectorAll('[data-page]').forEach(link => {
    if (link.dataset.page === page) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
  });
  text('page-title', pages[page][0]); text('page-description', pages[page][1]);
  text('header-page', page.charAt(0).toUpperCase() + page.slice(1));
  document.title = pages[page][0] + ' · Flow';
  closeNav(); render();
  if (page !== previous && $('menu-toggle').getClientRects().length) {
    $('main').focus({ preventScroll: true });
    window.scrollTo(0, 0);
  }
  if (page === 'history') loadHistory();
}
function syncMenu() {
  const mobile = $('menu-toggle').getClientRects().length > 0;
  const open = mobile ? $('sidebar').classList.contains('open') : !document.body.classList.contains('menu-hidden');
  $('header-menu-toggle').setAttribute('aria-expanded', String(open));
  $('header-menu-toggle').setAttribute('aria-label', open ? 'Hide menu' : 'Show menu');
  $('menu-toggle').setAttribute('aria-expanded', String(mobile && open));
}
function closeNav() { $('sidebar').classList.remove('open'); $('nav-backdrop').hidden = true; syncMenu(); }
function toggleMenu() {
  if ($('menu-toggle').getClientRects().length) {
    const open = !$('sidebar').classList.contains('open');
    $('sidebar').classList.toggle('open', open); $('nav-backdrop').hidden = !open;
    if (open) $('sidebar').querySelector('[aria-current="page"]')?.focus();
  } else {
    document.body.classList.toggle('menu-hidden');
    try { localStorage.setItem('flow-menu-hidden', String(document.body.classList.contains('menu-hidden'))); } catch { }
  }
  syncMenu();
}
document.querySelectorAll('[data-page]').forEach(link => link.addEventListener('click', closeNav));
function render() {
  const focusedAppId = document.activeElement?.dataset.appId;
  text('machine-name', state?.machineName || 'This laptop');
  text('nav-app-count', state ? count(state.summary.activeApps) : '—');
  if (state && !budgetDirty && document.activeElement !== $('budget-input')) $('budget-input').value = state.settings?.budgetGb ?? 0;
  const renderers = { overview: renderOverview, applications: renderApps, connections: renderConnections, diagnostics: renderDiagnostics, history: renderHistory, network: renderNetwork, router: renderRouter, settings: renderSettings, controls: () => window.flowControls?.render() };
  renderers[page]();
  if ($('app-dialog').open) renderDetail();
  showHealth();
  if (focusedAppId) focusApp(focusedAppId);
}

// Page renderers are declared below; polling starts after all event handlers are bound.

function renderOverview() {
  const s = state?.summary;
  put('overview-metrics', metric('Download', bytes(s?.downloadBps, true), 'Across physical adapters', 'down', 'download-text'), metric('Upload', bytes(s?.uploadBps, true), 'Across physical adapters', 'up', 'upload-text'), metric('Today’s usage', s ? bytes(num(s.todayReceived) + num(s.todaySent)) : '—', 'Physical-adapter traffic', 'history'), metric('Active applications', count(s?.activeApps), count(s?.connectionCount) + ' open connections', 'apps'));
  renderLiveChart();
  const adapters = list(state?.interfaces);
  const adapter = adapters.find(a => a.isDefault) || adapters.find(a => a.status === 'Up');
  if (adapter) {
    put('connection-card', el('div', { class: 'aside-body' }, el('div', { class: 'aside-top' }, el('span', { class: 'connection-icon' }, icon(adapter.type === 'Wi-Fi' ? 'wifi' : 'network')), el('div', {}, el('h2', {}, 'Your connection'), el('p', {}, adapter.type || 'Network adapter')), badge(adapter.status, adapter.status === 'Up' ? 'good' : '')), el('div', { class: 'aside-primary' }, adapter.ssid || adapter.name), el('div', { class: 'aside-sub' }, adapter.isDefault ? 'Default adapter' : 'Active adapter'), el('dl', { class: 'mini-dl' }, el('dt', {}, 'IP address'), el('dd', {}, list(adapter.ipAddresses)[0] || 'Not reported'), el('dt', {}, 'Link speed'), el('dd', {}, linkSpeed(adapter.speed)), el('dt', {}, 'Gateway'), el('dd', {}, adapter.gateway || 'Not reported'))));
  } else put('connection-card', el('div', { class: 'aside-body' }, el('h2', {}, 'Your connection'), el('p', { class: 'aside-sub' }, state ? 'No active adapter reported.' : 'Waiting for adapter information.')));
  const q = state?.diagnostics?.networkQuality;
  if (q) {
    const qBadgeClass = q.quality === 'Optimal' || q.quality === 'Good' ? 'good' : q.quality === 'Fair' ? 'blue' : 'bad';
    text('nav-quality-val', (q.internetPingMs !== null && q.internetPingMs !== undefined ? Math.round(q.internetPingMs) + 'ms' : q.quality));
    put('quality-card', el('div', { class: 'aside-body' },
      el('div', { class: 'aside-top' },
        el('span', { class: 'connection-icon' }, icon('activity')),
        el('div', {}, el('h2', {}, 'Connection quality'), el('p', {}, 'Real-time telemetry')),
        badge(q.quality, qBadgeClass)
      ),
      el('div', { class: 'quality-stat-row' },
        el('div', { class: 'quality-stat' },
          el('span', { class: 'quality-stat-label' }, 'Internet ping'),
          el('div', { class: 'quality-stat-val download-text' }, q.internetPingMs !== null && q.internetPingMs !== undefined ? Math.round(q.internetPingMs) : '—', el('small', {}, 'ms'))
        ),
        el('div', { class: 'quality-stat' },
          el('span', { class: 'quality-stat-label' }, 'Gateway ping'),
          el('div', { class: 'quality-stat-val upload-text' }, q.gatewayPingMs !== null && q.gatewayPingMs !== undefined ? Math.round(q.gatewayPingMs) : '—', el('small', {}, 'ms'))
        )
      ),
      el('dl', { class: 'mini-dl' },
        el('dt', {}, 'Jitter'), el('dd', {}, (q.jitterMs ? q.jitterMs + ' ms' : '0 ms')),
        el('dt', {}, 'Packet loss'), el('dd', {}, q.packetLossPercent + '%'),
        el('dt', {}, 'Gateway'), el('dd', {}, q.gatewayAddress || 'Not detected')
      ),
      el('div', { class: 'quality-foot' },
        el('span', { class: 'small muted' }, 'Tested every 3s'),
        el('a', { href: '#diagnostics', class: 'text-link' }, 'Open diagnostics →')
      )
    ));
  } else {
    put('quality-card', el('div', { class: 'aside-body' }, el('h2', {}, 'Connection quality'), el('p', { class: 'aside-sub' }, 'Measuring latency…')));
  }
  const used = s ? num(s.monthReceived) + num(s.monthSent) : null;
  const budget = num(state?.settings?.budgetGb) * 1e9;
  const percent = budget && used !== null ? used / budget * 100 : 0;
  const progress = el('div', { class: 'progress' + (percent > 100 ? ' exceeded' : ''), role: 'progressbar', 'aria-label': 'Monthly budget used', 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': Math.min(100, percent).toFixed(1), 'aria-valuetext': percent.toFixed(1) + '% used' });
  const fill = el('span'); fill.style.width = Math.min(100, percent) + '%'; progress.append(fill);
  put('budget-card', el('div', { class: 'aside-body' }, el('div', { class: 'aside-heading' }, el('h2', {}, 'This month'), el('a', { href: '#settings', class: 'text-link' }, 'Set budget')), el('div', { class: 'budget-value' }, bytes(used), el('small', {}, budget ? ' of ' + bytes(budget) : ' collected')), ...(budget ? [progress, el('p', { class: 'budget-caption' }, percent.toFixed(1) + '% of monthly budget')] : [el('p', { class: 'budget-caption' }, 'No budget set. Add an allowance in Settings.')])));
  const apps = list(state?.apps).slice().sort((a, b) => rate(b) - rate(a) || total(b) - total(a));
  put('overview-apps', appTable(apps.slice(0, 5)));
  text('overview-app-caption', state ? count(apps.length) + ' applications observed' : 'Waiting for collection');
  put('overview-insight', icon('info'), el('span', {}, apps.length ? apps[0].name + ' is currently first by traffic. App counters measure kernel TCP/IP traffic; physical-adapter totals may differ, especially with a VPN.' : 'Application totals measure kernel TCP/IP traffic and may differ from physical-adapter totals, especially with a VPN.'));
}
function renderLiveChart() {
  const points = list(state?.series).filter(p => Number.isFinite(new Date(p.time).getTime())).slice().sort((a, b) => new Date(a.time) - new Date(b.time));
  if (!points.length) { put('live-chart', pending('traffic samples')); text('chart-caption', 'Waiting for samples'); return; }
  const width = Math.max(300, $('live-chart').clientWidth - 26), height = 260;
  const left = 66, right = width - 18, top = 18, bottom = height - 32;
  const peak = Math.max(...points.map(p => Math.max(num(p.downloadBps), num(p.uploadBps))));
  const unit = $('chart-unit').value;
  const factor = unit === 'auto' ? peak >= 1e9 ? 1e9 : peak >= 1e6 ? 1e6 : peak >= 1000 ? 1000 : 1 : Number(unit);
  const suffix = { 1: 'B/s', 1000: 'KB/s', 1000000: 'MB/s', 1000000000: 'GB/s' }[factor];
  const rough = (peak || factor) / 4;
  const power = 10 ** Math.floor(Math.log10(rough));
  const tick = [1, 2, 5, 10].find(n => n * power >= rough) * power;
  const max = tick * 4;
  const start = new Date(points[0].time).getTime(), end = new Date(points.at(-1).time).getTime();
  const x = point => start === end ? (left + right) / 2 : left + (new Date(point.time).getTime() - start) / (end - start) * (right - left);
  const y = value => bottom - num(value) / max * (bottom - top);
  const chart = svg('svg', { viewBox: `0 0 ${width} ${height}`, role: 'img', 'aria-label': 'Live download and upload traffic. Peak ' + bytes(peak, true) + '. From ' + time(points[0].time) + ' to ' + time(points.at(-1).time) }, svg('title', {}, 'Live network traffic'));
  for (let i = 0; i <= 4; i++) {
    const value = max * i / 4, yy = y(value);
    chart.append(svg('line', { x1: left, x2: right, y1: yy, y2: yy, class: 'chart-grid' }), svg('text', { x: left - 11, y: yy + 4, 'text-anchor': 'end', class: 'chart-label' }, (value / factor).toLocaleString(undefined, { maximumFractionDigits: 2 }) + ' ' + suffix));
  }
  for (const [field, color] of [['downloadBps', '#2859df'], ['uploadBps', '#008978']]) {
    const path = points.map((point, i) => (i ? 'L' : 'M') + x(point).toFixed(2) + ',' + y(point[field]).toFixed(2)).join(' ');
    if (field === 'downloadBps' && points.length > 1) chart.append(svg('path', { d: path + ` L${x(points.at(-1))},${bottom} L${x(points[0])},${bottom} Z`, fill: '#2859df', opacity: '.06' }));
    chart.append(svg('path', { d: path, stroke: color, class: 'chart-line' }));
    const last = points.at(-1);
    chart.append(svg('circle', { cx: x(last), cy: y(last[field]), r: 3.2, fill: color }, svg('title', {}, bytes(last[field], true) + ' at ' + time(last.time))));
  }
  const labels = points.length === 1 ? [0] : [0, Math.floor((points.length - 1) / 2), points.length - 1];
  for (const i of [...new Set(labels)]) chart.append(svg('text', { x: x(points[i]), y: height - 8, 'text-anchor': i === 0 ? 'start' : i === points.length - 1 ? 'end' : 'middle', class: 'chart-label' }, time(points[i].time, { hour: '2-digit', minute: '2-digit' })));
  put('live-chart', chart);
  text('chart-caption', 'Peak ' + bytes(peak, true));
  text('live-window', 'Physical adapters · ' + points.length + ' samples');
}
function appTable(apps) {
  if (!apps.length) return state?.collector?.status === 'needs-admin' ? empty('Application capture needs administrator access', 'Launch Start Monitor.cmd and approve Windows elevation. Adapter totals are still available.', 'shield') : pending('applications');
  const max = Math.max(1, ...apps.map(total));
  return table(['Application', 'Downloaded', 'Uploaded', 'Total usage', 'Connections', 'Current traffic', 'Actions'], apps.map(app => {
    const button = el('button', { class: 'app-name-button', type: 'button', 'data-app-id': app.id }, app.name || 'Unknown application');
    button.addEventListener('click', () => openApp(app));
    const track = el('span', { class: 'usage-track', 'aria-hidden': 'true' }, el('span'));
    track.firstChild.style.width = total(app) / max * 100 + '%';
    return el('tr', {}, cell(el('div', { class: 'app-cell' }, el('span', { class: 'app-avatar', 'aria-hidden': 'true' }, String(app.name || '?').slice(0, 1).toUpperCase()), el('div', { class: 'app-name' }, button, el('span', { class: 'app-secondary' }, list(app.pids).length ? 'PID ' + app.pids.join(', ') : 'No current connection'))), ''), cell(bytes(app.receivedBytes), 'number download-text'), cell(bytes(app.sentBytes), 'number upload-text'), cell(el('div', { class: 'usage-cell' }, bytes(total(app)), track)), cell(count(app.connectionCount)), cell(bytes(rate(app), true), 'number rate-cell'), cell(appActions(app), 'app-actions'));
  }), 'app-list');
}
function appActions(app) {
  const access = window.flowControls?.access(app) || { allowed: false, reason: 'Application controls are loading.' };
  const limits = el('button', { class: 'button row-limit-button', type: 'button', 'data-row-action': 'limits', title: access.reason || 'Set limits for ' + app.name, disabled: access.allowed ? null : '' }, 'Limits');
  const end = el('button', { class: 'button row-end-button', type: 'button', 'data-row-action': 'end', title: access.endReason || 'Select a running process of ' + app.name + ' to end', disabled: access.canEnd ? null : '' }, 'End process');
  limits.addEventListener('click', () => window.flowControls?.openRule(app));
  end.addEventListener('click', () => window.flowControls?.openProcesses(app));
  return el('div', { class: 'row-actions' }, limits, end);
}
function renderApps() {
  const query = $('app-search').value.trim().toLowerCase();
  const filter = $('app-filter').value;
  const apps = list(state?.apps).filter(a => [a.name, a.path, ...list(a.pids)].join(' ').toLowerCase().includes(query) && (filter !== 'connected' || a.connectionCount > 0) && (filter !== 'transferring' || rate(a) > 0));
  const comparators = { rate: (a, b) => rate(b) - rate(a), total: (a, b) => total(b) - total(a), download: (a, b) => num(b.receivedBytes) - num(a.receivedBytes), upload: (a, b) => num(b.sentBytes) - num(a.sentBytes), name: (a, b) => String(a.name).localeCompare(String(b.name)), connections: (a, b) => num(b.connectionCount) - num(a.connectionCount) };
  apps.sort(comparators[$('app-sort').value]);
  text('app-count', state ? '(' + apps.length + ')' : '');
  put('applications-table', query && !apps.length ? empty('No matching applications', 'Try another application name, executable path, or PID.', 'search') : appTable(apps));
}
function endpoint(address, port) {
  if (!address || address === '*' || address === '0.0.0.0' && !port) return '—';
  return (String(address).includes(':') ? '[' + address + ']' : address) + (port ? ':' + port : '');
}
function connectionTable(connections) {
  if (!connections.length) return pending('connections');
  return table(['Application / PID', 'Protocol', 'Local endpoint', 'Remote endpoint / Host', 'State'], connections.map(c => el('tr', {},
    cell(el('div', {}, el('span', { class: 'app-name' }, c.app || 'Unknown application'), el('span', { class: 'app-secondary' }, 'PID ' + c.pid)), ''),
    cell(badge(c.protocol, c.protocol === 'TCP' ? 'blue' : '')),
    cell(endpoint(c.localAddress, c.localPort), 'number endpoint'),
    cell(el('div', { class: 'endpoint-cell' },
      el('span', { class: 'endpoint-addr' }, endpoint(c.remoteAddress, c.remotePort)),
      c.remoteHost || c.service ? el('span', { class: 'endpoint-host', title: (c.service ? '[' + c.service + '] ' : '') + (c.remoteHost || '') },
        c.service ? el('span', { class: 'badge-subtle' }, c.service) : null,
        c.remoteHost ? el('span', { class: 'host-text' }, c.remoteHost) : null
      ) : null
    ), 'number endpoint'),
    cell(c.state || '—'))), 'connection-list');
}
function renderConnections() {
  const query = $('connection-search').value.trim().toLowerCase(), protocol = $('protocol-filter').value;
  const stateFilter = $('connection-state-filter')?.value || 'all';
  const connections = list(state?.connections).filter(c =>
    (protocol === 'all' || c.protocol === protocol) &&
    (stateFilter === 'all' || (c.state && c.state.toLowerCase() === stateFilter.toLowerCase())) &&
    [c.app, c.pid, c.localAddress, c.localPort, c.remoteAddress, c.remotePort, c.state, c.remoteHost || '', c.service || ''].join(' ').toLowerCase().includes(query)
  );
  text('connection-count', state ? '(' + connections.length + ')' : '');
  put('connections-table', state?.diagnostics?.connectionError && !connections.length
    ? empty('Connection snapshot unavailable', state.diagnostics.connectionError, 'info')
    : (query || protocol !== 'all' || stateFilter !== 'all') && !connections.length
      ? empty('No matching connections', 'Try a different endpoint, application, protocol, or state.', 'search')
      : connectionTable(connections));
}
function openApp(app) { selectedApp = app; renderDetail(); $('app-dialog').showModal(); }
function renderDetail() {
  if (!selectedApp) return;
  selectedApp = list(state?.apps).find(a => a.id === selectedApp.id) || selectedApp;
  const app = selectedApp;
  window.flowControls?.selectApp(app);
  text('detail-title', app.name || 'Unknown application');
  const connections = list(state?.connections).filter(c => list(app.pids).includes(c.pid));
  put('app-detail', el('div', { class: 'metrics' }, metric('Downloaded', bytes(app.receivedBytes), 'Since monitor started', 'down', 'download-text'), metric('Uploaded', bytes(app.sentBytes), 'Since monitor started', 'up', 'upload-text')), el('section', { class: 'panel' }, details([['Executable', app.path], ['Process IDs', list(app.pids).join(', ') || 'No current process'], ['Protocols', list(app.protocols).join(', ')], ['Current traffic', bytes(rate(app), true)], ['Last seen', time(app.lastSeen)]])), el('h3', {}, 'Current connections (' + connections.length + ')'), el('section', { class: 'panel table-panel' }, connectionTable(connections)), el('p', { class: 'page-note' }, 'Kernel TCP/IP counters can differ from physical-adapter totals, especially with VPN traffic. Totals cover this monitor session.'));
}

async function loadHistory() {
  if (stopped) return;
  const request = ++historyRequest, query = historyQuery();
  historyLoading = true; historyError = ''; renderHistory();
  try {
    const data = await api('/api/history?' + query);
    if (request !== historyRequest || stopped) return;
    if (!data || !Array.isArray(data.daily) || !Array.isArray(data.apps) || !data.totals) throw new Error('The service returned incomplete history.');
    historyData = data; historyData.loadedQuery = query; historyData.loadedAt = new Date();
  } catch (error) { if (request === historyRequest && !stopped) historyError = error.message; }
  finally { if (request === historyRequest) { historyLoading = false; if (page === 'history') renderHistory(); } }
}
function renderHistory() {
  const data = historyData?.loadedQuery === historyQuery() ? historyData : null;
  message('history-message', historyError ? historyError + (data ? ' Showing the previous history snapshot.' : ' Use Refresh to try again.') : historyLoading ? 'Loading local history…' : '', historyError ? 'error' : '');
  $('history-refresh').disabled = historyLoading || stopped;
  $('export-history').disabled = !data || historyLoading || stopped;
  $('history-dates').hidden = historyRange !== 'custom';
  $('history-ranges').querySelectorAll('button').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.range === historyRange)));
  text('history-updated', data ? 'Updated ' + time(data.loadedAt) : '');
  const key = [data?.loadedAt?.getTime(), historyQuery(), historyLoading, $('history-search').value, $('history-sort').value, $('history-chart').clientWidth].join('|');
  if (key === historyRenderedKey) return;
  historyRenderedKey = key;
  const days = list(data?.daily);
  const used = data ? total(data.totals) : null;
  put('history-totals', metric('Total usage', bytes(used), 'Download + upload', 'activity'),
    metric('Downloaded', bytes(data?.totals?.receivedBytes), 'Saved physical-adapter traffic', 'down', 'download-text'),
    metric('Uploaded', bytes(data?.totals?.sentBytes), 'Saved physical-adapter traffic', 'up', 'upload-text'),
    metric('Per recorded day', bytes(days.length ? used / days.length : null), days.length + ' days with saved records', 'history'));
  text('history-period', data?.startDate ? dateLabel(data.startDate) + ' – ' + dateLabel(data.endDate) : 'Saved physical-adapter traffic');
  if (!data) {
    put('history-chart', empty(historyLoading ? 'Loading history' : 'History unavailable', historyLoading ? 'Reading usage collected on this laptop.' : 'Choose Refresh to request local history.', 'history'));
    for (const id of ['history-apps', 'history-days']) put(id, empty('No records loaded', 'Records will appear for the selected period.', 'history'));
    text('history-insight', 'Records are available while the local monitoring service runs.');
    text('history-storage-status', 'Waiting for local database');
    put('history-storage');
    return;
  }
  renderHistoryChart(list(data.daily));
  const peak = days.slice().sort((a, b) => total(b) - total(a))[0];
  const previous = total(data.previousTotals || {});
  const comparison = data.previousRecordedDays ? (previous ? (Math.abs((used - previous) / previous) * 100).toFixed(1) + '% ' + (used >= previous ? 'more' : 'less') + ' than the preceding period.' : 'The preceding period has zero saved usage.') : 'Comparison needs records from the preceding period.';
  text('history-insight', (peak ? 'Busiest recorded day was ' + dateLabel(peak.date) + ' with ' + bytes(total(peak)) + '. ' : '') + comparison + ' Comparisons use saved samples, which can cover partial days.');
  const storage = data.storage;
  text('history-storage-status', storage ? storage.error ? 'SQLite · save needs attention' : 'SQLite · saved on this laptop' : 'Local SQLite history');
  put('history-storage', storage ? details([['Database file', storage.file], ['Database size', bytes(storage.databaseBytes) + ' allocated; journal files can add temporary space'],
  ['Saved dates', storage.firstDay ? dateLabel(storage.firstDay) + ' – ' + dateLabel(storage.lastDay) : 'Waiting for samples'],
  ['Recorded days', count(storage.recordedDays)], ['Retention', 'Kept until you remove the local data'],
  ['Save status', storage.error || 'Writing automatically about every 2 seconds'],
  ['Waiting to save', count(storage.pendingMinutes) + ' sampled minute(s)']]) : el('p', { class: 'page-note' }, 'Restart Flow to load storage details from the updated service.'));
  put('history-days', days.length ? table(['Date', 'Downloaded', 'Uploaded', 'Total usage'], days.slice().reverse().map(day => {
    const button = el('button', { class: 'history-day-button', type: 'button', 'aria-label': 'View app usage for ' + day.date }, dateLabel(day.date));
    button.addEventListener('click', () => {
      historyRange = 'custom'; historyFrom = day.date; historyTo = day.date;
      $('history-start').value = day.date; $('history-end').value = day.date;
      loadHistory(); $('page-history').scrollIntoView({ block: 'start' });
    });
    const coverage = day.sampledMinutes == null ? 'Saved usage' : day.sampledMinutes + ' sampled minute(s)';
    return el('tr', {}, cell(el('div', {}, button, el('span', { class: 'app-secondary' }, coverage)), ''), cell(bytes(day.receivedBytes), 'number download-text'), cell(bytes(day.sentBytes), 'number upload-text'), cell(bytes(total(day))));
  }), 'history-day-list') : empty('No daily records', 'Choose another date range to explore earlier collection.', 'history'));
  const query = $('history-search').value.trim().toLowerCase();
  const allApps = list(data.apps);
  const apps = allApps.filter(app => [app.name, app.path].join(' ').toLowerCase().includes(query));
  const comparators = { total: (a, b) => total(b) - total(a), download: (a, b) => num(b.receivedBytes) - num(a.receivedBytes), upload: (a, b) => num(b.sentBytes) - num(a.sentBytes), name: (a, b) => String(a.name).localeCompare(String(b.name)) };
  apps.sort(comparators[$('history-sort').value]);
  const appTotal = allApps.reduce((sum, app) => sum + total(app), 0);
  text('history-app-caption', apps.length + ' of ' + allApps.length + ' applications · ' + bytes(appTotal) + ' measured app traffic');
  put('history-apps', apps.length ? table(['Application', 'Downloaded', 'Uploaded', 'Total usage', 'App share'], apps.map(app => {
    const share = appTotal ? total(app) / appTotal * 100 : 0;
    const progress = el('progress', { max: 100, value: share, 'aria-label': app.name + ' share of measured application traffic' });
    return el('tr', {}, cell(el('div', {}, el('span', { class: 'app-name' }, app.name || 'Unknown application'), el('span', { class: 'app-secondary', title: app.path || '' }, app.path || 'Path not reported')), ''), cell(bytes(app.receivedBytes), 'number download-text'), cell(bytes(app.sentBytes), 'number upload-text'), cell(bytes(total(app))), cell(el('div', { class: 'history-share' }, share.toFixed(1) + '%', progress)));
  }), 'history-app-list') : empty(query ? 'No matching applications' : 'No application usage recorded', query ? 'Try another name or executable path.' : 'Application usage is saved while administrator capture runs.', 'apps'));
}
function historyQuery() {
  const params = new URLSearchParams({ range: historyRange });
  if (historyRange === 'custom') { params.set('start', historyFrom); params.set('end', historyTo); }
  return params.toString();
}
function dateLabel(value) {
  const date = new Date(value + 'T12:00:00');
  return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}
function renderHistoryChart(days) {
  if (!days.length) { put('history-chart', empty('No usage recorded in this period', 'History starts when the monitor runs. Select another period to inspect earlier local records.', 'history')); return; }
  const width = Math.max(240, $('history-chart').clientWidth - 32), height = 260;
  const left = 65, right = width - 12, top = 15, bottom = 222;
  const max = Math.max(1, ...days.map(total)) * 1.1;
  const chart = svg('svg', { viewBox: `0 0 ${width} ${height}`, role: 'img', 'aria-label': 'Daily physical-adapter usage. ' + days.map(d => d.date + ': ' + bytes(d.receivedBytes) + ' downloaded, ' + bytes(d.sentBytes) + ' uploaded').join('; ') }, svg('title', {}, 'Daily network usage'));
  for (let i = 0; i <= 4; i++) {
    const value = max * i / 4, y = bottom - value / max * (bottom - top);
    chart.append(svg('line', { x1: left, x2: right, y1: y, y2: y, class: 'chart-grid' }), svg('text', { x: left - 10, y: y + 4, 'text-anchor': 'end', class: 'chart-label' }, bytes(value)));
  }
  const firstDate = historyData?.startDate || days[0].date, lastDate = historyData?.endDate || days.at(-1).date;
  const first = Date.parse(firstDate), length = Math.round((Date.parse(lastDate) - first) / 86400000) + 1;
  const step = (right - left) / length, barWidth = Math.min(42, step * .7);
  days.forEach((day, i) => {
    const index = Math.round((Date.parse(day.date) - first) / 86400000);
    const x = left + step * (index + .5), downloadHeight = num(day.receivedBytes) / max * (bottom - top), uploadHeight = num(day.sentBytes) / max * (bottom - top);
    const group = svg('g', {}, svg('title', {}, day.date + ': ' + bytes(day.receivedBytes) + ' downloaded, ' + bytes(day.sentBytes) + ' uploaded'));
    group.append(svg('rect', { x: x - barWidth / 2, y: bottom - downloadHeight, width: barWidth, height: downloadHeight, fill: 'var(--blue)', rx: '2' }), svg('rect', { x: x - barWidth / 2, y: bottom - downloadHeight - uploadHeight, width: barWidth, height: uploadHeight, fill: 'var(--teal)', rx: '2' }));
    chart.append(group);
    if (i % Math.max(1, Math.ceil(days.length / (width < 500 ? 4 : 8))) === 0 || i === days.length - 1) {
      const date = new Date(day.date + 'T12:00:00');
      chart.append(svg('text', { x, y: 247, 'text-anchor': 'middle', class: 'chart-label' }, Number.isNaN(date.getTime()) ? day.date : date.toLocaleDateString([], { month: 'short', day: 'numeric' })));
    }
  });
  put('history-chart', chart);
}
function linkSpeed(value) {
  if (!Number.isFinite(value) || value <= 0) return 'Not reported';
  return value >= 1e9 ? (value / 1e9).toLocaleString(undefined, { maximumFractionDigits: 2 }) + ' Gbps' : (value / 1e6).toLocaleString(undefined, { maximumFractionDigits: 1 }) + ' Mbps';
}
function renderNetwork() {
  const adapters = list(state?.interfaces).slice().sort((a, b) => Number(!!b.isDefault) - Number(!!a.isDefault));
  text('network-summary', state ? adapters.length + ' adapters · ' + adapters.filter(a => a.status === 'Up').length + ' up' : 'Waiting for adapter information');
  put('adapter-list', adapters.length ? adapters.map(adapter => el('section', { class: 'panel' }, el('div', { class: 'panel-heading adapter-heading' }, el('div', { class: 'adapter-title' }, el('span', { class: 'connection-icon' }, icon(adapter.type === 'Wi-Fi' ? 'wifi' : 'network')), el('div', {}, el('h2', {}, adapter.name), el('p', {}, adapter.description || adapter.type))), el('div', { class: 'adapter-badges' }, badge(adapter.status || 'Unknown', adapter.status === 'Up' ? 'good' : ''), ...(adapter.isDefault ? [badge('Default route', 'blue')] : []))), el('div', { class: 'adapter-rates' }, el('div', {}, el('span', {}, 'Download'), el('strong', { class: 'download-text' }, bytes(adapter.downloadBps, true))), el('div', {}, el('span', {}, 'Upload'), el('strong', { class: 'upload-text' }, bytes(adapter.uploadBps, true)))), details([['Type', adapter.type], ['IP addresses', list(adapter.ipAddresses).join('\n')], ['Gateway', adapter.gateway], ['MAC address', adapter.mac], ['Link speed', linkSpeed(adapter.speed)], ...(adapter.type === 'Wi-Fi' || adapter.ssid ? [['Wi-Fi network', adapter.ssid], ['Signal', adapter.signal], ['Receive link', adapter.linkReceiveMbps ? adapter.linkReceiveMbps + ' Mbps' : null], ['Transmit link', adapter.linkTransmitMbps ? adapter.linkTransmitMbps + ' Mbps' : null]] : []), ['Adapter received', bytes(adapter.receivedBytes)], ['Adapter sent', bytes(adapter.sentBytes)]]))) : pending('network adapters'));
}
function diagTile(label, value, note, color = '') {
  return el('div', { class: 'diag-tile' },
    el('div', { class: 'diag-tile-label' }, label),
    el('div', { class: 'diag-tile-value ' + color }, value),
    el('div', { class: 'diag-tile-note' }, note)
  );
}

function renderDiagnostics() {
  const q = state?.diagnostics?.networkQuality;
  if (q) {
    const qBadgeClass = q.quality === 'Optimal' || q.quality === 'Good' ? 'good' : q.quality === 'Fair' ? 'blue' : 'bad';
    put('diag-quality-badge', badge(q.quality, qBadgeClass));
    put('diag-metrics',
      diagTile('Internet ping', q.internetPingMs !== null && q.internetPingMs !== undefined ? Math.round(q.internetPingMs) + ' ms' : '—', '1.1.1.1 Cloudflare', 'download-text'),
      diagTile('Gateway ping', q.gatewayPingMs !== null && q.gatewayPingMs !== undefined ? Math.round(q.gatewayPingMs) + ' ms' : '—', 'Local router / Wi-Fi', 'upload-text'),
      diagTile('Jitter', q.jitterMs !== null && q.jitterMs !== undefined ? q.jitterMs + ' ms' : '0 ms', 'Latency variance'),
      diagTile('Packet loss', q.packetLossPercent + '%', 'Sample window', q.packetLossPercent > 0 ? 'upload-text' : '')
    );
    text('diag-gateway-ip', 'Gateway: ' + (q.gatewayAddress || 'Not detected'));
    renderPingChart(list(q.history));
  }
  const alerts = list(state?.alerts);
  text('alerts-count', alerts.length ? '(' + alerts.length + ')' : '');
  if (!alerts.length) {
    put('alerts-table', el('div', { class: 'empty-state', style: 'padding: 32px 20px;' },
      el('span', { class: 'empty-icon' }, icon('shield')),
      el('strong', {}, 'No network alerts recorded'),
      el('p', {}, 'Telemetry is stable. Disconnections, sustained packet loss, or latency spikes will appear here.')
    ));
  } else {
    put('alerts-table', table(['Time', 'Severity', 'Event', 'Details'], alerts.map(a => el('tr', {},
      cell(time(a.time), 'small muted'),
      cell(badge(a.level.toUpperCase(), a.level === 'error' ? 'bad' : a.level === 'warning' ? 'warning' : 'blue')),
      cell(el('strong', { class: 'app-name' }, a.title)),
      cell(a.detail)
    ))));
  }
}

function renderPingChart(historyPoints) {
  if (!historyPoints.length) { put('diag-ping-chart', pending('latency samples')); return; }
  const container = $('diag-ping-chart');
  const width = Math.max(280, (container.clientWidth || 400) - 26), height = 160;
  const left = 45, right = width - 15, top = 15, bottom = height - 25;
  const maxVal = Math.max(50, ...historyPoints.map(p => Math.max(num(p.internetMs), num(p.gatewayMs))));
  const scaleY = val => bottom - (num(val) / maxVal) * (bottom - top);
  const scaleX = i => left + (i / Math.max(1, historyPoints.length - 1)) * (right - left);

  const netPath = historyPoints.map((p, i) => (i === 0 ? 'M' : 'L') + ' ' + scaleX(i).toFixed(1) + ' ' + scaleY(p.internetMs).toFixed(1)).join(' ');
  const gwPath = historyPoints.map((p, i) => (i === 0 ? 'M' : 'L') + ' ' + scaleX(i).toFixed(1) + ' ' + scaleY(p.gatewayMs).toFixed(1)).join(' ');

  const chartSvg = svg('svg', { viewBox: `0 0 ${width} ${height}`, style: `width: 100%; height: ${height}px; display: block;` },
    svg('line', { x1: left, y1: top, x2: right, y2: top, stroke: 'var(--line)', 'stroke-dasharray': '2 2' }),
    svg('line', { x1: left, y1: (top + bottom) / 2, x2: right, y2: (top + bottom) / 2, stroke: 'var(--line)', 'stroke-dasharray': '2 2' }),
    svg('line', { x1: left, y1: bottom, x2: right, y2: bottom, stroke: 'var(--line)' }),
    svg('text', { x: 6, y: top + 4, fill: 'var(--muted)', 'font-size': '10' }, Math.round(maxVal) + 'ms'),
    svg('text', { x: 6, y: bottom - 2, fill: 'var(--muted)', 'font-size': '10' }, '0ms'),
    svg('path', { d: netPath, fill: 'none', stroke: 'var(--blue)', 'stroke-width': '2' }),
    svg('path', { d: gwPath, fill: 'none', stroke: 'var(--teal)', 'stroke-width': '2' })
  );
  put('diag-ping-chart', chartSvg);
}

function renderRouter() {
  const router = state?.router;
  const connected = router?.status === 'connected';
  $('router-status').className = 'badge ' + (connected ? 'good' : router?.status === 'error' ? 'warning' : '');
  text('router-status', connected ? 'Connected' : router?.status === 'error' ? 'Unavailable' : 'Not connected');
  // Keep the form nodes mounted so polling cannot erase credentials or focus.
  $('router-form').hidden = connected;
  $('router-connected').hidden = !connected;
  if (router?.message && !$('router-message').dataset.action) message('router-message', router.message, router.status === 'error' ? 'error' : '');
  if (!connected) {
    put('router-details', empty('Connect to your router', 'View router information, whole-network monthly totals, and a list of connected devices. This is optional.', 'router'));
    put('router-devices', empty('Router is not connected', 'Connect above to read the router’s device list. Per-device traffic totals are not available.', 'network'));
    return;
  }
  put('router-details', el('div', { class: 'panel-heading' }, el('div', {}, el('h2', {}, router.model || 'Your router'), el('p', {}, 'Whole-network monthly usage'))), el('div', { class: 'router-stats' }, el('div', {}, el('span', {}, 'Downloaded'), el('strong', { class: 'download-text' }, Number.isFinite(router.monthlyDownloadMb) ? bytes(router.monthlyDownloadMb * 1e6) : '—')), el('div', {}, el('span', {}, 'Uploaded'), el('strong', { class: 'upload-text' }, Number.isFinite(router.monthlyUploadMb) ? bytes(router.monthlyUploadMb * 1e6) : '—'))), details([['Firmware', router.firmware], ['Network type', router.networkType], ['Temperature', router.temperature], ['Last updated', router.updatedAt ? time(router.updatedAt) : null]]));
  const devices = list(router.devices);
  put('router-devices', devices.length ? table(['Device', 'IP address', 'MAC address', 'Type', 'Signal', 'Transmit link', 'Receive link'], devices.map(device => el('tr', {}, cell(el('span', { class: 'app-name' }, device.name || 'Unnamed device'), ''), cell(device.ip || '—', 'number endpoint'), cell(device.mac || '—', 'number endpoint'), cell(device.type || '—'), cell(device.signal || '—'), cell(device.txRate || '—'), cell(device.rxRate || '—')))) : empty('No devices reported', 'The router has not returned a device list. No per-device usage is inferred.', 'network'));
}
function renderSettings() {
  const collector = state?.collector;
  text('capture-status', collector ? (collector.message || collector.status) + (num(collector.eventsLost) ? ' Lost events: ' + count(collector.eventsLost) + '.' : '') : 'Waiting for collector status.');
}

function focusApp(id) {
  const button = [...document.querySelectorAll('#page-' + page + ' [data-app-id]')].find(node => node.dataset.appId === id);
  button?.focus({ preventScroll: true });
}
function notify(value) {
  text('toast', value); $('toast').hidden = false;
  clearTimeout(notify.timer); notify.timer = setTimeout(() => { $('toast').hidden = true; }, 4500);
}
window.addEventListener('hashchange', navigate);
$('menu-toggle').addEventListener('click', toggleMenu);
$('header-menu-toggle').addEventListener('click', toggleMenu);
$('menu-close').addEventListener('click', () => {
  if ($('menu-toggle').getClientRects().length) closeNav();
  else if (!document.body.classList.contains('menu-hidden')) toggleMenu();
  $('header-menu-toggle').focus();
});
$('nav-backdrop').addEventListener('click', closeNav);
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && $('sidebar').classList.contains('open')) { closeNav(); $('menu-toggle').focus(); }
});
$('pause-toggle').addEventListener('click', () => {
  paused = !paused;
  $('pause-toggle').setAttribute('aria-pressed', String(paused));
  text('pause-label', paused ? 'Resume display' : 'Pause display');
  $('pause-icon').querySelector('use').setAttribute('href', paused ? '#i-play' : '#i-pause');
  $('paused-alert').hidden = !paused;
  if (!paused && latest) { state = latest; render(); }
  showHealth();
});
$('retry-state').addEventListener('click', poll);
$('dismiss-admin').addEventListener('click', () => { adminDismissed = true; $('admin-alert').hidden = true; });
$('chart-unit').addEventListener('change', renderLiveChart);
$('app-search').addEventListener('input', renderApps);
$('app-sort').addEventListener('change', renderApps);
$('app-filter').addEventListener('change', renderApps);
function quickFind() {
  if (document.querySelector('dialog[open]')) return;
  location.hash = '#applications'; navigate(); $('app-search').focus(); $('app-search').select();
}
$('quick-find').addEventListener('click', quickFind);
document.addEventListener('keydown', event => { if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k' && !document.querySelector('dialog[open]')) { event.preventDefault(); quickFind(); } });
$('connection-search').addEventListener('input', renderConnections);
$('protocol-filter').addEventListener('change', renderConnections);
$('close-detail').addEventListener('click', () => $('app-dialog').close());
$('app-dialog').addEventListener('close', () => { if (selectedApp) focusApp(String(selectedApp.id)); selectedApp = null; });
$('app-dialog').addEventListener('click', event => { if (event.target === $('app-dialog') && event.clientX < $('app-dialog').getBoundingClientRect().left) $('app-dialog').close(); });
$('history-ranges').addEventListener('click', event => {
  const button = event.target.closest('[data-range]');
  if (!button) return;
  historyRange = button.dataset.range;
  if (historyRange === 'custom' && !historyFrom) {
    const today = new Date();
    historyTo = today.getFullYear() + '-' + String(today.getMonth() + 1).padStart(2, '0') + '-' + String(today.getDate()).padStart(2, '0');
    historyFrom = historyData?.startDate || historyTo;
    $('history-start').value = historyFrom; $('history-end').value = historyTo;
  }
  loadHistory();
});
$('history-dates').addEventListener('submit', event => {
  event.preventDefault();
  if (!$('history-dates').reportValidity()) return;
  historyFrom = $('history-start').value; historyTo = $('history-end').value;
  historyRange = 'custom'; loadHistory();
});
$('history-search').addEventListener('input', renderHistory);
$('history-sort').addEventListener('change', renderHistory);
$('history-refresh').addEventListener('click', loadHistory);
$('export-history').addEventListener('click', async () => {
  const button = $('export-history'); button.disabled = true;
  try {
    const response = await fetch('/api/export?' + historyQuery(), { cache: 'no-store', headers: pairingHeaders(), signal: AbortSignal.timeout(20000) });
    if (!response.ok) throw new Error('CSV export failed (' + response.status + '). Try again.');
    const url = URL.createObjectURL(await response.blob());
    const link = el('a', { href: url, download: 'flow-usage-' + historyRange + '-' + new Date().toISOString().slice(0, 10) + '.csv' });
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    notify('Usage CSV exported.');
  } catch (error) { message('history-message', error.message, 'error'); }
  finally { button.disabled = false; }
});
$('export-json')?.addEventListener('click', async () => {
  const button = $('export-json'); button.disabled = true;
  try {
    const response = await fetch('/api/export?' + historyQuery() + '&format=json', { cache: 'no-store', headers: pairingHeaders(), signal: AbortSignal.timeout(20000) });
    if (!response.ok) throw new Error('JSON export failed (' + response.status + '). Try again.');
    const url = URL.createObjectURL(await response.blob());
    const link = el('a', { href: url, download: 'flow-usage-' + historyRange + '-' + new Date().toISOString().slice(0, 10) + '.json' });
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    notify('Usage JSON exported.');
  } catch (error) { message('history-message', error.message, 'error'); }
  finally { button.disabled = false; }
});
$('budget-input').addEventListener('input', () => { budgetDirty = true; message('budget-message', ''); });
$('budget-form').addEventListener('submit', async event => {
  event.preventDefault();
  const budgetGb = Number($('budget-input').value);
  if (!Number.isFinite(budgetGb) || budgetGb < 0 || budgetGb > 100000 || !$('budget-form').reportValidity()) { message('budget-message', 'Enter a budget from 0 to 100,000 GB.', 'error'); return; }
  $('budget-save').disabled = true; message('budget-message', 'Saving budget…');
  try {
    const result = await api('/api/settings', { method: 'POST', body: JSON.stringify({ budgetGb }) });
    if (result.success === false) throw new Error(result.message || 'The budget could not be saved.');
    if (state) state.settings = { ...state.settings, budgetGb };
    if (latest) latest.settings = { ...latest.settings, budgetGb };
    budgetDirty = false; message('budget-message', 'Budget saved on this laptop.', 'success');
  } catch (error) { message('budget-message', error.message, 'error'); }
  finally { $('budget-save').disabled = false; }
});
$('router-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (routerBusy || !$('router-form').reportValidity()) return;
  routerBusy = true; $('router-connect').disabled = true;
  $('router-message').dataset.action = 'true'; message('router-message', 'Connecting to 192.168.8.1…');
  try {
    const result = await api('/api/router/connect', { method: 'POST', signal: AbortSignal.timeout(60000), body: JSON.stringify({ username: $('router-username').value.trim(), password: $('router-password').value }) });
    if (!result.success) throw new Error(result.message || 'Router connection failed.');
    message('router-message', result.message || 'Router connected.', 'success');
    poll();
  } catch (error) { message('router-message', error.message, 'error'); }
  finally { $('router-password').value = ''; routerBusy = false; $('router-connect').disabled = !!state?.access?.isRemote; delete $('router-message').dataset.action; poll(); }
});
$('router-disconnect').addEventListener('click', async () => {
  if (routerBusy) return;
  routerBusy = true; $('router-disconnect').disabled = true; $('router-message').dataset.action = 'true';
  try {
    const result = await api('/api/router/disconnect', { method: 'POST', body: '{}' });
    if (!result.success) throw new Error(result.message || 'Router disconnect failed.');
    message('router-message', result.message || 'Router disconnected.', 'success');
    $('router-password').value = ''; poll();
  } catch (error) { message('router-message', error.message, 'error'); }
  finally { routerBusy = false; $('router-disconnect').disabled = !!state?.access?.isRemote; delete $('router-message').dataset.action; poll(); }
});
$('stop-monitor').addEventListener('click', async () => {
  if (!window.confirm('Stop the monitor? Traffic collection and history will stop. Restart using Start Monitor.cmd.')) return;
  $('stop-monitor').disabled = true;
  try {
    const result = await api('/api/stop', { method: 'POST', body: '{}' });
    if (!result.success) throw new Error(result.message || 'The monitor could not be stopped.');
    stopped = true; clearTimeout(pollTimer); historyRequest++;
    message('stop-message', 'Monitor stopped. Restart using Start Monitor.cmd.', 'success');
    $('pause-toggle').disabled = true; $('retry-state').disabled = true;
    $('budget-save').disabled = true; $('router-connect').disabled = true; $('router-disconnect').disabled = true;
    $('export-history').disabled = true; $('history-refresh').disabled = true;
    pollError = ''; showHealth(); notify('Monitor stopped. Restart using Start Monitor.cmd.');
  } catch (error) { message('stop-message', error.message, 'error'); $('stop-monitor').disabled = false; }
});
let resizeTimer;
window.addEventListener('resize', () => {
  if (!$('menu-toggle').getClientRects().length) closeNav(); else syncMenu();
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { if (page === 'overview') renderLiveChart(); else if (page === 'history') renderHistory(); }, 150);
});
$('connection-state-filter')?.addEventListener('change', renderConnections);

let speedTestRunning = false;
$('run-speedtest')?.addEventListener('click', async () => {
  if (speedTestRunning) return;
  speedTestRunning = true;
  $('run-speedtest').disabled = true;
  $('speedtest-progress').hidden = false;
  text('speedtest-badge', 'Testing…');
  $('speedtest-badge').className = 'badge blue';
  text('speedtest-sub', 'Measuring latency & downloading test payload…');
  text('speedtest-val', '…');
  text('speedtest-msg', '');
  try {
    const res = await api('/api/diagnostics/speedtest', { method: 'POST', body: '{}' });
    if (res.success) {
      text('speedtest-val', res.downloadMbps);
      text('speedtest-badge', 'Completed');
      $('speedtest-badge').className = 'badge good';
      put('speedtest-sub', el('div', { class: 'speedtest-meta' },
        el('span', {}, icon('activity'), el('strong', {}, res.pingMs + ' ms'), ' ping'),
        el('span', {}, el('strong', {}, res.durationSeconds + ' s'), ' duration'),
        el('span', {}, el('strong', {}, res.server))
      ));
      text('speedtest-msg', 'Successfully downloaded ' + bytes(res.bytesReceived) + ' from ' + res.server + '.');
    } else {
      text('speedtest-val', '—');
      text('speedtest-badge', 'Failed');
      $('speedtest-badge').className = 'badge warning';
      text('speedtest-sub', res.error || 'Speed test failed');
    }
  } catch (err) {
    text('speedtest-val', '—');
    text('speedtest-badge', 'Error');
    $('speedtest-badge').className = 'badge error';
    text('speedtest-sub', err.message || 'Speed test failed');
  } finally {
    speedTestRunning = false;
    $('speedtest-progress').hidden = true;
    $('run-speedtest').disabled = false;
  }
});

let pingRunning = false;
$('ping-form')?.addEventListener('submit', async (e) => {
  e.preventDefault();
  if (pingRunning) return;
  const host = $('ping-host').value.trim();
  if (!host) return;
  pingRunning = true;
  const btn = $('ping-form').querySelector('button[type=submit]');
  btn.disabled = true;
  const wrap = $('ping-console-wrap');
  const out = $('ping-output');
  if (wrap) wrap.hidden = false;
  out.hidden = false;
  out.textContent = 'Pinging ' + host + '…\n';
  try {
    const res = await api('/api/diagnostics/ping?host=' + encodeURIComponent(host) + '&count=4');
    out.textContent = list(res.logs).join('\n');
  } catch (err) {
    out.textContent = 'Ping error: ' + err.message;
  } finally {
    pingRunning = false;
    btn.disabled = false;
  }
});

$('copy-ping-output')?.addEventListener('click', async () => {
  const content = $('ping-output')?.textContent;
  if (!content) return;
  try {
    await navigator.clipboard.writeText(content);
    notify('Ping output copied.');
  } catch {
    notify('Could not copy output.');
  }
});

document.querySelectorAll('.chip[data-ping]').forEach(chip => {
  chip.addEventListener('click', () => {
    $('ping-host').value = chip.dataset.ping;
    $('ping-form').requestSubmit();
  });
});

let dnsRunning = false;
$('dns-form')?.addEventListener('submit', async (e) => {
  e.preventDefault();
  if (dnsRunning) return;
  const domain = $('dns-domain').value.trim() || 'google.com';
  dnsRunning = true;
  const btn = $('dns-form').querySelector('button[type=submit]');
  btn.disabled = true;
  put('dns-results', el('p', { class: 'field-hint' }, 'Benchmarking DNS servers for ' + domain + '…'));
  try {
    const res = await api('/api/diagnostics/dns?name=' + encodeURIComponent(domain));
    const cards = list(res.tests).map(t => {
      const timeClass = !t.success ? 'fail' : t.elapsedMs < 30 ? 'fast' : t.elapsedMs < 100 ? 'normal' : 'slow';
      return el('div', { class: 'dns-card' },
        el('div', {},
          el('div', { class: 'dns-card-name' }, t.server),
          el('div', { class: 'dns-card-ips' }, t.success ? (list(t.addresses).join(', ') || 'Resolved') : t.error)
        ),
        el('div', { class: 'dns-card-time ' + timeClass }, t.success ? t.elapsedMs + ' ms' : 'Failed')
      );
    });
    put('dns-results', ...cards);
  } catch (err) {
    put('dns-results', el('p', { class: 'form-message error' }, err.message));
  } finally {
    dnsRunning = false;
    btn.disabled = false;
  }
});

document.querySelectorAll('.chip[data-dns]').forEach(chip => {
  chip.addEventListener('click', () => {
    $('dns-domain').value = chip.dataset.dns;
    $('dns-form').requestSubmit();
  });
});

$('clear-alerts')?.addEventListener('click', async () => {
  const btn = $('clear-alerts');
  btn.disabled = true;
  try {
    await api('/api/diagnostics/alerts/clear', { method: 'POST', body: '{}' });
    if (state) state.alerts = [];
    if (latest) latest.alerts = [];
    renderDiagnostics();
    notify('Alert history cleared.');
  } catch (err) {
    notify('Could not clear alerts: ' + err.message);
  } finally {
    btn.disabled = false;
  }
});

// Small invariant checks cover rate formatting and IPv6 endpoints without fabricated UI data.
$('copy-mobile-link').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText($('mobile-link').value); notify('Phone pairing link copied.'); }
  catch { $('mobile-link').focus(); $('mobile-link').select(); notify('Select and copy the phone pairing link.'); }
});
console.assert(bytes(0) === '0 B' && bytes(1000) === '1 KB', 'Byte formatting invariant');
console.assert(endpoint('::1', 443) === '[::1]:443', 'IPv6 endpoint invariant');
try { document.body.classList.toggle('menu-hidden', localStorage.getItem('flow-menu-hidden') === 'true'); } catch { }
navigate();
poll();
setInterval(showHealth, 1000);
