async (browserPage) => {
  const context = await browserPage.context().browser().newContext({viewport: {width: 1440, height: 1000}, hasTouch: true});
  const page = await context.newPage();
  const failures = [];
  const exports = [];
  page.on('pageerror', error => failures.push(error.message));
  const check = (ok, message) => { if (!ok) throw new Error(message); };
  const daily = [{date: '2026-11-08', receivedBytes: 1500000000, sentBytes: 200000000, sampledMinutes: 128},
    {date: '2026-11-10', receivedBytes: 120000000, sentBytes: 10000000, sampledMinutes: 20}];
  const apps = [{id: 'browser', name: 'Browser.exe', path: 'C:/Example/Browser.exe', receivedBytes: 800000000, sentBytes: 20000000},
    {id: 'sync', name: 'CloudSyncWithALongExecutableName.exe', path: 'C:/Example/CloudSync.exe', receivedBytes: 20000000, sentBytes: 110000000}];
  try {
    await context.route('**/api/**', route => {
      const url = new URL(route.request().url());
      if (route.request().method() !== 'GET') { failures.push('Unexpected mutation'); return route.abort(); }
      if (url.pathname === '/api/history') {
        const start = url.searchParams.get('start') || '2026-11-04';
        const end = url.searchParams.get('end') || '2026-11-10';
        if (start > end) return route.fulfill({status: 400, json: {message: 'Choose an ordered date range.'}});
        const days = daily.filter(day => day.date >= start && day.date <= end);
        return route.fulfill({json: {range: url.searchParams.get('range'), startDate: start, endDate: end, daily: days,
          apps: start === end ? [apps[0]] : apps, totals: {receivedBytes: days.reduce((n, d) => n + d.receivedBytes, 0), sentBytes: days.reduce((n, d) => n + d.sentBytes, 0)},
          previousTotals: {receivedBytes: 1000000000, sentBytes: 100000000}, previousRecordedDays: 2,
          storage: {engine: 'SQLite', file: 'C:/Example/Flow/data/usage.db', databaseBytes: 286720, firstDay: '2026-10-01', lastDay: '2026-11-10', recordedDays: 22, pendingMinutes: 0}}});
      }
      if (url.pathname === '/api/export') {
        exports.push(url.search);
        return route.fulfill({contentType: 'text/csv', body: 'Kind,Date,Program,Executable,Download bytes,Upload bytes\r\n'});
      }
      return route.fulfill({json: url.pathname === '/api/state' ? {summary: {}, apps: [], series: [], collector: {status: 'running'}} :
        url.pathname === '/api/controls' ? {daily: {}, rules: []} : {enabled: false}});
    });
    await page.goto('http://localhost:5099/#history');
    await page.getByText('SQLite · saved on this laptop', {exact: true}).waitFor();
    await page.getByRole('button', {name: 'Hide menu', exact: true}).filter({visible: true}).last().click();
    check(await page.locator('#sidebar').isHidden(), 'Desktop menu should hide');
    await page.getByRole('button', {name: 'Show menu', exact: true}).click();
    check(await page.locator('#sidebar').isVisible(), 'Desktop menu should reopen');
    await page.locator('#history-search').fill('CloudSync');
    check(await page.locator('#history-apps tbody tr').count() === 1, 'Saved application search');
    await page.locator('#history-search').fill('');
    await page.locator('#history-sort').selectOption('upload');
    check((await page.locator('#history-apps tbody tr').first().innerText()).includes('CloudSync'), 'Upload ranking');
    await page.getByRole('button', {name: 'View app usage for 2026-11-08'}).click();
    await page.waitForFunction(() => document.querySelector('#history-app-caption').textContent.includes('1 of 1'));
    check(await page.locator('#history-start').inputValue() === '2026-11-08' && await page.locator('#history-end').inputValue() === '2026-11-08', 'Daily drilldown dates');
    const download = page.waitForEvent('download');
    await page.getByRole('button', {name: 'Export CSV'}).click();
    await download;
    check(exports[0].includes('start=2026-11-08') && exports[0].includes('end=2026-11-08'), 'Export uses inspected day');
    await page.locator('#history-start').fill('2026-11-10');
    await page.locator('#history-end').fill('2026-11-08');
    await page.getByRole('button', {name: 'Apply dates'}).click();
    await page.getByText('Choose an ordered date range.', {exact: false}).waitFor();
    await page.getByRole('button', {name: '7 days', exact: true}).click();
    await page.locator('#history-days tbody tr').first().waitFor();
    for (const width of [320, 390, 768, 1440]) {
      await page.setViewportSize({width, height: 1000});
      check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Report page overflow at ' + width);
      check(await page.locator('#history-apps .app-secondary').first().evaluate(node => node.scrollWidth <= node.clientWidth + 1), 'Report executable path overflow at ' + width);
    }
    await page.setViewportSize({width: 390, height: 844});
    await page.locator('#header-menu-toggle').click();
    await page.locator('#menu-close').click();
    check(await page.locator('#sidebar').isHidden(), 'Mobile menu closes');
    check(failures.length === 0, failures.join(', '));
    return {passed: true, checks: ['desktop and mobile menu', 'date drilldown', 'search and sort', 'CSV range', 'date errors', 'responsive report']};
  } finally { await context.close(); }
}
