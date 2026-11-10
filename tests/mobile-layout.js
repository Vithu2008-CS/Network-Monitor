async (browserPage) => {
  const context = await browserPage.context().browser().newContext({hasTouch: true, isMobile: true});
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const check = (ok, message) => { if (!ok) throw new Error(message); };
  const sample = {id: 'reader', name: 'Reader.exe', path: 'C:/Examples/Reader.exe', pids: [42], connectionCount: 1,
    receivedBytes: 2380000, sentBytes: 70000, downloadBps: 18000, uploadBps: 2000};
  const apps = [sample, {...sample, id: 'background', name: 'ExampleWithAVeryLongExecutableNameForWrapping.exe', pids: [], connectionCount: 0, downloadBps: 0, uploadBps: 0}];
  const connections = [{app: sample.name, pid: 42, protocol: 'TCP', localAddress: '2001:db8:1234:5678:abcd:ef12:3456:7890', localPort: 51422,
    remoteAddress: '2001:db8::2', remotePort: 443, state: 'Established'}];
  const fixture = {
    '/api/state': {summary: {activeApps: 2}, apps, connections, series: [], isAdministrator: true,
      collector: {status: 'running'}, access: {isRemote: true, canEndProcesses: true}},
    '/api/controls': {daily: {budgetGb: 0, usedBytes: 0}, rules: []},
    '/api/startup': {enabled: false},
    '/api/controls/apps/reader/processes': []
  };
  const widths = [320, 360, 390, 430, 700];
  try {
    await context.route('**/api/**', route => {
      const path = new URL(route.request().url()).pathname;
      if (route.request().method() !== 'GET' || !(path in fixture)) {
        errors.push('Unexpected API request: ' + path);
        return route.abort();
      }
      return route.fulfill({json: fixture[path]});
    });
    await page.goto('http://localhost:5099/#applications');
    const rows = page.locator('#applications-table tbody tr');
    await rows.first().waitFor();
    for (const width of widths) {
      await page.setViewportSize({width, height: 812});
      const fits = await rows.evaluateAll(items => items.every(row => {
        const bounds = row.getBoundingClientRect();
        return [...row.querySelectorAll('td, button')].every(node => {
          const rect = node.getBoundingClientRect();
          return rect.left >= bounds.left - 1 && rect.right <= bounds.right + 1 &&
            rect.right <= innerWidth && node.scrollWidth <= node.clientWidth + 1;
        });
      }));
      check(fits, 'App content clipped at ' + width);
      check(await page.evaluate(() => document.documentElement.scrollWidth === innerWidth), 'Page overflow at ' + width);
      check(await rows.first().getByRole('button', {name: 'Limits'}).isDisabled(), 'Phone limit access');
      const end = rows.first().getByRole('button', {name: 'End process'});
      check(await end.isEnabled() && (await end.boundingBox()).height >= 44, 'Phone action target');
    }
    await page.setViewportSize({width: 390, height: 844});
    const nav = page.getByRole('navigation', {name: 'Mobile navigation'});
    await page.locator('#app-filter').selectOption('transferring');
    check(await rows.count() === 1, 'Traffic filter');
    await page.locator('#app-search').fill('no-match');
    await page.getByText('No matching applications', {exact: true}).waitFor();
    await page.locator('#app-search').fill('');
    const button = rows.first().getByRole('button', {name: sample.name});
    const original = await button.elementHandle();
    await page.waitForResponse(response => new URL(response.url()).pathname === '/api/state');
    check(await button.evaluate((node, previous) => node === previous, original), 'Polling replaced focused target');
    await button.click();
    await page.getByRole('dialog').getByText('Established', {exact: true}).waitFor();
    await page.getByRole('button', {name: 'Close application details'}).click();
    await rows.first().getByRole('button', {name: 'End process'}).click();
    await page.getByText('No matching processes', {exact: true}).waitFor();
    await page.getByRole('button', {name: 'Close process manager'}).click();
    await nav.getByRole('link', {name: 'Connections'}).click();
    await page.locator('#connections-table tbody tr').waitFor();
    check(await page.locator('#connections-table .endpoint').first().evaluate(td => td.scrollWidth <= td.clientWidth + 1), 'IPv6 endpoint clipped');
    await nav.getByRole('button', {name: 'More pages'}).click();
    await page.locator('#sidebar [href="#settings"]').click();
    await page.locator('#appearance-theme').selectOption('dark');
    await nav.getByRole('link', {name: 'Overview'}).click();
    await page.locator('#overview-apps .app-list').waitFor();
    check(await page.locator('html').getAttribute('data-theme') === 'dark', 'Dark appearance');
    await page.setViewportSize({width: 844, height: 390});
    check(await nav.isVisible(), 'Phone navigation after rotation');
    await nav.getByRole('link', {name: 'Apps'}).click();
    check(await rows.first().evaluate(tr => getComputedStyle(tr).display === 'grid'), 'Landscape app layout');
    await page.setViewportSize({width: 1440, height: 900});
    check(await nav.isHidden() && await rows.first().evaluate(tr => getComputedStyle(tr).display === 'table-row'), 'Desktop table');
    check(errors.length === 0, errors.join(', '));
    return {passed: true, phoneWidths: widths, landscape: [844, 390], desktopWidth: 1440};
  } finally { await context.close(); }
}
