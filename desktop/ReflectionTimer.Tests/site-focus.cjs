// Standalone synthetic Site picker checks. No browser history or profile is read.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
  const browser=await chromium.launch({channel:process.env.REFLECTION_TEST_BROWSER||'chrome',headless:true});let count=0;
  const check=(ok,name)=>{assert.ok(ok,name);count++;console.log('PASS '+name);};
  try{
    const screenshotDirectory=process.env.REFLECTION_SITE_SCREENSHOTS;
    if(screenshotDirectory)await fs.mkdir(screenshotDirectory,{recursive:true});
    const page=await browser.newPage({viewport:{width:739,height:642}}),errors=[];
    const screenshot=async(name,target)=>{
      if(!screenshotDirectory)return;
      await page.locator('.focus-picker-body').evaluate(body=>body.scrollTop=0);
      if(target)await target.scrollIntoViewIfNeeded();
      await page.screenshot({path:path.join(screenshotDirectory,name+'.png')});
    };
    page.on('pageerror',error=>errors.push(error.message));
    const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
    await page.route('**/*',async route=>{const file=path.basename(new URL(route.request().url()).pathname);if(!/\.(js|css|html)$/.test(file))return route.abort();await route.fulfill({body:await fs.readFile(path.join(web,file)),contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});});
    await page.addInitScript(()=>{
      const handlers=[];window.messages=[];window.choices=new Map();window.successes=0;window.browserConnected=false;
      window.dispatchBridge=message=>handlers.forEach(handler=>handler({data:message}));
      window.settings={volume:50,tracks:[{id:0,name:'Default'},{id:9,name:'None'}],sounds:[0,1,2,3,4,5].map(kind=>({kind,track:0,behavior:0,volume:100,fadeOutAfterSeconds:10})),focusMode:{enabled:false,delaySeconds:5,targets:[],multipleTargets:false,idleEnabled:false,idleSeconds:20}};
      window.chrome={webview:{addEventListener:(_,handler)=>handlers.push(handler),postMessage:message=>{
        window.messages.push(message);queueMicrotask(()=>{
          let extra={};const {action,data,requestId}=message;
          if(action==='focusTargets'){
            if(data.reset){window.choices.clear();for(const target of window.settings.focusMode.targets)window.choices.set(target.id,target);}
            const targets=data.kind===3?[{id:'site-current',key:'focused-site',kind:3,useFocused:true,name:'Use focused site',app:'',captureScope:0},
              {id:'site-open',key:'site:open.example',kind:3,name:'open.example',siteHost:'open.example',app:'chrome',current:true}]
              :[{id:'window-open',key:'window:open',kind:0,name:'Open window',app:'Synthetic app'}];
            for(const target of targets)window.choices.set(target.id,target);
            window.dispatchBridge({type:'focusTargets',kind:data.kind,targets,browserConnected:window.browserConnected,nativeSites:true});
          }
          if(action==='focusSite'){
            if(data.website.includes('bad')){window.dispatchBridge({type:'reply',requestId,error:'Enter a valid website address.'});return;}
            const host=new URL(data.website.includes('://')?data.website:'https://'+data.website).hostname.toLowerCase();
            const target={id:'site-'+host,key:'site:'+host,kind:3,name:host,siteHost:host,app:''};window.choices.set(target.id,target);extra={target};
          }
          if(action==='focusSelect'){
            window.settings.focusMode={...window.settings.focusMode,targets:data.ids.map(id=>window.choices.get(id)),multipleTargets:data.multipleTargets,targetOnSiteLinks:data.targetOnSiteLinks,browserCompanionEnabled:data.browserCompanionEnabled,idleEnabled:data.idleEnabled,idleSeconds:data.idleSeconds};
            window.successes++;window.dispatchBridge({type:'settings',settings:window.settings});
          }
          window.dispatchBridge({type:'reply',requestId,...extra});
        });
      }}};
    });
    await page.goto('https://reflection-timer.invalid/index.html?view=main');await page.waitForFunction(()=>window.messages.some(m=>m.action==='ready'));
    await page.evaluate(()=>window.dispatchBridge({type:'settings',settings:window.settings}));
    const dialog=page.locator('#focus-target-dialog'),website=page.locator('#focus-site-website'),links=page.locator('#focus-target-on-site-links');
    const open=async()=>{await page.locator('#choose-focus-target').click();await page.waitForFunction(()=>document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');};
    const site=async()=>{await page.locator('#focus-target-kind').selectOption('3');await page.waitForFunction(()=>document.querySelector('#focus-target-list th')?.textContent==='Site');};
    await open();check(await page.locator('#focus-site-controls').isHidden(),'Manual website controls stay out of existing target categories');
    check(await links.isChecked(),'Target On-Site Links is checked for legacy/default settings');
    await site();
    check(await page.locator('#focus-target-list tbody tr').first().textContent().then(text=>text.includes('Use focused site')),'Dynamic current-site choice is the first Site row');
    check(await page.locator('#focus-target-list thead th').allTextContents().then(values=>JSON.stringify(values)===JSON.stringify(['Site','App'])),'Site has meaningful table headers without a fabricated tab number');
    check(await page.locator('#focus-picker-status').textContent().then(text=>text.includes('current page in each browser window')&&!text.includes('previously seen')&&!text.includes('waiting')),'Site guidance explains the native current-page list without requiring a companion connection');
    await screenshot('site-chooser');
    await screenshot('site-chooser-list',page.locator('#focus-target-list'));
    await website.fill('saved.example');await website.press('Control+Enter');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets[0]?.siteHost==='saved.example'&&window.successes===1),'Ctrl+Enter stages a typed website and saves it instead of ignoring it');
    check(await page.locator('#choose-focus-target .focus-target-type').textContent()==='Site'&&await page.locator('#choose-focus-target .focus-target-label').textContent()==='saved.example','Selected Site uses the existing two-line button styling');
    await open();
    check(await page.locator('#focus-target-list tbody tr.unavailable').count()===0&&await page.locator('#focus-target-list tbody').textContent().then(text=>text.includes('saved.example')),'A saved website absent from open tabs remains usable without a gray unavailable row');
    await links.uncheck();await page.keyboard.press('Escape');
    await open();check(await links.isChecked(),'Cancel discards an unsaved On-Site Links checkbox change');
    await links.uncheck();await website.fill('second.example');await website.press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets[0]?.siteHost==='second.example'&&window.settings.focusMode.targetOnSiteLinks===false&&window.successes===2),'Ctrl+S saves the typed website and link-tracking preference once');
    await open();await website.fill('bad website');await page.locator('#focus-site-add').click();
    await page.waitForFunction(()=>document.querySelector('#focus-site-website').getAttribute('aria-invalid')==='true');
    check(await website.evaluate(input=>document.activeElement===input)&&await page.locator('#focus-picker-status').textContent().then(text=>text.includes('valid website')),'Invalid website feedback restores focus to its labelled input');
    check(await page.evaluate(()=>window.settings.focusMode.targets[0].siteHost==='second.example'&&window.successes===2),'Failed manual validation preserves the prior selection and does not play save success');
    await website.fill('manual.example');await website.press('Enter');
    await page.waitForFunction(()=>document.querySelector('#focus-target-list tbody').textContent.includes('manual.example'));
    check(await dialog.isVisible()&&await website.inputValue()==='','Plain Enter adds a website to the picker draft without prematurely saving settings');
    await page.locator('#focus-multiple-targets').check();
    await page.locator('#focus-target-kind').selectOption('0');await page.waitForFunction(()=>document.querySelector('#focus-target-list th')?.nextElementSibling?.textContent==='App');
    await page.locator('#focus-target-list tbody input[type=checkbox]').check();await page.locator('#focus-target-use').click();await dialog.waitFor({state:'hidden'});
    check(await page.locator('#choose-focus-target').getAttribute('aria-label')==='Window, Site','Multi-target button includes Site alongside existing categories');
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===2&&window.successes===3),'Multi-target save keeps the manual website and window with one success result');
    check(await page.evaluate(()=>window.settings.focusMode.browserCompanionEnabled===false&&!window.messages.some(message=>message.action==='focusBrowserSetup')),'Saving Site targets leaves the optional companion off without automatically configuring it');
    await open();
    check(!await page.locator('#focus-picker-companion').getAttribute('open')&&await page.locator('#focus-browser-companion').isHidden(),'Optional companion controls start collapsed');
    await page.locator('#focus-picker-companion summary').focus();await page.keyboard.press('Enter');
    const companion=page.locator('#focus-browser-companion');
    check(!await companion.isChecked()&&await page.locator('#focus-browser-status').textContent().then(text=>text.includes('Native Site tracking is active')&&text.includes('off')),'Companion defaults unchecked while native Site tracking remains active');
    await screenshot('site-companion');
    check(await page.locator('#focus-site-links-help').textContent()==='Native: same-website pages. Companion: also follows links to other websites.','Link policy explains native same-website behavior and optional external-link following');
    await companion.check();await page.keyboard.press('Escape');await open();
    await page.locator('#focus-picker-companion summary').click();
    check(!await companion.isChecked(),'Cancel discards a pending companion checkbox change');
    await companion.check();await companion.press('Control+Enter');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.browserCompanionEnabled===true&&!window.messages.some(message=>message.action==='focusBrowserSetup')),'Shortcut save commits companion preference without registering or configuring it');
    await open();await page.locator('#focus-picker-companion summary').click();
    check(await companion.isChecked()&&await page.locator('#focus-browser-status').textContent().then(text=>text.includes('Waiting for the companion')&&text.includes('Native Site tracking remains active')),'An enabled disconnected companion retains native Site tracking');
    await companion.focus();
    const targetRequests=await page.evaluate(()=>window.messages.filter(message=>message.action==='focusTargets').length);
    await page.evaluate(()=>{window.browserConnected=true;window.dispatchBridge({type:'browserCompanionStatus',connected:true});});
    await page.waitForFunction(()=>document.querySelector('#focus-browser-status').textContent.includes('Companion connected'));
    check(await page.locator('#focus-browser-status').textContent().then(text=>text.includes('Background sites')&&text.includes('links to other sites')),'Connected companion feedback describes its additional browser capabilities');
    check(await companion.evaluate(input=>document.activeElement===input&&input.checked)
      &&await page.evaluate(()=>window.messages.filter(message=>message.action==='focusTargets').length)===targetRequests
      &&await page.locator('#focus-browser-status').getAttribute('role')==='status','Companion connection updates live without refreshing the list, moving focus or changing the picker draft');
    await companion.uncheck();await companion.press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.browserCompanionEnabled===false),'Ctrl+S can disable the optional companion');
    await open();await page.locator('#focus-picker-companion summary').click();
    await page.getByRole('button',{name:'Configure companion…',exact:true}).focus();await page.keyboard.press('Space');
    await page.waitForFunction(()=>window.messages.some(message=>message.action==='focusBrowserSetup'));
    check(await page.evaluate(()=>window.messages.filter(message=>message.action==='focusBrowserSetup').length===1&&window.settings.focusMode.browserCompanionEnabled===false),'Configure companion is keyboard accessible and only runs on its explicit button activation');
    check(errors.length===0,'Site picker produces no uncaught page errors: '+errors.join('; '));
    console.log(count+' Site picker checks passed.');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
