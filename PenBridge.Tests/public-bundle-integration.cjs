const {chromium}=require('playwright');
const fs=require('node:fs');
const assert=require('node:assert/strict');
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1194,height:834}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 const loader=fs.readFileSync('PenBridge.Web/dist/pad-loader.js','utf8');await page.route('https://tharu8813.github.io/PenBridge/pad-loader.js',r=>r.fulfill({contentType:'text/javascript',body:loader}));
 await page.goto('http://127.0.0.1:18080/connect?assets=https%3A%2F%2Ftharu8813.github.io%2FPenBridge%2F');
 await page.locator('#settingsBtn').waitFor({state:'visible'});await page.waitForFunction(()=>document.querySelector('#statusText').textContent==='연결됨');
 assert.equal(await page.evaluate(()=>CONFIG.mappingKey.includes('0,0')),true);assert.equal(await page.locator('#toolSetting').count(),0);assert.deepEqual(errors,[]);
 console.log('PASS: local bootstrap loaded external public bundle, fetched config, and opened WebSocket');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
