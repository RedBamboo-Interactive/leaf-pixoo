const { chromium } = require('../web/node_modules/playwright');
const path = require('node:path');
const fs = require('node:fs');
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
        if (scenario === 'one') await page.getByRole('img', { name: /pixel face/ }).waitFor();
        if (scenario === 'one') await page.getByText('Bound Agent', { exact: true }).waitFor();
        if (scenario === 'two') await page.getByText('Emulator · Second Agent', { exact: true }).waitFor();
        if (scenario === 'two' && viewport === 'desktop') await page.getByRole('img', { name: /pixel face/ }).waitFor();
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({
          path: path.join(artifacts, `${scenario}-${viewport}.png`),
          fullPage: viewport === 'phone',
        });
        screenshots++;
        if (scenario === 'two' && viewport === 'phone') {
          await page.getByRole('button', { name: /Living room/ }).click();
          await page.getByRole('img', { name: /pixel face/ }).waitFor();
        }
        if (scenario === 'one' && viewport === 'desktop') {
          await page.getByRole('button', { name: 'speaking', exact: true }).click();
          await page.getByTestId('current-state').getByText('speaking', { exact: true }).waitFor();
          await page.waitForTimeout(250);
          const liveA = path.join(artifacts, 'live-animation-frame-a.png');
          const liveB = path.join(artifacts, 'live-animation-frame-b.png');
          await page.screenshot({ path: liveA });
          await page.waitForTimeout(220);
          await page.screenshot({ path: liveB });
          if (Buffer.compare(fs.readFileSync(liveA), fs.readFileSync(liveB)) === 0)
            throw new Error('LED preview did not advance between live screenshots');
          screenshots += 2;
          await page.getByLabel('Name').fill('Lounge display');
          await page.getByLabel('Name').press('Enter');
          await page.getByRole('button', { name: /Lounge display/ }).waitFor();
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
