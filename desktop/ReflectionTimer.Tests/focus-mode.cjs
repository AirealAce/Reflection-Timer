const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let passed=0;
  const check=(ok,name)=>{assert.ok(ok,name);passed++;console.log('PASS '+name);};
  try{
    const page=await browser.newPage({viewport:{width:940,height:780}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
    async function capture(name,target){if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});await target.screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,name+'.png')});}}
    await page.route('**/*',async route=>{const name=path.basename(new URL(route.request().url()).pathname);if(!/\.(html|js|css)$/.test(name))return route.abort();await route.fulfill({body:await fs.readFile(path.join(web,name)),contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'});});
    await page.addInitScript(()=>{
      const handlers=[];window.messages=[];
      window.dispatchBridge=m=>handlers.forEach(h=>h({data:m}));
      window.settings={volume:50,tracks:[{id:0,name:'Default'},{id:6,name:'Battle (Trainer)'},{id:9,name:'None'}],sounds:[0,1,2,3,4,5].map(kind=>({kind,track:kind===5?6:0,behavior:kind===5?2:0,volume:100,fadeOutAfterSeconds:10,defaultName:'Battle (Trainer)'})),focusMode:{enabled:false,delaySeconds:5,target:null}};
      window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
        window.messages.push(m);queueMicrotask(()=>{
          if(m.action==='focusTargets')window.dispatchBridge({type:'focusTargets',kind:m.data.kind,targets:[{id:'target-a',name:m.data.kind===1?'Same tab title':'Work window',app:'chrome'},{id:'target-b',name:'Same tab title',app:'chrome'}]});
          if(m.action==='focusSelect'){window.settings.focusMode={...window.settings.focusMode,enabled:m.data.enable||window.settings.focusMode.enabled,target:m.data.id==='target-a'?'Work window':'Same tab title',targetKind:Number(document.querySelector('#focus-target-kind').value)};window.dispatchBridge({type:'settings',settings:window.settings});}
          if(m.action==='focusMode'){window.settings.focusMode={...window.settings.focusMode,...m.data};window.dispatchBridge({type:'settings',settings:window.settings});}
          window.dispatchBridge({type:'reply',requestId:m.requestId});
        });
      }}};
    });
    await page.goto('https://reflection-timer.invalid/index.html?view=main');await page.waitForFunction(()=>window.messages.some(m=>m.action==='ready'));
    await page.evaluate(()=>window.dispatchBridge({type:'settings',settings:window.settings}));
    check(!await page.locator('#focus-enabled').isChecked(),'Focus mode stays off for existing users');
    await page.locator('#choose-focus-target').click();await page.locator('#focus-target-dialog').waitFor({state:'visible'});
    await page.keyboard.press('Escape');await page.locator('#focus-target-dialog').waitFor({state:'hidden'});
    check(await page.locator('#choose-focus-target').evaluate(el=>el===document.activeElement)&&!await page.locator('#focus-enabled').isChecked(),'Escape cancels target selection and restores focus without enabling monitoring');
    await page.locator('#focus-enabled').click();
    await page.getByRole('dialog',{name:'Choose a focus target'}).waitFor({state:'visible'});
    check(await page.locator('#focus-target-kind').evaluate(el=>el===document.activeElement),'Opening the selector focuses its labeled target-type control');
    await page.locator('#focus-target-use').click();await page.locator('#focus-target-dialog').waitFor({state:'hidden'});
    check(await page.locator('#focus-enabled').isChecked()&&await page.locator('#focus-enabled').evaluate(el=>el===document.activeElement),'Choosing a target enables focus mode and returns keyboard focus');
    check(await page.evaluate(()=>window.messages.filter(m=>m.action==='focusSelect').at(-1).data.id==='target-a'),'Only the native listed target ID crosses the selection bridge');
    await page.locator('#session-mode').click();
    await page.evaluate(()=>window.dispatchBridge({type:'state',state:{clock:{seconds:0,text:'0 seconds',status:'Ready',stopwatch:true},timer:{mode:1,durationSeconds:900,autoRestart:false,enabled:false,threshold:15},prompts:[],schedules:[],outbox:[],focusMode:window.settings.focusMode}}));
    check(await page.locator('#focus-timer-options').isVisible()&&await page.locator('#focus-enabled').isChecked()&&await page.locator('#session-mode').textContent().then(text=>text.includes('Stopwatch')),'The selected focus target and toggle remain available in Stopwatch mode');
    await capture('focus-stopwatch',page.locator('body'));
    await page.locator('#tab-settings').click();
    check(await page.locator('#sound-form-5 legend').textContent()==='Focus mode · away from selected window or tab','Focus audio has a matching independent settings section');
    check(await page.locator('#sound-track-5').inputValue()==='6','Default focus selection is Battle (Trainer)');
    check(await page.locator('#sound-message-fade-row-5').count()===0,'Focus audio does not expose irrelevant message-sent fade options');
    await page.locator('#focus-delay').fill('8');await page.locator('#focus-audio-help').click();
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='focusMode'&&m.data.delaySeconds===8));
    check(!await page.evaluate(()=>window.messages.some(m=>m.action==='saveSound')),'Focus delay autosaves without overwriting sound settings');
    await page.locator('#choose-focus-target-settings').click();await page.locator('#focus-target-kind').selectOption('1');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='focusTargets'&&m.data.kind===1));
    await page.locator('#focus-target-list').selectOption('target-b');await page.locator('#focus-target-use').click();
    await page.locator('#focus-target-dialog').waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.messages.filter(m=>m.action==='focusSelect').at(-1).data.id==='target-b'),'Duplicate tab titles remain separately selectable by stable IDs');
    await page.locator('#focus-settings-enabled').uncheck();
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='focusMode'&&!m.data.enabled));
    check(!await page.locator('#focus-enabled').isChecked(),'Audio settings and Timer toggles stay synchronized');
    await page.locator('#focus-delay').fill('12');await page.locator('#focus-delay').press('Control+s');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='settingsSaveComplete'));
    check(await page.evaluate(()=>window.messages.filter(m=>m.action==='focusMode').at(-1).data.delaySeconds===12),'Ctrl+S flushes an unblurred focus delay edit');
    await page.locator('#focus-delay').fill('-1');await page.locator('#focus-audio-help').click();
    check(!await page.locator('#focus-delay').evaluate(el=>el.checkValidity()),'Invalid negative delays are rejected');
    await page.locator('#focus-delay').fill('5');await page.locator('#focus-audio-help').click();
    await page.locator('#sound-track-5').selectOption('9');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='saveSound'&&m.data.kind===5));
    check(await page.evaluate(()=>window.messages.filter(m=>m.action==='saveSound').at(-1).data.track===9),'Focus audio can independently be set to None');
    for(const theme of [0,1,2,3]){
      await page.evaluate(theme=>window.dispatchBridge({type:'settings',settings:{...window.settings,theme}}),theme);
      const centered=await page.locator('#focus-delay').evaluate(input=>{const r=input.getBoundingClientRect(),label=input.previousElementSibling.getBoundingClientRect();return Math.abs(r.y+r.height/2-label.y-label.height/2)<2;});
      check(centered,'Delay row is vertically centered in theme '+theme);
      await capture('focus-audio-theme-'+theme,page.locator('#sound-form-5'));
    }
    await page.locator('#choose-focus-target-settings').click();await page.locator('#focus-target-dialog').waitFor({state:'visible'});
    await capture('focus-target-selector',page.locator('#focus-target-dialog'));
    await page.locator('#focus-target-cancel').click();
    check(errors.length===0,'Focus controls have no browser script errors');
    console.log(`${passed} focus browser checks passed.`);
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
