// Headless settings/bridge checks only; no installed profile, native keyboard,
// screen-reader tree or external service is used.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let passed=0;
  const check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);};
  try{
    const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',async route=>{
      const name=path.basename(new URL(route.request().url()).pathname);
      if(!/\.(html|js|css)$/.test(name))return route.abort();
      await route.fulfill({body:await fs.readFile(path.join(web,name)),contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'});
    });
    await page.addInitScript(()=>{
      const handlers=[];window.messages=[];window.holdDisplay=false;window.failDisplay=false;
      window.dispatchBridge=m=>handlers.forEach(h=>h({data:m}));
      window.reply=m=>window.dispatchBridge({type:'reply',requestId:m.requestId,error:window.failDisplay&&m.action==='displayOption'?'Synthetic save failure':undefined});
      window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
        window.messages.push(m);if(m.action==='displayOption'&&window.holdDisplay)return;
        queueMicrotask(()=>window.reply(m));
      }}};
    });
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='ready'));
    const settings={sheetUrl:'',webAppUrl:'',sheetMode:'date',sheetName:'',connected:false,volume:0,threshold:15,
      showFloatingTimer:true,theme:0,popup:4,placement:4,overlap:0,tracks:[{id:0,name:'Default'}],
      sounds:[0,1,2,3,4].map(kind=>({kind,track:0,behavior:0,volume:100,fadeOutAfterSeconds:10,defaultName:'Default'}))};
    await page.evaluate(settings=>window.dispatchBridge({type:'settings',settings}),settings);
    await page.locator('#tab-settings').click();
    check(!await page.locator('#viewerAutoHide').isChecked()&&await page.locator('#viewerAutoHideSeconds').inputValue()==='3','Missing legacy settings use off/3 without changing other defaults');
    await page.locator('#viewerAutoHide').check();
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='displayOption'&&m.data.option==='viewerAutoHide'&&m.data.value===1));
    check(await page.evaluate(()=>window.messages.filter(m=>m.action==='displayOption').at(-1).data.quiet),'Enabling autosaves quietly');
    await page.locator('#viewerAutoHideSeconds').fill('7');await page.locator('#viewerAutoHideSeconds').press('Tab');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='displayOption'&&m.data.option==='viewerAutoHideSeconds'&&m.data.value===7));
    check(true,'Delay changes autosave their numeric value');
    await page.evaluate(()=>{window.holdDisplay=true;});
    await page.locator('#viewerAutoHide').uncheck();await page.locator('#viewerAutoHide').check();
    await page.evaluate(settings=>window.dispatchBridge({type:'settings',settings:{...settings,viewerAutoHide:false,viewerAutoHideSeconds:3}}),settings);
    check(await page.locator('#viewerAutoHide').isChecked(),'A stale settings reply cannot undo a newer pending checkbox edit');
    await page.evaluate(()=>{window.holdDisplay=false;window.reply(window.messages.filter(m=>m.action==='displayOption').at(-1));});
    await page.waitForFunction(()=>window.messages.filter(m=>m.action==='displayOption'&&m.data.option==='viewerAutoHide').at(-1).data.value===1);
    await page.locator('#viewerAutoHideSeconds').fill('0');await page.locator('#viewerAutoHideSeconds').press('Tab');
    check(!await page.locator('#viewerAutoHideSeconds').evaluate(e=>e.checkValidity()),'Zero delay is rejected by the settings field');
    check(!await page.evaluate(()=>window.messages.some(m=>m.action==='displayOption'&&m.data.option==='viewerAutoHideSeconds'&&m.data.value===0)),'Invalid delays never reach the native bridge');
    await page.locator('#viewerAutoHideSeconds').fill('9');
    await page.locator('#save-settings').click();
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='settingsSaveComplete'));
    check(await page.evaluate(()=>{const m=window.messages.filter(m=>m.action==='saveAppearance').at(-1);return m.data.viewerAutoHide&&m.data.viewerAutoHideSeconds===9;}),'Save settings includes both auto-hide fields');
    check(await page.evaluate(()=>window.messages.filter(m=>m.action==='settingsSaveComplete').length===1),'Only an explicit Save requests success audio');
    await page.evaluate(()=>{window.failDisplay=true;});await page.locator('#viewerAutoHide').uncheck();
    await page.waitForFunction(()=>document.querySelector('#error').textContent.includes('Synthetic save failure'));
    check(true,'Failed auto-save is visibly reported instead of claiming success');
    await page.evaluate(()=>{window.failDisplay=false;});await page.locator('#save-settings').click();
    await page.waitForFunction(()=>window.messages.filter(m=>m.action==='settingsSaveComplete').length===2);
    check(!await page.evaluate(()=>window.messages.filter(m=>m.action==='saveAppearance').at(-1).data.viewerAutoHide),'Explicit save can recover the failed checkbox preference');
    await page.evaluate(()=>window.dispatchBridge({type:'flushSettings'}));
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='flushed'));
    check(!await page.evaluate(()=>window.messages.some(m=>m.action==='flushFailed')),'A recovered display save does not leave an old failure blocking quit');
    for(const theme of [0,1,2,3]){
      await page.evaluate(settings=>window.dispatchBridge({type:'settings',settings}),{...settings,theme});
      const centered=await page.locator('.viewer-hide-row').evaluate(row=>{const centers=[...row.children].map(el=>{const r=el.getBoundingClientRect();return r.y+r.height/2});return Math.max(...centers)-Math.min(...centers)<2;});
      check(centered,'Checkbox, seconds field and unit stay vertically centered in theme '+theme);
    }
    check(errors.length===0,'Auto-hide settings produce no browser script errors');
    console.log(`${passed} auto-hide browser checks passed.`);
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
