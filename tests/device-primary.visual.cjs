const { chromium } = require('../web/node_modules/playwright');
const path = require('node:path');
const { chromiumLaunchOptions, outputDirectory } = require('./visual-output.cjs');

(async () => {
  const artifacts = outputDirectory('device-primary');
  const browser = await chromium.launch(chromiumLaunchOptions());
  const errors = [];
  let screenshots = 0;
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 1 });
    const page = await context.newPage();
    page.on('console', message => { if (message.type() === 'error') errors.push(`console: ${message.text()}`); });
    page.on('pageerror', error => errors.push(`page: ${error.message}`));
    for (const scenario of ['empty', 'one', 'two']) {
      for (const viewport of ['desktop', 'phone']) {
        await page.setViewportSize(viewport === 'desktop' ? { width: 1440, height: 1000 } : { width: 390, height: 844 });
        await page.goto(`http://127.0.0.1:4173/?harness=1&surface=nova&scenario=${scenario}&theme=dark`, { waitUntil: 'networkidle' });
        if (scenario === 'empty') await page.getByText('No displays yet', { exact: true }).waitFor();
        else await page.getByRole('img', { name: /pixel face/ }).waitFor();
        if (scenario === 'one') await page.getByText('Bound Agent', { exact: true }).waitFor();
        if (scenario === 'two') await page.getByText('Emulator · Second Agent', { exact: true }).waitFor();
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({
          path: path.join(artifacts, `${scenario}-${viewport}.png`),
          fullPage: viewport === 'phone',
        });
        screenshots++;
        if (scenario === 'one' && viewport === 'desktop') {
          await page.getByLabel('Name').fill('Lounge display');
          await page.getByLabel('Name').press('Enter');
          await page.getByRole('button', { name: 'Select Lounge display' }).waitFor();
          await page.getByRole('button', { name: 'Change' }).click();
          await page.getByRole('button', { name: 'Select Second Agent' }).click();
          await page.getByText('Emulator · Second Agent', { exact: true }).waitFor();
        }
      }
    }
    if (errors.length) throw new Error(errors.join('\n'));
    console.log(JSON.stringify({ screenshots, scenarios: 3, errors: 0 }));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
