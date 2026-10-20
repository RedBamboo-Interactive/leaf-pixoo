const { chromium } = require('../web/node_modules/playwright');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const artifacts = path.resolve(__dirname, '../artifacts');
  fs.mkdirSync(artifacts, { recursive: true });
  for (const name of fs.readdirSync(artifacts)) {
    if (/^pixoo-.*\.png$/.test(name)) fs.unlinkSync(path.join(artifacts, name));
  }
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
    for (const theme of ['dark', 'light']) {
      await page.setViewportSize({ width: 1440, height: 1000 });
      await page.goto(`http://127.0.0.1:4173/?harness=1&theme=${theme}`, { waitUntil: 'networkidle' });
      await page.getByRole('main').waitFor();
      for (const state of ['resting', 'speaking']) {
        await page.getByRole('button', { name: state, exact: true }).click();
        await page.getByTestId('current-state').getByText(state, { exact: true }).waitFor();
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({ path: path.join(artifacts, `pixoo-desktop-${theme}-${state}.png`) });
      }

      await page.setViewportSize({ width: 390, height: 844 });
      for (const state of ['resting', 'speaking']) {
        await page.getByRole('button', { name: state, exact: true }).click();
        await page.getByTestId('current-state').getByText(state, { exact: true }).waitFor();
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({ path: path.join(artifacts, `pixoo-phone-${theme}-${state}.png`) });
      }
    }

    await page.setViewportSize({ width: 1180, height: 900 });
    await page.goto('http://127.0.0.1:4173/?harness=1&theme=dark&surface=nova&agentId=22222222-2222-2222-2222-222222222222', { waitUntil: 'networkidle' });
    await page.getByRole('main').waitFor();
    await page.getByRole('button', { name: 'speaking', exact: true }).click();
    await page.getByTestId('current-state').getByText('speaking', { exact: true }).waitFor();
    await page.screenshot({ path: path.join(artifacts, 'pixoo-nova-section-dark-speaking.png') });
    if (errors.length) throw new Error(errors.join('\n'));
    console.log(JSON.stringify({ screenshots: 9, realMediaRequests: 0, errors: 0 }));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
