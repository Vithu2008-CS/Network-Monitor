// Playwright page function. Uses only the isolated test executable's disabled rule.
async (page) => {
  page.setDefaultTimeout(12000);
  await page.setViewportSize({width: 1440, height: 1000});
  await page.goto('http://localhost:5099/#controls');
  await page.reload();
  await page.waitForFunction(() => document.querySelector('#control-engine-status').textContent.includes('available'));
  await page.getByRole('button', {name: 'Add app rule', exact: true}).click();
  const id = await page.locator('#rule-app option').evaluateAll(options => options.find(o => o.textContent === 'Probe.exe')?.value);
  if (!id) throw new Error('Run tests/control_smoke.py to populate the isolated probe first.');
  await page.locator('#rule-app').selectOption(id);
  await page.locator('#rule-daily').fill('100');
  await page.locator('#rule-enabled').uncheck();
  await page.getByRole('button', {name: 'Save rule', exact: true}).click();
  await page.waitForFunction(() => document.querySelector('#rules-list').textContent.includes('100 MB'));
  await page.locator('#rules-list [data-rule-action=edit]').click();
  await page.locator('#rule-daily').fill('250');
  await page.getByRole('button', {name: 'Save rule', exact: true}).click();
  await page.waitForFunction(() => document.querySelector('#rules-list').textContent.includes('250 MB'));
  await page.locator('#rules-list [data-rule-action=reset]').click();
  await page.waitForFunction(() => document.querySelector('#controls-message').textContent.includes('reset'));
  // Use the API for cleanup so a native confirmation dialog cannot leave test state behind.
  const removed = await page.request.post('http://localhost:5099/api/controls/rules/' + id + '/delete', {headers: {'X-Flow-Request': '1'}, data: {}});
  if (!removed.ok()) throw new Error('Could not remove isolated test rule');
  await page.locator('[data-page=settings]').click();
  await page.locator('#appearance-theme').selectOption('dark');
  if (await page.evaluate(() => document.documentElement.dataset.theme) !== 'dark') throw new Error('Theme did not change');
  await page.locator('#appearance-theme').selectOption('light');
  await page.setViewportSize({width: 390, height: 844});
  await page.getByRole('button', {name: 'More pages', exact: true}).click();
  await page.locator('[data-page=controls]').click();
  if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)) throw new Error('Controls overflow on mobile');
  return {passed: true, checks: ['create', 'edit', 'reset', 'delete', 'theme', 'mobile fit']};
}
