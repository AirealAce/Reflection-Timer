// Targeted persistence/bridge tests only. No accessibility-tree or keyboard tests.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let passed=0;
  const check=(value,message)=>{assert.ok(value,message);passed++;console.log('PASS '+message);};
  try{
    const page=await browser.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',async route=>{
      const file=path.basename(new URL(route.request().url()).pathname);
      if(!/\.(html|css|js)$/.test(file))return route.abort();
      await route.fulfill({body:await fs.readFile(path.join(root,file)),contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});
    });
    await page.addInitScript(()=>{
      const handlers=[];window.voiceMessages=[];window.holdVoice=false;window.failVoice=false;
      window.dispatchBridge=message=>handlers.forEach(f=>f({data:message}));
      window.replyVoice=message=>window.dispatchBridge({type:'reply',requestId:message.requestId,error:window.failVoice?'Synthetic save failure':undefined});
      window.chrome={webview:{addEventListener:(_,f)=>handlers.push(f),postMessage:message=>{
        window.voiceMessages.push(message);
        if(message.action==='voiceAnnouncements'&&window.holdVoice)return;
        queueMicrotask(()=>window.replyVoice(message));
      }}};
    });
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.voiceMessages.some(m=>m.action==='ready'));
    const settings={sheetUrl:'',webAppUrl:'',sheetMode:'date',sheetName:'Reflections',hasToken:false,connected:false,volume:50,threshold:15,
      showFloatingTimer:true,theme:0,popup:4,placement:4,overlap:0,loggingEnabled:false,startAtLogin:false,
      tracks:[{id:0,name:'Default'},{id:9,name:'None'}],sounds:[0,1,2,3,4].map(kind=>({kind,track:0,behavior:0,volume:100,fadeOutEnabled:false,fadeOutAfterSeconds:10,custom:false,defaultName:'Default'}))};
    check(await page.locator('#voice-announcements').isDisabled(),'Voice option is unavailable until saved settings load');
    check(!await page.locator('#voice-announcements').isChecked(),'The unloaded page never flashes the old voice-on default');
    await page.evaluate(settings=>window.dispatchBridge({type:'settings',settings}),settings);
    await page.locator('#tab-settings').click();
    check(!await page.locator('#voice-announcements').isChecked(),'Missing preference loads as off');
    await page.evaluate(settings=>window.dispatchBridge({type:'settings',settings}),{...settings,voiceAnnouncements:false});
    check(!await page.locator('#voice-announcements').isChecked(),'An explicit saved opt-out overrides the default');
    await page.locator('#voice-announcements').check();
    await page.waitForFunction(()=>window.voiceMessages.some(m=>m.action==='voiceAnnouncements'&&m.data.enabled));
    check(await page.evaluate(()=>window.voiceMessages.filter(m=>m.action==='voiceAnnouncements').at(-1).data.quiet),'Checking voice autosaves silently');
    await page.evaluate(()=>{window.holdVoice=true;});
    await page.locator('#voice-announcements').uncheck();
    await page.waitForFunction(()=>window.voiceMessages.filter(m=>m.action==='voiceAnnouncements').length===2);
    await page.locator('#voice-announcements').check();
    await page.evaluate(settings=>window.dispatchBridge({type:'settings',settings}),{...settings,voiceAnnouncements:false});
    check(await page.locator('#voice-announcements').isChecked(),'A stale settings reply cannot overwrite a newer checkbox edit');
    await page.evaluate(()=>{window.holdVoice=false;window.replyVoice(window.voiceMessages.filter(m=>m.action==='voiceAnnouncements').at(-1));});
    await page.waitForFunction(()=>window.voiceMessages.filter(m=>m.action==='voiceAnnouncements').length===3);
    check(await page.evaluate(()=>window.voiceMessages.filter(m=>m.action==='voiceAnnouncements').at(-1).data.enabled),'Rapid edits persist the latest selected value');
    await page.evaluate(()=>{window.failVoice=true;});
    await page.locator('#voice-announcements').uncheck();
    await page.waitForFunction(()=>document.querySelector('#error').textContent.includes('Synthetic save failure'));
    check(true,'Failed autosave is visibly reported');
    await page.evaluate(()=>{window.failVoice=false;window.dispatchBridge({type:'flushSettings'});});
    await page.waitForFunction(()=>window.voiceMessages.some(m=>m.action==='flushed'));
    check(await page.evaluate(()=>window.voiceMessages.filter(m=>m.action==='voiceAnnouncements').at(-1).data.enabled===false),'Quit/save flush retries the unsaved preference');
    await page.locator('#preview-voice').click();
    await page.waitForFunction(()=>window.voiceMessages.some(m=>m.action==='previewVoice'));
    check(!await page.locator('#voice-announcements').isChecked(),'Preview works without enabling future announcements');
    await page.evaluate(settings=>window.dispatchBridge({type:'settings',settings}),{...settings,voiceAnnouncements:true});
    check(await page.locator('#voice-announcements').isChecked(),'A later clean load restores the saved enabled preference');
    const commands=await page.evaluate(()=>window.voiceMessages.filter(m=>m.action==='voiceAnnouncements').length);
    await page.evaluate(()=>window.dispatchBridge({type:'sessionStatus',message:'Timer paused with 2 minutes remaining.'}));
    check(await page.locator('#session-status').textContent()==='Timer paused with 2 minutes remaining.','Native session feedback also leaves readable status text');
    check(await page.locator('#session-status').getAttribute('aria-live')==='off'&&await page.locator('#status').textContent()==='',
      'Session status does not duplicate the native notification in a browser live region');
    check(await page.evaluate(()=>window.voiceMessages.filter(m=>m.action==='voiceAnnouncements').length)===commands,'Displaying session feedback cannot overwrite the saved voice preference');
    check(errors.length===0,'Voice settings produced no browser script errors');
    await page.goto('https://reflection-timer.invalid/compact.html');
    await page.waitForFunction(()=>window.voiceMessages.some(m=>m.action==='ready'));
    await page.evaluate(()=>window.dispatchBridge({type:'sessionStatus',message:'Stopwatch paused at 3 seconds elapsed.'}));
    check(await page.locator('#session-status').textContent()==='Stopwatch paused at 3 seconds elapsed.','Compact retains the same readable session status');
    check(await page.locator('#session-status').getAttribute('aria-live')==='off'&&await page.locator('#status').textContent()==='',
      'Compact does not generate a second live announcement for native feedback');
    check(errors.length===0,'Compact session feedback produces no script errors');
    console.log(`${passed} voice settings checks passed.`);
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
