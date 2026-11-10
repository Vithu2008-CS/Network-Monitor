'use strict';
(() => {
  let model = null, currentApp = null, loading = false, busy = false, activityId = -1;
  const cards = new Map();
  const readOnly = () => !state?.isAdministrator || !!state?.access?.isRemote;
  const canEnd = () => !!state?.isAdministrator && (!state?.access?.isRemote || !!state?.access?.canEndProcesses);
  const request = (url, options = {}) => api(url, {...options, signal: AbortSignal.timeout(45000)});
  const post = (url, data = {}) => request(url, {method: 'POST', body: JSON.stringify(data)});
  const rules = () => list(model?.rules);
  const find = id => rules().find(v => v.rule.appId === id);
  const allowed = app => !!app?.path && !/^[a-z]:\\windows\\/i.test(app.path) && !(app.name === 'dotnet.exe' && /[\\/]\.tools[\\/]dotnet[\\/]/i.test(app.path));
  const values = r => ({appId: r.appId, dailyLimitMb: r.dailyLimitMb, uploadLimitKbps: r.uploadLimitKbps, quotaAction: r.quotaAction, blocked: r.blocked, enabled: r.enabled});
  const setBusy = value => { busy = value; render(); };

  async function refresh() {
    if (loading || busy || stopped) return;
    loading = true;
    try { model = await request('/api/controls'); render(); }
    catch (error) { if (page === 'controls') message('controls-message', error.message, 'error'); }
    finally { loading = false; }
  }
  async function change(action, success) {
    if (readOnly() || busy) return;
    setBusy(true); message('controls-message', 'Updating Windows controls…');
    try { await action(); message('controls-message', success, 'success'); }
    catch (error) { message('controls-message', error.message, 'error'); }
    finally { setBusy(false); await refresh(); }
  }

  function render() {
    const locked = readOnly() || busy;
    for (const id of ['add-app-rule', 'restore-all-controls', 'save-daily-budget', 'reset-daily-budget', 'save-app-rule']) $(id).disabled = locked;
    $('daily-budget').disabled = readOnly();
    $('manage-app-rule').disabled = locked || !allowed(currentApp);
    $('manage-app-processes').disabled = !allowed(currentApp);
    if (!model) return;
    const daily = model.daily;
    if (document.activeElement !== $('daily-budget') && !$('daily-budget').dataset.dirty) $('daily-budget').value = daily.budgetGb;
    text('daily-budget-summary', bytes(daily.usedBytes) + ' counted today' + (daily.budgetGb ? ' of ' + bytes(daily.budgetGb * 1e9) + ' · ' + bytes(daily.remainingBytes) + ' remaining.' : '. No daily allowance set.'));
    text('control-engine-status', model.serviceError || (readOnly() ? 'Read-only viewing. Make changes on the laptop with administrator capture running.' : 'Windows app controls are available. Applied status is shown on each rule.'));
    if (page === 'controls') {
      put('controls-stats', stat('Configured rules', rules().length), stat('Network blocked', rules().filter(v => v.status === 'blocked').length), stat('Upload limited', rules().filter(v => v.status === 'limited').length), stat('Quota reached', rules().filter(v => v.exceeded && v.rule.enabled).length));
      renderRules(); renderActivity();
    }
    const nearBudget = daily.budgetGb > 0 && daily.usedBytes >= daily.budgetGb * 1e9 * .8;
    const quotas = rules().filter(v => v.rule.enabled && v.exceeded).length;
    $('overview-budget-notice').hidden = !nearBudget && !quotas;
    text('overview-budget-notice', (nearBudget ? (daily.exceeded ? 'Daily laptop budget reached. ' : '80% of the daily laptop budget used. ') : '') + (quotas ? quotas + ' application daily allowance(s) reached. ' : '') + 'Review App controls for details.');
    if (currentApp) {
      const view = find(currentApp.id);
      text('app-control-status', !allowed(currentApp) ? 'This program is protected or does not expose a controllable executable path.' : view ? 'Rule: ' + view.status + ' · ' + bytes(view.usedBytes) + ' counted today.' : 'No app rule configured. Limits apply to all instances of this executable.');
    }
  }
  function stat(label, value) { return el('div', {class: 'control-stat'}, el('span', {}, label), el('strong', {}, count(value))); }
  function renderRules() {
    const container = $('rules-list');
    if (!rules().length) {
      cards.clear(); container.replaceChildren(empty('No app rules yet', 'Choose Add app rule, or open a program and select Set limits & access.', 'shield')); return;
    }
    if (!cards.size) container.replaceChildren();
    const keep = new Set();
    for (const view of rules()) {
      const rule = view.rule; keep.add(rule.appId);
      let card = cards.get(rule.appId);
      if (!card) {
        card = el('section', {class: 'panel rule-card', 'data-rule-id': rule.appId},
          el('div', {class: 'rule-heading'}, el('h3', {}, rule.name), el('span', {class: 'badge control-status'})),
          el('p', {class: 'field-hint rule-path'}, rule.path), el('p', {class: 'rule-usage'}),
          el('progress', {class: 'rule-progress', max: '100', value: '0', 'aria-label': 'Daily app allowance used'}),
          el('p', {class: 'field-hint rule-meta'}), el('p', {class: 'form-message error rule-error', role: 'status'}),
          el('div', {class: 'rule-actions'}, ...[['edit', 'Edit'], ['reset', 'Reset today'], ['toggle', 'Pause rule'], ['delete', 'Delete rule']].map(([action, label]) => el('button', {class: 'button', type: 'button', 'data-rule-action': action}, label))));
        cards.set(rule.appId, card); container.append(card);
      }
      const badge = card.querySelector('.control-status'); badge.textContent = view.status; badge.className = 'badge control-status ' + (view.status === 'error' ? 'warning' : view.status === 'blocked' ? 'warning' : 'good');
      card.querySelector('.rule-usage').textContent = bytes(view.usedBytes) + (view.limitBytes > 0 ? ' of ' + bytes(view.limitBytes) + ' today · ' + bytes(view.remainingBytes) + ' remaining' : ' today · no quota');
      card.querySelector('.rule-progress').value = view.limitBytes > 0 ? Math.min(100, view.usedBytes / view.limitBytes * 100) : 0;
      card.querySelector('.rule-meta').textContent = (rule.uploadLimitKbps ? 'Upload cap ' + rule.uploadLimitKbps + ' KB/s' : 'No upload cap') + ' · ' + (rule.blocked ? 'Manual network block' : 'Quota action: ' + rule.quotaAction) + ' · resets at local midnight';
      card.querySelector('.rule-error').textContent = view.error || '';
      card.querySelector('[data-rule-action="toggle"]').textContent = rule.enabled ? 'Pause rule' : 'Enable rule';
      card.querySelectorAll('button').forEach(button => { button.disabled = readOnly() || busy; });
    }
    for (const [id, card] of cards) if (!keep.has(id)) { card.remove(); cards.delete(id); }
  }
  function renderActivity() {
    const entries = list(model.activity);
    if ((entries[0]?.id || 0) === activityId) return;
    activityId = entries[0]?.id || 0;
    put('control-activity', entries.length ? entries.slice(0, 50).map(entry => el('div', {class: 'activity-item'}, el('div', {}, el('strong', {}, entry.action), el('span', {class: 'muted'}, ' · ' + entry.app), el('p', {}, entry.detail)), el('time', {datetime: entry.time}, time(entry.time)))) : empty('No control activity yet', 'Rule changes, process actions, and quota alerts will appear here.', 'history'));
  }
  function editor(id = null) {
    if (readOnly()) return;
    const candidates = new Map(list(state?.apps).filter(allowed).map(a => [a.id, {id: a.id, name: a.name, path: a.path}]));
    for (const view of rules()) candidates.set(view.rule.appId, {id: view.rule.appId, name: view.rule.name, path: view.rule.path});
    $('rule-app').replaceChildren(...Array.from(candidates.values()).sort((a, b) => a.name.localeCompare(b.name)).map(a => el('option', {value: a.id}, a.name)));
    if (id && candidates.has(id)) $('rule-app').value = id;
    $('rule-app').disabled = !!id;
    fillEditor(); message('rule-message', ''); $('rule-dialog').showModal();
  }
  function fillEditor() {
    const id = $('rule-app').value, rule = find(id)?.rule;
    $('rule-daily').value = rule?.dailyLimitMb ?? 0; $('rule-upload').value = rule?.uploadLimitKbps ?? 0;
    $('rule-overflow').value = rule?.quotaAction ?? 'alert'; $('rule-blocked').checked = rule?.blocked ?? false; $('rule-enabled').checked = rule?.enabled ?? true;
    text('rule-path', rule?.path || list(state?.apps).find(a => a.id === id)?.path || 'No eligible application is currently available.');
  }
  async function processList() {
    if (!currentApp) return;
    message('process-message', 'Reading current process identities…');
    try {
      const rows = await request('/api/controls/apps/' + encodeURIComponent(currentApp.id) + '/processes');
      const items = rows.map(target => {
        const button = el('button', {class: 'button danger-button', type: 'button', disabled: !canEnd() || !target.canEnd ? '' : null}, 'End process');
        button.addEventListener('click', async () => {
          if (!canEnd() || !window.confirm('End ' + target.name + ' (PID ' + target.pid + ')? Unsaved work in this process may be lost.')) return;
          button.disabled = true;
          try { await post('/api/controls/apps/' + encodeURIComponent(currentApp.id) + '/end', {pid: target.pid, startToken: target.startToken}); message('process-message', 'Process ended.', 'success'); await refresh(); await processList(); }
          catch (error) { message('process-message', error.message, 'error'); button.disabled = false; }
        });
        return el('div', {class: 'activity-item'}, el('div', {}, el('strong', {}, target.name + ' · PID ' + target.pid), el('p', {}, target.reason || 'Verified running executable'), el('p', {class: 'process-metrics'}, 'RAM ' + bytes(target.memoryBytes) + ' · CPU time ' + num(target.cpuSeconds).toFixed(1) + ' s')), button);
      });
      put('process-list', items.length ? items : empty('No matching processes', 'The application may have exited. Refresh after it starts again.', 'apps'));
      message('process-message', 'Process identities are checked again before every action.');
    } catch (error) { message('process-message', error.message, 'error'); }
  }

  $('add-app-rule').addEventListener('click', () => editor());
  $('manage-app-rule').addEventListener('click', () => currentApp && editor(currentApp.id));
  $('manage-app-processes').addEventListener('click', () => { text('process-title', currentApp?.name || 'Running processes'); $('process-dialog').showModal(); processList(); });
  $('close-rule-dialog').addEventListener('click', () => $('rule-dialog').close());
  $('close-process-dialog').addEventListener('click', () => $('process-dialog').close());
  $('refresh-processes').addEventListener('click', processList);
  $('rule-app').addEventListener('change', fillEditor);
  $('rule-form').addEventListener('submit', async event => {
    event.preventDefault(); if (readOnly() || busy || !$('rule-form').reportValidity()) return;
    const data = {appId: $('rule-app').value, dailyLimitMb: Number($('rule-daily').value), uploadLimitKbps: Number($('rule-upload').value), quotaAction: $('rule-overflow').value, blocked: $('rule-blocked').checked, enabled: $('rule-enabled').checked};
    if (!data.appId) { message('rule-message', 'Select an observed application.', 'error'); return; }
    if (data.enabled && (data.blocked || data.quotaAction === 'block' || data.uploadLimitKbps > 0) && !window.confirm('Apply network controls to all instances of this program? Active transfers may be interrupted.')) return;
    setBusy(true); message('rule-message', 'Saving rule and checking Windows enforcement…');
    try { await post('/api/controls/rules', data); $('rule-dialog').close(); message('controls-message', 'Rule saved. Check its applied status below.', 'success'); }
    catch (error) { message('rule-message', error.message, 'error'); }
    finally { setBusy(false); await refresh(); }
  });
  $('rules-list').addEventListener('click', event => {
    const button = event.target.closest('[data-rule-action]'), id = button?.closest('[data-rule-id]')?.dataset.ruleId;
    if (!id || readOnly() || busy) return;
    const rule = find(id)?.rule; if (!rule) return;
    if (button.dataset.ruleAction === 'edit') editor(id);
    else if (button.dataset.ruleAction === 'reset') change(() => post('/api/controls/rules/' + encodeURIComponent(id) + '/reset'), 'Daily quota reset. Usage history was retained.');
    else if (button.dataset.ruleAction === 'toggle') change(() => post('/api/controls/rules', {...values(rule), enabled: !rule.enabled}), 'Rule updated.');
    else if (window.confirm('Delete this rule and restore this app’s network access? History is kept.')) change(() => post('/api/controls/rules/' + encodeURIComponent(id) + '/delete'), 'Rule removed.');
  });
  $('refresh-controls').addEventListener('click', refresh);
  $('restore-all-controls').addEventListener('click', () => { if (window.confirm('Disable all Flow app rules and restore their network access? Other Windows firewall rules will stay unchanged.')) change(() => post('/api/controls/restore'), 'All Flow app controls disabled. Access restored.'); });
  $('daily-budget').addEventListener('input', () => { $('daily-budget').dataset.dirty = 'true'; });
  $('daily-form').addEventListener('submit', event => {
    event.preventDefault(); if (!$('daily-form').reportValidity()) return;
    change(async () => { await post('/api/controls/daily', {dailyBudgetGb: Number($('daily-budget').value)}); delete $('daily-budget').dataset.dirty; }, 'Daily laptop budget updated.');
  });
  $('reset-daily-budget').addEventListener('click', () => change(() => post('/api/controls/daily/reset'), 'Daily budget counter reset. Usage history was retained.'));
  let theme = 'light'; try { theme = localStorage.getItem('flow-theme') || 'light'; } catch { }
  $('appearance-theme').value = theme === 'dark' ? 'dark' : 'light'; document.documentElement.dataset.theme = $('appearance-theme').value;
  $('appearance-theme').addEventListener('change', () => { document.documentElement.dataset.theme = $('appearance-theme').value; try { localStorage.setItem('flow-theme', $('appearance-theme').value); } catch { } });
  window.flowControls = {
    render,
    selectApp(app) { currentApp = app; render(); },
    access(app) { return {allowed: !readOnly() && allowed(app), canEnd: canEnd() && allowed(app), reason: readOnly() ? 'Limit changes are available on the laptop only.' : allowed(app) ? '' : 'Protected Windows component or executable path unavailable.', endReason: !canEnd() ? 'Use the private phone link or start the laptop monitor as administrator.' : allowed(app) ? '' : 'Protected Windows component or executable path unavailable.'}; },
    openRule(app) { currentApp = app; editor(app.id); },
    openProcesses(app) { currentApp = app; text('process-title', app.name + ' · processes'); $('process-dialog').showModal(); processList(); }
  };
  render(); refresh();
  setInterval(() => { if (!document.hidden && !stopped) refresh(); }, 5000);
})();
