// Standalone synthetic Site picker checks. No browser history or profile is read.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
  const browser=await chromium.launch({channel:process.env.REFLECTION_TEST_BROWSER||'chrome',headless:true});let count=0;
  const check=(ok,name)=>{assert.ok(ok,name);count++;console.log('PASS '+name);};
  try{
    const page=await browser.newPage({viewport:{width:739,height:642}}),errors=[];
    page.on('pageerror',error=>errors.push(error.message));
    const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
    await page.route('**/*',async route=>{const file=path.basename(new URL(route.request().url()).pathname);if(!/\.(js|css|html)$/.test(file))return route.abort();await route.fulfill({body:await fs.readFile(path.join(web,file)),contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});});
    await page.addInitScript(()=>{
      const handlers=[];window.messages=[];window.choices=new Map();window.successes=0;window.browserConnected=false;
      window.dispatchBridge=message=>handlers.forEach(handler=>handler({data:message}));
      window.settings={volume:50,tracks:[{id:0,name:'Default'},{id:9,name:'None'}],sounds:[0,1,2,3,4,5].map(kind=>({kind,track:0,behavior:0,volume:100,fadeOutAfterSeconds:10})),focusMode:{enabled:false,delaySeconds:5,targets:[],multipleTargets:false,idleEnabled:false,idleSeconds:20,browserCompanionEnabled:false}};
      window.chrome={webview:{addEventListener:(_,handler)=>handlers.push(handler),postMessage:message=>{
        window.messages.push(message);queueMicrotask(()=>{
          let extra={};const {action,data,requestId}=message;
          if(action==='focusTargets'){
            if(data.reset){window.choices.clear();for(const target of window.settings.focusMode.targets)window.choices.set(target.id,target);}
            const ready=window.settings.focusMode.browserCompanionEnabled&&window.browserConnected;
            const targets=data.kind===3?ready?[{id:'site-current',key:'focused-site',kind:3,useFocused:true,name:'Use focused site',app:'',captureScope:0},
              {id:'site-open',key:'site:open.example',kind:3,name:'open.example',siteHost:'open.example',app:'chrome',current:true},
              {id:'site-background',key:'site:background.example',kind:3,name:'background.example',siteHost:'background.example',app:'chrome'},
              {id:'site-other-browser',key:'site:other.example',kind:3,name:'other.example',siteHost:'other.example',app:'msedge'}]
              :window.settings.focusMode.targets.filter(target=>target.kind===3)
              :[{id:'window-open',key:'window:open',kind:0,name:'Open window',app:'Synthetic app'}];
            for(const target of targets)window.choices.set(target.id,target);
            window.dispatchBridge({type:'focusTargets',kind:data.kind,targets,browserConnected:ready});
          }
          if(action==='focusSite'){
            if(window.holdSite){window.releaseSite=error=>window.dispatchBridge({type:'reply',requestId,error});return;}
            if(data.website.includes('bad')){window.dispatchBridge({type:'reply',requestId,error:'Enter a valid website address.'});return;}
            const host=new URL(data.website.includes('://')?data.website:'https://'+data.website).hostname.toLowerCase();
            const target={id:'site-'+host,key:'site:'+host,kind:3,name:host,siteHost:host,app:''};window.choices.set(target.id,target);extra={target};
          }
          if(action==='focusSelect'){
            if(window.holdSelection){window.releaseSelection=error=>window.dispatchBridge({type:'reply',requestId,error});return;}
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
    const option=page.locator('#focus-target-kind option[value="3"]'),companion=page.locator('#focus-browser-companion');
    const open=async()=>{await page.locator('#choose-focus-target').click();await page.waitForFunction(()=>document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');};
    const site=async()=>{await page.locator('#focus-target-kind').selectOption('3');await page.waitForFunction(()=>[...document.querySelectorAll('#focus-target-list th')].some(header=>header.textContent==='Site'));};
    const expand=async()=>{if(!await page.locator('#focus-picker-companion').evaluate(details=>details.open))await page.locator('#focus-picker-companion summary').click();};
    const connection=connected=>page.evaluate(connected=>{window.browserConnected=connected;window.dispatchBridge({type:'browserCompanionStatus',connected});},connected);
    const requests=()=>page.evaluate(()=>window.messages.filter(message=>message.action==='focusTargets').length);
    await open();
    check(await option.isDisabled()&&await page.locator('#focus-site-controls').isHidden(),'Site starts disabled while native Window/Tab categories remain usable');
    check(await page.locator('#focus-target-list tbody tr').count()===1&&await links.isChecked(),'Existing target listing and default On-Site Links remain available without the companion');
    check(await page.locator('#focus-site-status').textContent().then(text=>text.includes('enabled, connected')&&text.includes('Configure'))
      &&await page.locator('#focus-target-kind').getAttribute('aria-describedby')==='focus-site-status','The target-type selector has concise accessible companion setup guidance');
    check(!await page.locator('#focus-picker-companion').evaluate(details=>details.open),'Optional companion controls start collapsed');
    await expand();await companion.check();
    check(await option.isDisabled()&&await page.locator('#focus-browser-status').textContent().then(text=>text.includes('Save to enable')),'An unsaved enable checkbox cannot pretend Site is ready');
    await connection(true);
    check(await option.isDisabled(),'A connection notification alone cannot bypass the saved companion preference');
    await companion.press('Control+Enter');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.browserCompanionEnabled===true&&!window.messages.some(message=>message.action==='focusBrowserSetup')),'Shortcut save enables the preference without silently configuring or installing a companion');
    await open();await connection(true);
    check(!await option.isDisabled(),'Site is enabled only after the preference is saved and a ready companion is reported');
    await site();
    check(await page.locator('#focus-target-list tbody tr').first().textContent().then(text=>text.includes('Use focused site')),'Dynamic current-site choice remains first when Site is ready');
    check(await page.locator('#focus-target-list thead th').allTextContents().then(values=>JSON.stringify(values)===JSON.stringify(['Site','App'])),'Site has meaningful table headers');
    check(await page.locator('#focus-target-list caption').textContent()==='Open sites'&&await page.locator('#focus-target-list tbody tr').count()===4,'A connected Site table lists every provided open website across tabs and browsers');
    await website.fill('saved.example');await website.press('Control+Enter');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets[0]?.siteHost==='saved.example'),'Ctrl+Enter stages a typed website and saves it');
    check(await page.locator('#choose-focus-target .focus-target-type').textContent()==='Site'&&await page.locator('#choose-focus-target .focus-target-label').textContent()==='saved.example','Selected Site retains the existing two-line button styling');
    await open();
    check(await page.locator('#focus-target-list tbody tr.unavailable').count()===0&&await page.locator('#focus-target-list tbody').textContent().then(text=>text.includes('saved.example')),'Saved websites absent from open tabs remain valid targets');
    await links.uncheck();await page.keyboard.press('Escape');await open();
    check(await links.isChecked(),'Cancel discards pending link-tracking preferences');
    await website.fill('bad website');await page.locator('#focus-site-add').click();
    await page.waitForFunction(()=>document.querySelector('#focus-site-website').getAttribute('aria-invalid')==='true');
    check(await website.evaluate(input=>document.activeElement===input)&&await page.locator('#focus-picker-status').textContent().then(text=>text.includes('valid website')),'Invalid manual website feedback restores labelled input focus');
    await website.fill('manual.example');await website.press('Enter');
    await page.waitForFunction(()=>document.querySelector('#focus-target-list tbody').textContent.includes('manual.example'));
    check(await dialog.isVisible()&&await website.inputValue()==='','Plain Enter adds a website to the draft without saving settings');
    await page.locator('#focus-multiple-targets').check();await page.locator('#focus-target-kind').selectOption('0');
    await page.locator('#focus-target-list tbody input[type=checkbox]').check();await page.locator('#focus-target-use').click();await dialog.waitFor({state:'hidden'});
    check(await page.locator('#choose-focus-target').getAttribute('aria-label')==='Window, Site'&&await page.evaluate(()=>window.settings.focusMode.targets.length===2),'Multi-target save keeps both Site and Window');
    await open();await site();await expand();await website.fill('draft.example');await companion.uncheck();
    check(await option.isDisabled()&&await website.inputValue()==='draft.example'&&await website.evaluate(input=>input.readOnly)&&await page.locator('#focus-site-add').isDisabled(),'Unchecking the companion immediately blocks Site additions while preserving typed website text');
    const oldManual=page.locator('tr[data-id="site-manual.example"] input'),background=page.locator('tr[data-id="site-background"] input');
    check(await oldManual.isChecked()&&await oldManual.getAttribute('aria-disabled')==='false'&&await background.getAttribute('aria-disabled')==='true','Saved Site checkboxes stay removable while unselected Site choices become unavailable');
    await background.click({force:true});
    check(!await background.isChecked(),'Clicking an unavailable Site cannot add it to the draft');
    await oldManual.click();
    check(!await oldManual.isChecked(),'A saved Site can be removed when the companion draft is disabled');
    await oldManual.click({force:true});
    check(!await oldManual.isChecked(),'An offline Site cannot be re-added after removal');
    await page.keyboard.press('Escape');await open();await site();await expand();
    check(await companion.isChecked()&&await page.locator('tr[data-id="site-manual.example"] input').isChecked(),'Cancel restores the saved companion preference and Site choices');
    await companion.uncheck();await companion.press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.browserCompanionEnabled===false&&window.settings.focusMode.targets.some(target=>target.kind===3)),'Disabling the companion preserves saved Site targets');
    await open();await page.locator('#focus-target-kind').selectOption('0');
    check(await page.locator('#focus-site-saved').isVisible(),'Saved sites remain manageable from other target categories while Site is disabled');
    await page.locator('#focus-site-saved').click();
    await page.waitForFunction(()=>document.querySelector('#focus-target-kind').value==='3'&&document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');
    check(await option.isDisabled()&&await page.locator('#focus-target-list caption').textContent()==='Saved sites'
      &&await page.locator('#focus-target-list tbody tr').count()===1,'Managing saved sites shows retained choices without introducing native or dynamic Site choices');
    await expand();await companion.check();await connection(false);await companion.press('Control+s');await dialog.waitFor({state:'hidden'});
    await open();
    check(await option.isDisabled()&&await page.locator('#focus-browser-status').textContent().then(text=>text.includes('Waiting')),'An enabled but disconnected companion cannot select Site');
    const saved=page.locator('tr[data-id="site-manual.example"] input');await saved.focus();
    const before=await requests(),selectedBefore=await page.locator('#focus-picker-status').textContent();
    await connection(true);await page.waitForFunction(()=>document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false'&&document.querySelector('tr[data-id="site-background"]'));
    check(await requests()===before+1&&await saved.isChecked()&&await saved.evaluate(input=>document.activeElement===input),'A reconnect refreshes Site inventory once, preserving checkbox selection and focus');
    check(await page.locator('#focus-picker-status').textContent().then(text=>text.includes('2 selected'))&&selectedBefore.includes('2 selected'),'Reconnect does not auto-select a new Site or replace other-category choices');
    await connection(true);check(await requests()===before+1,'Repeated ready notifications do not poll or repeatedly refresh inventory');
    await website.fill('unsaved.example');await page.locator('#focus-idle-enabled').check();await website.focus();await connection(false);
    check(await website.evaluate(input=>document.activeElement===input&&input.readOnly)&&await website.inputValue()==='unsaved.example'
      &&await page.locator('#focus-idle-enabled').isChecked(),'Connection loss preserves input focus, typed website and unrelated pending choices');
    const reconnectBefore=await requests();await connection(true);await page.waitForFunction(()=>document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');
    check(await requests()===reconnectBefore+1&&await website.evaluate(input=>document.activeElement===input&&!input.readOnly)
      &&await website.inputValue()==='unsaved.example'&&await page.locator('#focus-idle-enabled').isChecked(),'Reconnect preserves typed website and focus without committing the draft');
    await website.fill('');await saved.focus();
    await page.evaluate(()=>window.holdSelection=true);const beforeFailedSave=await requests();await saved.press('Control+s');
    await page.waitForFunction(()=>typeof window.releaseSelection==='function');await connection(false);await connection(true);
    check(await requests()===beforeFailedSave,'A reconnect waits for an active target save instead of overlapping it');
    await page.evaluate(()=>{window.holdSelection=false;window.releaseSelection('Synthetic selection failure.');});
    await page.waitForFunction(before=>window.messages.filter(message=>message.action==='focusTargets').length===before+1&&document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false',beforeFailedSave);
    check(await dialog.isVisible()&&await saved.isChecked()&&await saved.evaluate(input=>document.activeElement===input)
      &&await page.locator('#focus-idle-enabled').isChecked()&&await page.locator('#focus-picker-status').textContent().then(text=>text.includes('Synthetic selection failure')),'Failed target save drains its queued reconnect refresh and retains draft choices, focus and accessible error');
    await website.fill('pending.example');await page.evaluate(()=>window.holdSite=true);const beforeFailedAdd=await requests();await website.press('Enter');
    await page.waitForFunction(()=>typeof window.releaseSite==='function');await connection(false);await connection(true);
    check(await requests()===beforeFailedAdd,'A reconnect waits for an active manual website add');
    await page.evaluate(()=>{window.holdSite=false;window.releaseSite('Synthetic website failure.');});
    await page.waitForFunction(before=>window.messages.filter(message=>message.action==='focusTargets').length===before+1&&document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false',beforeFailedAdd);
    check(await website.inputValue()==='pending.example'&&await website.evaluate(input=>document.activeElement===input)
      &&await website.getAttribute('aria-invalid')==='true'&&await page.locator('#focus-idle-enabled').isChecked()
      &&await page.locator('#focus-picker-status').textContent().then(text=>text.includes('Synthetic website failure')),'Failed manual website add drains its queued reconnect refresh and retains text, focus, options and validation feedback');
    await page.keyboard.press('Escape');await open();await expand();
    await page.getByRole('button',{name:'Configure companion…',exact:true}).focus();await page.keyboard.press('Space');
    await page.waitForFunction(()=>window.messages.some(message=>message.action==='focusBrowserSetup'));
    check(await page.evaluate(()=>window.messages.filter(message=>message.action==='focusBrowserSetup').length===1),'Companion configuration remains keyboard accessible and explicitly activated');
    if(process.env.REFLECTION_SITE_SCREENSHOTS){await fs.mkdir(process.env.REFLECTION_SITE_SCREENSHOTS,{recursive:true});await page.screenshot({path:path.join(process.env.REFLECTION_SITE_SCREENSHOTS,'site-companion.png')});}
    await page.keyboard.press('Escape');
    check(errors.length===0,'Site picker produces no uncaught page errors: '+errors.join('; '));
    console.log(count+' Site picker checks passed.');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
