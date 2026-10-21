const fs = require('node:fs');
const path = require('node:path');

function outputDirectory(name) {
  const configured = process.env.REDLEAF_SCRATCH_DIR?.trim();
  if (configured && !path.isAbsolute(configured))
    throw new Error('REDLEAF_SCRATCH_DIR must be an absolute path.');
  const output = configured
    ? path.resolve(configured, 'leaf-pixoo', 'visual', name)
    : path.resolve(__dirname, '../artifacts', name);
  fs.mkdirSync(output, { recursive: true });
  return output;
}

function chromiumLaunchOptions() {
  const executablePath = process.env.PLAYWRIGHT_CHROMIUM_PATH?.trim();
  return executablePath ? { headless: true, executablePath } : { headless: true };
}

module.exports = { chromiumLaunchOptions, outputDirectory };
