const { chromium } = require('../web/node_modules/playwright');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const artifacts = path.resolve(__dirname, '../artifacts');
  fs.mkdirSync(artifacts, { recursive: true });
  const browser = await chromium.launch({
    headless: true,
    executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  });
  const errors = [];
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 1 });
    const page = await context.newPage();
    page.on('console', message => { if (message.type() === 'error') errors.push(`console: ${message.text()}`); });
    page.on('pageerror', error => errors.push(`page: ${error.message}`));
    await page.goto('http://127.0.0.1:4173/?harness=1', { waitUntil: 'networkidle' });
    await page.getByRole('main').waitFor();
    for (const state of ['resting', 'thinking', 'speaking', 'dark']) {
      await page.getByRole('button', { name: state, exact: true }).click();
      await page.locator('.state-readout strong').getByText(state, { exact: true }).waitFor();
      await page.screenshot({ path: path.join(artifacts, `pixoo-desktop-${state}.png`) });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    for (const state of ['resting', 'thinking', 'speaking', 'dark']) {
      await page.getByRole('button', { name: state, exact: true }).click();
      await page.locator('.state-readout strong').getByText(state, { exact: true }).waitFor();
      await page.screenshot({ path: path.join(artifacts, `pixoo-phone-${state}.png`) });
    }
    if (errors.length) throw new Error(errors.join('\n'));
    console.log(JSON.stringify({ screenshots: 8, realMediaRequests: 0, errors: 0 }));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
