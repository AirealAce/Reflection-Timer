// Synthetic bridge only: no installed profile, real files or Google requests.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let passed=0;
  const check=(ok,label)=>{assert.ok(ok,label);passed++;console.log('PASS '+label);};
  try{
    const page=await browser.newPage({viewport:{width:739,height:642}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',async route=>{const name=path.basename(new URL(route.request().url()).pathname);if(!/\.(html|js|css)$/.test(name))return route.abort();await route.fulfill({body:await fs.readFile(path.join(web,name)),contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'});});
    await page.addInitScript(()=>{
      const handlers=[];window.messages=[];
      window.settings={sheetUrl:'',webAppUrl:'',sheetMode:'date',sheetName:'Template',connected:false,sheetsEnabled:false,csvEnabled:true,csvDirectory:'C:\\Synthetic Desktop\\Reflection Timer',volume:0,threshold:15,showFloatingTimer:true,
        theme:0,popup:4,placement:4,overlap:2,tracks:[{id:0,name:'Default'},{id:6,name:'Battle (Trainer)'}],sounds:[0,1,2,3,4,5].map(kind=>({kind,track:0,behavior:2,volume:100,fadeOutAfterSeconds:10,defaultName:'Bundled track'}))};
      window.state={clock:{seconds:900,text:'15 minutes',status:'Ready'},timer:{durationSeconds:900,mode:0,autoRestart:false,enabled:true,threshold:15},prompts:[],schedules:[],outbox:[]};
      window.dispatchBridge=m=>handlers.forEach(h=>h({data:m}));
      window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
        window.messages.push(m);queueMicrotask(()=>{
          if(m.action==='csvSave'){
            if(window.csvFailure){window.dispatchBridge({type:'reply',requestId:m.requestId,error:'Choose a full path for the CSV folder.'});return;}
            Object.assign(window.settings,{csvEnabled:m.data.enabled,csvDirectory:m.data.directory});
            window.dispatchBridge({type:'settings',settings:window.settings});
          }
          if(m.action==='connectionStore'||m.action==='connectionSave'){
            Object.assign(window.settings,{sheetUrl:m.data.sheetUrl,webAppUrl:m.data.webAppUrl,sheetMode:m.data.sheetMode,sheetName:m.data.sheetName,sheetsEnabled:m.data.enabled,connected:m.data.enabled});
            window.dispatchBridge({type:'settings',settings:window.settings});
          }
          if(m.action==='csvBrowse')window.dispatchBridge({type:'csvFolder',directory:'C:\\Synthetic logs'});
          window.dispatchBridge({type:'reply',requestId:m.requestId});
        });
      }}};
    });
    await page.goto('https://reflection-timer.invalid/index.html?view=main');await page.waitForFunction(()=>window.messages.some(m=>m.action==='ready'));
    await page.evaluate(()=>{window.dispatchBridge({type:'settings',settings:window.settings});window.dispatchBridge({type:'state',state:window.state});});
    await page.locator('#tab-settings').click();
    check(await page.locator('#csv-enabled').isChecked()&&!await page.locator('#connection-enabled').isChecked(),'Fresh Settings selects CSV and leaves Sheets off');
    check(await page.locator('#csv-directory').inputValue()==='C:\\Synthetic Desktop\\Reflection Timer','Settings exposes the resolved Desktop folder');
    check(await page.locator('#extension-off,#duplicate-timers,#import-schedules').count()===0,'Abandoned extension controls are absent');
    check(await page.locator('#connection-enabled').evaluate(el=>el.form.id==='connection-form')&&await page.locator('#csv-enabled').evaluate(el=>el.form.id==='csv-form'),'Independent destination controls retain correct form owners');
    await page.locator('#csv-enabled').uncheck();await page.locator('#connection-enabled').check();await page.locator('#connection-enabled').press('Control+Enter');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='settingsSaveComplete'));
    check(await page.evaluate(()=>!window.settings.csvEnabled&&window.settings.sheetsEnabled),'Ctrl+Enter from a destination checkbox saves Sheets-only preferences');
    await page.locator('#csv-enabled').check();await page.locator('#csv-directory').fill('C:\\Custom reflection logs');await page.locator('#csv-directory').press('Control+s');
    await page.waitForFunction(()=>window.settings.csvDirectory==='C:\\Custom reflection logs'&&window.settings.csvEnabled);
    check(await page.locator('#connection-enabled').isChecked(),'Ctrl+S from the folder input enables CSV alongside Sheets');
    await page.locator('#csv-browse').click();await page.waitForFunction(()=>document.querySelector('#csv-directory').value==='C:\\Synthetic logs');
    check(await page.locator('#csv-directory').evaluate(el=>el===document.activeElement)&&await page.evaluate(()=>window.settings.csvDirectory==='C:\\Custom reflection logs'),'Folder selection restores accessible focus and waits for Save');
    await page.locator('#csv-directory').press('Control+Enter');await page.waitForFunction(()=>window.settings.csvDirectory==='C:\\Synthetic logs');
    check(await page.locator('#csv-directory').inputValue()==='C:\\Synthetic logs','Ctrl+Enter persists the chosen destination');
    await page.locator('#settings-search-input').fill('CSV');
    check(await page.locator('#csv-directory').isVisible()&&!await page.locator('#sheet-url').isVisible(),'Settings search finds CSV controls without exposing unrelated connection fields');
    await page.locator('#settings-search-clear').click();
    await page.evaluate(()=>window.csvFailure=true);await page.locator('#csv-directory').fill('invalid');await page.locator('#csv-directory').press('Control+s');
    await page.waitForFunction(()=>document.querySelector('#error').textContent.includes('full path'));
    check(await page.locator('#csv-directory').evaluate(el=>el===document.activeElement)&&await page.locator('#csv-directory').inputValue()==='invalid','A failed save retains the path and focuses the field needing correction');
    await page.evaluate(()=>window.csvFailure=false);await page.locator('#csv-directory').fill('C:\\Synthetic logs');await page.locator('#csv-directory').press('Control+s');
    await page.locator('#guided-setup').click();
    check(await page.locator('#guided-dialog').isVisible()&&await page.locator('#guided-page-2 #connection-enabled').count()===1,'Guided Sheets setup keeps its delivery choice and no extension confirmation');
    await page.locator('#guided-close').click();
    await page.locator('#destinations #connection-enabled').waitFor({state:'attached'});
    check(await page.locator('#destinations #connection-enabled').count()===1,'Closing Guided setup restores the Sheets destination checkbox');
    for(const theme of [0,1,2,3]){
      await page.evaluate(theme=>{window.settings.theme=theme;window.dispatchBridge({type:'settings',settings:window.settings});},theme);
      check(await page.locator('#csv-directory').evaluate(el=>!!el.labels[0]?.textContent.includes('CSV folder'))&&await page.getByRole('checkbox',{name:'CSV files',exact:true}).count()===1,'CSV controls retain accessible labels in theme '+theme);
      check(await page.locator('#destinations').evaluate(el=>el.scrollWidth<=el.clientWidth+1),'CSV folder controls fit the Settings section in theme '+theme);
      if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});await page.locator('#destinations').screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,'csv-theme-'+theme+'.png')});}
    }
    for(const width of [420,336]){
      await page.setViewportSize({width,height:642});
      check(await page.locator('#destinations').evaluate(el=>el.scrollWidth<=el.clientWidth+1),'CSV controls fit at '+width+' pixels without horizontal scrolling');
    }
    await page.setViewportSize({width:739,height:642});
    await page.evaluate(()=>{window.state.outbox=[{id:'csv-entry',saved:'10/7/2026 8:52 AM',localOnly:false,wantsSheets:false,status:'Pending',csvStatus:'Saved',csvFile:'C:\\Synthetic logs\\10-07-2026.csv',csvAttempts:1,complete:true,deliveryLabel:'CSV saved',destination:'CSV: 10-07-2026.csv',message:'Saved response',attempts:0}];window.dispatchBridge({type:'state',state:window.state});});
    await page.locator('#tab-outbox').click();
    check(await page.locator('#outbox-rows').textContent().then(text=>text.includes('CSV saved'))&&await page.locator('#retry-selected').getAttribute('aria-disabled')==='true','CSV success appears in Outbox and is not offered as unsent Sheets delivery');
    await page.evaluate(()=>{Object.assign(window.state.outbox[0],{csvStatus:'NeedsReview',csvError:'csv_access',csvFile:'',complete:false,deliveryLabel:'CSV needs review'});window.dispatchBridge({type:'state',state:window.state});});
    await page.locator('#retry-selected').click();
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='retry'));
    check(!await page.locator('#delivery-dialog').isVisible(),'CSV-only retry does not ask for an unrelated Google Sheet confirmation');
    const retries=await page.evaluate(()=>window.messages.filter(m=>m.action==='retry').length);
    await page.evaluate(()=>{Object.assign(window.state.outbox[0],{wantsSheets:true,status:'Sent'});window.dispatchBridge({type:'state',state:window.state});});
    await page.locator('#retry-selected').click();await page.waitForFunction(count=>window.messages.filter(m=>m.action==='retry').length>count,retries);
    check(!await page.locator('#delivery-dialog').isVisible(),'Retrying failed CSV after Sheets succeeded does not ask to resend Sheets');
    check(errors.length===0,'Destination settings, folder selection, Guided setup and Outbox have no script errors: '+errors.join('; '));
    console.log(passed+' CSV browser checks passed.');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
