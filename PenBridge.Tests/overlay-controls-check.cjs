const {chromium}=require('playwright');
const fs=require('node:fs');
const assert=require('node:assert/strict');
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1194,height:834}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>localStorage.setItem('penbridge.preferences',JSON.stringify({liveStats:true,liveX:16,liveY:70})));
 const html=fs.readFileSync('PenBridge.Web/pad.html','utf8').replace('const CONFIG = window.__PENBRIDGE_CONFIG__ ||',`const CONFIG = ${JSON.stringify({mappingMode:'preserveAspectRatio',targetAspect:16/9,allowNonPen:true,mappingKey:'ui',monitors:[]})} ||`).replace(/^connect\(\);$/gm,'');
 await page.route('http://penbridge.test/',r=>r.fulfill({contentType:'text/html',body:html}));await page.goto('http://penbridge.test/');
 assert.equal(await page.locator('#fullscreenBtn').count(),0);assert.equal(await page.locator('#toolSetting').count(),0);
 await page.locator('#toolbarToggle').click();assert.ok((await page.locator('#toolbar').getAttribute('class')).includes('collapsed'));assert.equal(await page.locator('#settingsBtn').isVisible(),false);
 await page.locator('#toolbarToggle').click();await page.locator('#settingsBtn').click();assert.equal(await page.locator('#inputRateSetting option').count(),4);await page.locator('#closeSettings').click();
 await page.evaluate(()=>{socket={readyState:1,send(){}};document.querySelector('#pad').dispatchEvent(new PointerEvent('pointerdown',{pointerId:1,pointerType:'pen',clientX:300,clientY:300,pressure:.5,buttons:1,bubbles:true}));});
 await page.waitForFunction(()=>document.querySelector('#penLiveText').textContent.includes('samples/s'));
 const before=await page.locator('#penLiveReadout').boundingBox();await page.mouse.move(before.x+20,before.y+20);await page.mouse.down();await page.mouse.move(before.x+140,before.y+100);await page.mouse.up();const after=await page.locator('#penLiveReadout').boundingBox();assert.ok(after.x>before.x+80&&after.y>before.y+50);
 await page.screenshot({path:'PenBridge.Tests/overlay-controls-preview.png'});
 await page.locator('#closeLiveStats').click();assert.equal(await page.locator('#penLiveReadout').isHidden(),true);
 await page.evaluate(()=>setHud('연결됨','ok'));assert.ok((await page.locator('#hud').getAttribute('class')).includes('visible'));await page.waitForTimeout(2000);assert.ok(!(await page.locator('#hud').getAttribute('class')).includes('visible'));
 assert.deepEqual(errors,[]);console.log('PASS: removed controls, polling options, collapsible toolbar, transient HUD, movable/closable input readout');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
