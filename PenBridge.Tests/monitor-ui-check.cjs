const { chromium } = require('playwright');
const fs = require('fs');
const assert = require('node:assert/strict');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1194, height: 834 } });
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    const config = { mappingMode: 'preserveAspectRatio', targetAspect: 16 / 9, allowNonPen: false,
      mappingKey: 'primary', selectedMonitor: 'primary',
      monitors: [{ id: 'primary', label: '주 모니터 · 1920×1080' }, { id: 'left', label: '보조 모니터 · 1280×1024' }] };
    const html = fs.readFileSync('PenBridge.Web/pad.html', 'utf8')
      .replace('const CONFIG = window.__PENBRIDGE_CONFIG__ ||', `const CONFIG = ${JSON.stringify(config)} ||`).replace(/^connect\(\);$/gm, '');
    await page.route('http://penbridge.test/', r => r.fulfill({ contentType: 'text/html', body: html }));
    let reject = false;
    await page.route('**/monitor?*', r => reject ? r.fulfill({ status: 409 }) : r.fulfill({
      contentType: 'application/json', body: JSON.stringify({ ...config, selectedMonitor: 'left', mappingKey: 'left', targetAspect: 1.25 }) }));
    await page.goto('http://penbridge.test/');
    await page.getByRole('button', { name: '설정', exact: true }).click();
    assert.equal(await page.locator('#monitorSetting option').count(), 2);
    await page.locator('#monitorSetting').selectOption('left');
    await page.waitForFunction(() => document.querySelector('#monitorStatus').textContent === '대상 모니터를 변경했습니다.');
    assert.equal(await page.locator('#monitorSetting').inputValue(), 'left');
    reject = true;
    await page.locator('#monitorSetting').selectOption('primary');
    await page.waitForFunction(() => document.querySelector('#monitorStatus').textContent.includes('변경하지 못했습니다'));
    assert.equal(await page.locator('#monitorSetting').inputValue(), 'left');
    assert.equal(await page.locator('#monitorSetting').isDisabled(), false);
    await page.screenshot({ path: 'PenBridge.Tests/remote-monitor-preview.png' });
    assert.deepEqual(errors, []);
    console.log('PASS: monitor inventory, selection, failure rollback, enabled controls, no JS errors');
  } finally { await browser.close(); }
})();
