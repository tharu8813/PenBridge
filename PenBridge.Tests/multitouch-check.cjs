const {chromium}=require('playwright');
const fs=require('node:fs');
const assert=require('node:assert/strict');
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1194,height:834}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 const html=fs.readFileSync('PenBridge.Web/pad.html','utf8').replace('const CONFIG = window.__PENBRIDGE_CONFIG__ ||',`const CONFIG = ${JSON.stringify({mappingMode:'preserveAspectRatio',targetAspect:16/9,allowNonPen:true,mappingKey:'multi',monitors:[]})} ||`).replace(/^connect\(\);$/gm,'');
 await page.route('http://penbridge.test/',r=>r.fulfill({contentType:'text/html',body:html}));await page.goto('http://penbridge.test/');
 await page.evaluate(()=>{window.samples=[];socket={readyState:1,send:s=>window.samples.push(JSON.parse(s))};const p=document.querySelector('#pad');const fire=(target,type,id,x)=>target.dispatchEvent(new PointerEvent(type,{pointerId:id,pointerType:'touch',clientX:x,clientY:400,pressure:.5,buttons:type==='pointerup'?0:1,bubbles:true}));fire(p,'pointerdown',11,250);fire(p,'pointerdown',22,750);fire(p,'pointermove',11,300);fire(p,'pointermove',22,700);fire(window,'pointerup',11,300);fire(p,'pointermove',22,650);fire(window,'pointerup',22,650);});
 const samples=await page.evaluate(()=>window.samples);assert.deepEqual(samples.map(s=>s.pointerId),[11,22,11,22,11,22,22]);assert.equal(await page.evaluate(()=>activePointers.size),0);
 await page.locator('#settingsBtn').click();await page.locator('#touchTrailColorSetting').fill('#34c759');await page.locator('#touchTrailColorSetting').dispatchEvent('change');
 assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('penbridge.preferences')).touchTrailColor),'#34c759');
 await page.locator('#touchTrailColorSetting').scrollIntoViewIfNeeded();await page.screenshot({path:'PenBridge.Tests/multitouch-preview.png'});
 assert.deepEqual(errors,[]);console.log('PASS: two simultaneous touch streams, independent release, separate finger trail color');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
