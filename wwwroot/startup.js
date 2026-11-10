'use strict';
(() => {
  let status = null, busy = false, loading = false;
  const readOnly = () => !state?.isAdministrator || !!state?.access?.isRemote;
  function renderStartup() {
    $('startup-enable').disabled = busy || readOnly() || !!status?.enabled || status?.owned === false;
    $('startup-disable').disabled = busy || readOnly() || !status?.enabled || status?.owned === false;
    $('startup-badge').textContent = status ? status.enabled ? 'Enabled' : 'Off' : 'Checking';
    $('startup-badge').className = 'badge ' + (status?.enabled ? 'good' : '');
    if (status) $('startup-status').textContent = status.message + (status.enabled ? ' Phone sharing at startup: ' + (status.shareMode ? 'on' : 'off') + '.' : '');
  }
  async function refreshStartup() {
    if (loading || busy || stopped) return;
    loading = true;
    try { status = await api('/api/startup', {signal: AbortSignal.timeout(35000)}); renderStartup(); }
    catch (error) { message('startup-message', error.message, 'error'); text('startup-badge', 'Unavailable'); }
    finally { loading = false; }
  }
  async function setStartup(enabled) {
    if (readOnly() || busy) return;
    busy = true; renderStartup(); message('startup-message', 'Updating Windows sign-in startup…');
    try {
      status = await api('/api/startup', {method: 'POST', signal: AbortSignal.timeout(35000), body: JSON.stringify({enabled})});
      message('startup-message', enabled ? 'Automatic startup enabled for Windows sign-in and unlock.' : 'Automatic startup disabled. This running monitor is unchanged.', 'success');
    } catch (error) { message('startup-message', error.message, 'error'); }
    finally { busy = false; renderStartup(); }
  }
  $('startup-enable').addEventListener('click', () => setStartup(true));
  $('startup-disable').addEventListener('click', () => setStartup(false));
  $('startup-refresh').addEventListener('click', refreshStartup);
  window.addEventListener('hashchange', () => { if (location.hash === '#settings') refreshStartup(); });
  setInterval(renderStartup, 2000);
  renderStartup(); refreshStartup();
})();
