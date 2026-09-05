const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { test } = require('node:test');
const html = fs.readFileSync(require('node:path').join(__dirname, '../PenBridge.Web/pad.html'), 'utf8');
function harness(windowOverrides={}) {
  const elements = new Map();
  function element() { return { style: {}, setAttribute(k,v){this[k]=v;}, pause(){}, removeAttribute(){}, load(){}, showModal(){this.open=true;}, close(){this.open=false;}, listeners: {}, addEventListener(t,f) { this.listeners[t]=f; }, getContext() { return new Proxy({}, {get: () => () => {}}); }, setPointerCapture() {}, hasPointerCapture() { return true; }, releasePointerCapture() {} }; }
  const window = Object.assign(element(), { innerWidth: 1000, innerHeight: 1000, devicePixelRatio: 2, __PENBRIDGE_DISABLE_VIDEO__:true }, windowOverrides);
  const document = Object.assign(element(), { getElementById(id) { if (!elements.has(id)) elements.set(id,element()); return elements.get(id); } });
  const context = vm.createContext({ window, document, URLSearchParams, location: {search: '', protocol:'http:',host:'test'}, performance: {now:()=>0}, setInterval(){}, clearTimeout(){}, setTimeout(){}, requestAnimationFrame(){}, WebSocket: {OPEN:1} });
  context.window.__PENBRIDGE_CONFIG__={mappingMode:'preserveAspectRatio', targetAspect:2, allowNonPen:true, mappingKey:'old'};
  vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/\nconnect\(\);/g, '\n'),context);
  vm.runInContext('var sent=[]; socket={readyState:1,send:s=>sent.push(JSON.parse(s))};',context);
  const run = code => vm.runInContext(code,context);
  const event = (id=1,x=250,y=400,buttons=1) => ({pointerId:id,clientX:x,clientY:y,buttons,pressure:0.5,pointerType:'pen',preventDefault(){}});
  const fire = (type,e) => (type==='pointerup'?window:elements.get('pad')).listeners[type](e);
  return {run,event,fire,elements,sent:()=>JSON.parse(run('JSON.stringify(sent)'))};
}
test('pressure curves preserve endpoints and hover never has pressure',()=>{const h=harness();assert.ok(h.run('pressureResponse(.25,true,"soft")')>.25);assert.ok(h.run('pressureResponse(.25,true,"firm")')<.25);assert.equal(h.run('pressureResponse(1,true,"normal")'),1);assert.equal(h.run('pressureResponse(.7,false,"constant")'),0);assert.equal(h.run('pressureResponse(0,true,"constant")'),.5);});
test('altitude and azimuth can supply missing tilt',()=>{const h=harness();assert.equal(h.run('readTilt({altitudeAngle:Math.PI/4,azimuthAngle:0}).x'),45);});
test('rotation and eraser barrel flags reach outgoing samples',()=>{const h=harness();h.fire('pointerdown',{...h.event(),twist:350,buttons:35});const s=h.sent()[0];assert.equal(s.rotation,350);assert.equal(s.eraser,true);assert.equal(s.barrel,true);});
test('predicted points remain local and never reach Windows',()=>{const h=harness();h.run('preferences.prediction=true');h.fire('pointerdown',h.event());h.fire('pointermove',{...h.event(1,300,400),getPredictedEvents:()=>[h.event(1,900,400)]});assert.equal(h.sent().at(-1).x,.3);assert.equal(h.sent().length,2);assert.equal(h.elements.get('predictionCursor').hidden,false);h.fire('pointerup',h.event());assert.equal(h.elements.get('predictionCursor').hidden,true);});
test('hover cursor hides when contact starts',()=>{const h=harness();h.fire('pointermove',h.event(1,300,400,0));assert.equal(h.elements.get('hoverCursor').hidden,false);h.fire('pointerdown',h.event());assert.equal(h.elements.get('hoverCursor').hidden,true);});
test('secure raw input selects only one movement stream',()=>{const h=harness({isSecureContext:true,onpointerrawupdate:null});assert.equal(h.elements.get('pad').listeners.pointermove,undefined);h.fire('pointerdown',h.event());h.fire('pointerrawupdate',{...h.event(),type:'pointerrawupdate'});assert.equal(h.sent().length,2);});
test('queued hover cannot follow down; rapid double tap and drag stay ordered',()=>{
  const h=harness(); h.fire('pointermove',h.event(1,250,400,0)); h.fire('pointerdown',h.event()); h.run('frame()');
  h.fire('pointerup',h.event()); h.fire('pointerdown',h.event()); h.fire('pointermove',h.event(1,350,450)); h.run('frame()'); h.fire('pointerup',h.event());
  assert.deepEqual(h.sent().map(s=>s.phase),['down','up','down','move','up']);
});
test('another pointer cannot move or cancel the active stroke',()=>{
  const h=harness(); h.fire('pointerdown',h.event()); h.fire('pointermove',h.event(2)); h.fire('pointercancel',h.event(2)); h.fire('pointerup',h.event(2)); h.run('frame()');
  assert.deepEqual(h.sent().map(s=>s.phase),['down']); assert.equal(h.run('currentlyDown'),true);
});
test('capture loss releases at last position with zero pressure and allows next tap',()=>{
  const h=harness(); h.fire('pointerdown',h.event()); h.fire('pointermove',h.event(1,350,450)); h.fire('lostpointercapture',h.event());
  const up=h.sent().at(-1); assert.equal(up.phase,'up'); assert.equal(up.x,0.35); assert.equal(up.y,0.4); assert.equal(up.pressure,0);
  h.fire('pointerdown',h.event()); assert.equal(h.sent().at(-1).phase,'down');
});
test('mapping change releases with old key and stretch aligns image and input',()=>{
  const h=harness(); h.fire('pointerdown',h.event()); h.run("preferences.mapping='stretch'; applyConfig({mappingMode:'stretch', targetAspect:2, allowNonPen:true,mappingKey:'new'})");
  assert.equal(h.sent().at(-1).mappingKey,'old'); assert.equal(h.sent().at(-1).phase,'up');
  assert.equal(h.elements.get('screenPreview').style.objectFit,'fill');
  h.fire('pointerdown',h.event(1,250,400)); assert.equal(h.sent().at(-1).y,0.4); assert.equal(h.sent().at(-1).mappingKey,'new');
});
test('native drag and touch gesture defaults are canceled',()=>{
  const h=harness(); for (const type of ['touchstart','touchmove','contextmenu','dragstart','selectstart','dblclick']) { let prevented=false; h.fire(type,{preventDefault(){prevented=true;}}); assert.equal(prevented,true); }
});
test('resize cancels contact before changing coordinates',()=>{
  const h=harness(); h.fire('pointerdown',h.event()); h.run('resize()'); assert.equal(h.sent().at(-1).phase,'up'); assert.equal(h.run('currentlyDown'),false);
});

test('settings sheet releases the stroke and blocks pad input',()=>{const h=harness();h.fire('pointerdown',h.event());h.elements.get('settingsBtn').listeners.click();assert.equal(h.sent().at(-1).phase,'up');h.fire('pointerdown',h.event());assert.equal(h.sent().at(-1).phase,'up');h.elements.get('closeSettings').listeners.click();h.fire('pointerdown',h.event());assert.equal(h.sent().at(-1).phase,'down');});

test('low latency catches up before the old 800ms threshold',()=>{const h=harness();const p=h.run('playbackPolicy(0.3,30,true)');assert.equal(p.seek,true);assert.ok(p.target<0.07);});
test('small buffer avoids seeks and returns to normal playback',()=>{const h=harness();const p=h.run('playbackPolicy(0.04,60,true)');assert.equal(p.seek,false);assert.equal(p.rate,1);});
test('moderate lag uses gentle catchup instead of repeated seeking',()=>{const h=harness();const p=h.run('playbackPolicy(0.14,30,true)');assert.equal(p.seek,false);assert.equal(p.rate,1.08);});
test('stable mode retains larger headroom',()=>{const h=harness();const p=h.run('playbackPolicy(0.3,30,false)');assert.equal(p.seek,false);assert.equal(p.target,0.25);assert.equal(p.rate,1);});

test('tablet mode aborts video and disables video-only controls',async()=>{const h=harness();h.run("var aborted=false; videoController={abort(){aborted=true;}}; preferences.mode='tablet'");await h.run('startVideo()');assert.equal(h.run('aborted'),true);assert.equal(h.elements.get('screenPreview').hidden,true);assert.equal(h.elements.get('fpsSetting').disabled,true);assert.equal(h.elements.get('tabletSurface').hidden,false);h.fire('pointerdown',h.event());assert.equal(h.sent().at(-1).phase,'down');});
test('returning to stream mode restores video controls',async()=>{const h=harness();h.run("preferences.mode='tablet'; syncMode(); preferences.mode='stream'");await h.run('startVideo()');assert.equal(h.elements.get('screenPreview').hidden,false);assert.equal(h.elements.get('fpsSetting').disabled,false);assert.equal(h.elements.get('tabletSurface').hidden,true);});
test('input lock releases current stroke and resumes cleanly',()=>{const h=harness();h.fire('pointerdown',h.event());h.elements.get('pauseInput').listeners.click();assert.equal(h.sent().at(-1).phase,'up');h.fire('pointerdown',h.event());assert.equal(h.sent().at(-1).phase,'up');h.elements.get('pauseInput').listeners.click();h.fire('pointerdown',h.event());assert.equal(h.sent().at(-1).phase,'down');});
test('contact moves preserve every sample before any animation frame',()=>{const h=harness();h.fire('pointerdown',h.event());for(let i=0;i<10;i++)h.fire('pointermove',h.event(1,300+i,400));assert.equal(h.sent().filter(s=>s.phase==='move').length,10);h.fire('pointerup',h.event());assert.equal(h.sent().at(-1).phase,'up');});
test('coalesced movement positions and pressure arrive in order',()=>{const h=harness();h.fire('pointerdown',h.event());const e=h.event();e.getCoalescedEvents=()=>[h.event(1,300,400),{...h.event(1,310,410),pressure:.8},h.event(1,320,420)];h.fire('pointermove',e);const moves=h.sent().filter(s=>s.phase==='move');assert.deepEqual(moves.map(s=>s.x),[.3,.31,.32]);assert.equal(moves[1].pressure,.8);h.fire('pointerup',h.event());assert.equal(h.sent().at(-1).phase,'up');});
test('calibration fits known offset and scale',()=>{const h=harness();const fit=h.run('fitCalibration(calibrationTargets.map(([x,y])=>[(x-.02)/1.05,(y+.01)/.95]))');assert.ok(Math.abs(fit.sx-1.05)<1e-10);assert.ok(Math.abs(fit.sy-.95)<1e-10);assert.ok(Math.abs(fit.ox-.02)<1e-10);assert.ok(Math.abs(fit.oy+.01)<1e-10);});
test('invalid calibration cannot replace saved correction',()=>{const h=harness();assert.equal(h.run('fitCalibration([[.5,.5],[.5,.5],[.5,.5],[.5,.5]])'),null);});
test('calibration only applies to the matching coordinate space',()=>{const h=harness();h.run('calibration={key:calibrationKey(),sx:1,sy:1,ox:.02,oy:0}');assert.equal(h.run('correctPosition(.5,.5).x'),.52);h.run('CONFIG.mappingKey="different"');assert.equal(h.run('correctPosition(.5,.5).x'),.5);});
test('calibration blocks PC input and cancels on resize',()=>{const h=harness();h.elements.get('calibrateBtn').listeners.click();h.fire('pointerdown',h.event());assert.equal(h.sent().length,0);h.run('resize()');assert.equal(h.run('calibrating'),false);});
test('margin keeps preview and normalized input aligned',()=>{const h=harness();h.run('preferences.margin=64; computeActiveRect()');assert.equal(h.elements.get('screenPreview').style.width,'872px');assert.equal(h.run('toSample({clientX:64,clientY:282},"down",true).x'),0);assert.equal(h.run('toSample({clientX:936,clientY:718},"down",true).y'),1);});
test('touches in margin do not start a stroke but an active drag can leave area',()=>{const h=harness();h.run('preferences.margin=64; computeActiveRect()');h.fire('pointerdown',h.event(1,10,400));assert.equal(h.sent().length,0);h.fire('pointerdown',h.event(1,300,400));h.fire('pointermove',h.event(1,0,400));assert.equal(h.sent().at(-1).x,0);h.fire('pointerup',h.event(1,0,400));assert.equal(h.sent().at(-1).phase,'up');});
test('margin change invalidates previous calibration',()=>{const h=harness();h.run('calibration={key:calibrationKey(),sx:1,sy:1,ox:.02,oy:0};preferences.margin=32;computeActiveRect()');assert.equal(h.run('correctPosition(.5,.5).x'),.5);});
test('trail lines distinguish separate strokes',()=>{const h=harness();h.fire('pointerdown',h.event());h.fire('pointerup',h.event());h.fire('pointerdown',h.event());assert.notEqual(h.run('trail[0].stroke'),h.run('trail[1].stroke'));});
test('invalid drawing preferences are bounded',()=>{const h=harness();h.run('preferences.margin=999;preferences.trailLife=-1;preferences.trailColor="invalid";normalizeDrawingPreferences()');assert.equal(h.run('preferences.margin'),96);assert.equal(h.run('preferences.trailLife'),150);assert.equal(h.run('preferences.trailColor'),'#0a84ff');});
test('two fingers keep independent contact streams',()=>{const h=harness();const touch=(id,x)=>({...h.event(id,x,400),pointerType:'touch'});h.fire('pointerdown',touch(11,200));h.fire('pointerdown',touch(22,700));h.fire('pointermove',touch(11,250));h.fire('pointermove',touch(22,750));h.fire('pointerup',touch(11,250));assert.equal(h.run('activePointers.size'),1);h.fire('pointermove',touch(22,800));h.fire('pointerup',touch(22,800));const sent=h.sent();assert.deepEqual(sent.map(s=>s.pointerId),[11,22,11,22,11,22,22]);assert.deepEqual(sent.map(s=>s.phase),['down','down','move','move','up','move','up']);assert.ok(sent.every(s=>s.pointerType==='touch'));});
test('finger trail uses only its separate color',()=>{const h=harness();h.run("preferences.trailColor='#112233';preferences.touchTrailColor='#ff9500'");const touch={...h.event(3,300,400),pointerType:'touch'};h.fire('pointerdown',touch);assert.equal(h.run('trail[0].pointerType'),'touch');assert.equal(h.run('preferences.trailColor'),'#112233');assert.equal(h.run('preferences.touchTrailColor'),'#ff9500');});
test('cancel releases only the matching finger',()=>{const h=harness();const a={...h.event(1,200,400),pointerType:'touch'},b={...h.event(2,700,400),pointerType:'touch'};h.fire('pointerdown',a);h.fire('pointerdown',b);h.fire('pointercancel',a);assert.equal(h.run('activePointers.size'),1);assert.equal(h.sent().at(-1).pointerId,1);assert.equal(h.sent().at(-1).phase,'up');});
test('capped input rate keeps the latest move before release',()=>{const h=harness();h.run("preferences.inputRate='60'");h.fire('pointerdown',h.event());for(let x=300;x<=390;x+=10)h.fire('pointermove',h.event(1,x,400));h.fire('pointerup',h.event(1,400,400));assert.deepEqual(h.sent().map(s=>s.phase),['down','move','up']);assert.equal(h.sent()[1].x,.39);});
