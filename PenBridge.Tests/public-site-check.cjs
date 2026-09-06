const {chromium}=require('playwright');
const fs=require('node:fs');
const assert=require('node:assert/strict');
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1024,height:768}}),errors=[],requests=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{Object.defineProperty(navigator,'mediaDevices',{value:{getUserMedia:async()=>({getTracks:()=>[{stop(){}}]})}});Object.defineProperty(HTMLMediaElement.prototype,'srcObject',{get(){return this._stream},set(v){this._stream=v}});HTMLMediaElement.prototype.play=async()=>{};window.BarcodeDetector=class{static async getSupportedFormats(){return['qr_code']}async detect(){return[]}};});
 const html=fs.readFileSync('PenBridge.Web/dist/index.html','utf8');await page.route('https://penbridge.test/',r=>r.fulfill({contentType:'text/html',body:html}));
 await page.route('http://192.168.0.24:8080/connect?*',r=>{requests.push(r.request().url());return r.fulfill({contentType:'text/html',body:'connected'});});
 await page.goto('https://penbridge.test/');await page.locator('#address').fill('8.8.8.8');await page.locator('#connect').click();assert.ok((await page.locator('#status').textContent()).includes('사설 IP'));
 await page.locator('#address').fill('192.168.0.24:8080');await page.screenshot({path:'PenBridge.Tests/public-connect-preview.png'});
 await page.locator('#scan').click();await page.locator('#scanner').waitFor({state:'visible'});await page.evaluate(()=>acceptQr('http://192.168.0.24:8080'));await page.waitForURL('http://192.168.0.24:8080/connect?*');assert.equal(requests.length,1);const target=new URL(requests[0]);assert.equal(target.searchParams.get('assets'),'https://penbridge.test/');assert.deepEqual(errors,[]);
 console.log('PASS: private IP validation, Apple-style landing, camera QR scan, public asset handoff');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
