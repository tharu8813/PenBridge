const { chromium } = require('playwright');
const fs = require('node:fs');
const assert = require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try {
  const page=await browser.newPage({viewport:{width:1194,height:834}});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  const html=fs.readFileSync('PenBridge.Web/pad.html','utf8').replace('const CONFIG = window.__PENBRIDGE_CONFIG__ ||',`const CONFIG = ${JSON.stringify({mappingMode:'preserveAspectRatio',targetAspect:16/9,allowNonPen:false,mappingKey:'test',monitors:[]})} ||`).replace(/^connect\(\);$/gm,'');
  await page.route('http://penbridge.test/',r=>r.fulfill({contentType:'text/html',body:html}));
  await page.goto('http://penbridge.test/');
  await page.locator('#settingsBtn').click();
  await page.locator('#pressureSetting').selectOption('soft');
  await page.locator('#liveStatsSetting').check();
  await page.locator('#closeSettings').click();
  await page.evaluate(()=>{
   window.samples=[];socket={readyState:1,send:s=>window.samples.push(JSON.parse(s))};
   const p=document.querySelector('#pad');
   p.dispatchEvent(new PointerEvent('pointerdown',{pointerId:1,pointerType:'pen',clientX:400,clientY:400,pressure:.25,tiltX:30,tiltY:20,twist:45,buttons:1,bubbles:true}));
   window.dispatchEvent(new PointerEvent('pointerup',{pointerId:1,pointerType:'pen',clientX:400,clientY:400,buttons:0,bubbles:true}));
  });
  const samples=await page.evaluate(()=>window.samples);
  assert.equal(samples[0].eraser,false);assert.equal(samples[0].rotation,45);assert.ok(samples[0].pressure>.25);
  await page.waitForFunction(()=>document.querySelector('#penLiveReadout').textContent.length>0);
  await page.locator('#settingsBtn').click();await page.locator('#penDiagnostics summary').click();
  await page.waitForFunction(()=>document.querySelector('#penDiagnosticsText').textContent.includes('45'));
  await page.locator('#penDiagnostics').scrollIntoViewIfNeeded();
  await page.screenshot({path:'PenBridge.Tests/advanced-pen-preview.png'});
  assert.deepEqual(errors,[]);console.log('PASS: advanced pen settings, mapped pressure, eraser, rotation, live diagnostics; synthetic browser input');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
