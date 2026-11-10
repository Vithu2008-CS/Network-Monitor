// Run as a Playwright page function. Requires a running Flow server.
async (page) => {
  page.setDefaultTimeout(10000);
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.setViewportSize({width: 390, height: 844});
  await page.goto('http://localhost:5099/#applications');
  await page.reload();
  await page.locator('#app-search').fill('codex');
  const program = page.getByRole('button', {name: 'codex.exe', exact: true});
  await program.waitFor();
  await program.evaluate(button => { window.flowTestButton = button; });
  await page.waitForTimeout(2600);
  assert(await program.evaluate(button => button === window.flowTestButton), 'Live refresh replaced the program button');
  await program.click();
  await page.getByRole('dialog').waitFor();
  assert((await page.getByRole('dialog').innerText()).includes('Executable'), 'Missing program details');
  await page.getByLabel('Close application details').click();
  await page.locator('#app-search').fill('');
  await page.goto('http://localhost:5099/#history');
  await page.waitForFunction(() => document.querySelector('#history-chart svg'));
  assert((await page.locator('#history-totals').innerText()).includes('MB'), 'History usage did not load');
  await page.goto('http://localhost:5099/#network');
  await page.waitForFunction(() => document.querySelector('#adapter-list').textContent.includes('Wi-Fi'));
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Mobile page overflows horizontally');
  assert(errors.length === 0, errors.join('; '));
  await page.setViewportSize({width: 1440, height: 1000});
  await page.goto('http://localhost:5099/#overview');
  return {passed: true, checks: ['stable live buttons', 'app details', 'history', 'Wi-Fi', 'mobile fit', 'no JS errors']};
}
