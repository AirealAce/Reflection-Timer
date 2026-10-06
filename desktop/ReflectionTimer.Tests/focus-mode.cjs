const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let passed=0;
  const check=(ok,name)=>{assert.ok(ok,name);passed++;console.log('PASS '+name);};
  try{
    const page=await browser.newPage({viewport:{width:739,height:642}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
    async function capture(name,target){if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});await target.screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,name+'.png')});}}
    await page.route('**/*',async route=>{const name=path.basename(new URL(route.request().url()).pathname);if(!/\.(html|js|css)$/.test(name))return route.abort();await route.fulfill({body:await fs.readFile(path.join(web,name)),contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'});});
    await page.addInitScript(()=>{
      const handlers=[];window.messages=[];window.choices=new Map();window.currentTabId='1-b';
      window.dispatchBridge=m=>handlers.forEach(h=>h({data:m}));
      window.settings={volume:50,tracks:[{id:0,name:'Default'},{id:6,name:'Battle (Trainer)'},{id:9,name:'None'}],sounds:[0,1,2,3,4,5].map(kind=>({kind,track:kind===5?6:0,behavior:kind===5?2:0,volume:100,fadeOutAfterSeconds:10,defaultName:'Battle (Trainer)'})),focusMode:{enabled:false,delaySeconds:5,targets:[],multipleTargets:false,idleEnabled:false,idleSeconds:20}};
      window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
        window.messages.push(m);queueMicrotask(()=>{
          if(m.action==='focusTargets'){
            if(m.data.reset){window.choices.clear();for(const t of window.settings.focusMode.targets)window.choices.set(t.id,t);}
            const kind=m.data.kind;
            let targets=window.emptyTargets?[]:['a','b'].map((suffix,index)=>({id:`${kind}-${suffix}`,key:`${kind}-${suffix}`,kind,name:window.longNames?'A long target name '.repeat(25):kind===2?'Work group':kind===1?'Same tab title':suffix==='a'?'Work window':'Other window',app:suffix==='a'&&kind===0?'EXCEL':'chrome',windowName:'Browser window',tabPosition:index+1,current:kind===1&&`${kind}-${suffix}`===window.currentTabId,selected:window.settings.focusMode.targets.some(t=>t.key===`${kind}-${suffix}`)}));
            const dynamic=[{suffix:'focused',scope:0,name:'Use focused '+['window','tab','tab group'][kind]},
              {suffix:'background',scope:kind===0?2:1,name:kind===0?'Use open windows (including background)':kind===1?'Use focused tabs (including background)':'Use focused tab groups (including background)'}];
            if(kind===2)dynamic.push({suffix:'open',scope:2,name:'Use open tab groups (including background)'});
            targets.unshift(...dynamic.map(({suffix,scope,name})=>({id:`${kind}-${suffix}`,key:`${kind}-${suffix}`,kind,useFocused:true,captureScope:scope,name,app:'',tabPosition:0,selected:window.settings.focusMode.targets.some(t=>t.key===`${kind}-${suffix}`)})));
            if(kind===0&&window.windowReplacements)targets=targets.filter(t=>t.useFocused).concat(window.windowReplacements);
            for(const t of targets)window.choices.set(t.id,t);
            if(window.repeatTarget&&targets.length)targets.push({...targets[0],id:'repeated-native-id'});
            window.dispatchBridge({type:'focusTargets',kind,targets});
          }
          if(m.action==='focusSelect'){
            const complete=()=>{
              if(window.selectionError){window.dispatchBridge({type:'reply',requestId:m.requestId,error:window.selectionError});return;}
              const targets=m.data.ids.map(id=>window.choices.get(id));if(targets.some(t=>!t))throw new Error('Unknown target sent');
              window.settings.focusMode={...window.settings.focusMode,targets,enabled:m.data.enable||window.settings.focusMode.enabled,multipleTargets:m.data.multipleTargets,idleEnabled:m.data.idleEnabled,idleSeconds:m.data.idleSeconds};
              window.successCount=(window.successCount??0)+1;window.dispatchBridge({type:'settings',settings:window.settings});window.dispatchBridge({type:'reply',requestId:m.requestId});
            };if(window.holdSelection)window.releaseSelection=complete;else complete();return;
          }
          if(m.action==='focusMode'){window.settings.focusMode={...window.settings.focusMode,...m.data};window.dispatchBridge({type:'settings',settings:window.settings});}
          if(m.action==='saveSound'){
            const complete=()=>{
              if(window.soundError){window.dispatchBridge({type:'reply',requestId:m.requestId,error:window.soundError});return;}
              const sound=window.settings.sounds.find(s=>s.kind===m.data.kind);Object.assign(sound,{track:m.data.track,custom:m.data.keepCustom,behavior:m.data.behavior,volume:m.data.volume,fadeOutEnabled:m.data.fade,fadeOutAfterSeconds:m.data.fadeSeconds});
              window.dispatchBridge({type:'settings',settings:window.settings});window.dispatchBridge({type:'reply',requestId:m.requestId});
            };if(window.holdSoundSave)window.releaseSoundSave=complete;else complete();return;
          }
          if(m.action==='browseSound'){Object.assign(window.settings.sounds.find(s=>s.kind===m.data.kind),{track:0,custom:true,customName:'Synthetic focus.mp3'});window.dispatchBridge({type:'settings',settings:window.settings});}
          window.dispatchBridge({type:'reply',requestId:m.requestId});
        });
      }}};
    });
    await page.goto('https://reflection-timer.invalid/index.html?view=main');await page.waitForFunction(()=>window.messages.some(m=>m.action==='ready'));
    await page.evaluate(()=>window.dispatchBridge({type:'settings',settings:window.settings}));
    const pressed=()=>page.locator('#focus-enabled').getAttribute('aria-pressed'),table=page.locator('#focus-target-list'),dialog=page.locator('#focus-target-dialog');
    const row=(kind,suffix)=>page.locator(`#focus-target-list tbody tr[data-key="${kind}-${suffix}"]`);
    const ready=()=>page.waitForFunction(()=>document.querySelector('#focus-target-dialog').open&&document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false'&&document.querySelector('#focus-target-list tbody tr'));
    const open=async(button='#choose-focus-target')=>{await page.locator(button).click();await ready();};
    const kind=async(value)=>{await page.locator('#focus-target-kind').selectOption(String(value));await ready();};
    const close=async()=>{await page.locator('#focus-target-cancel').click();await dialog.waitFor({state:'hidden'});};
    const saves=()=>page.evaluate(()=>window.messages.filter(m=>m.action==='focusSelect').length);
    const inline=()=>page.evaluate(()=>{const r=document.querySelector('#reset').getBoundingClientRect(),f=document.querySelector('#focus-enabled').getBoundingClientRect(),c=document.querySelector('#choose-focus-target').getBoundingClientRect();return f.x>=r.right&&c.x>=f.right&&Math.abs(f.y+f.height/2-r.y-r.height/2)<1&&Math.abs(c.y+c.height/2-f.y-f.height/2)<1;});
    const style=id=>page.locator(id).evaluate(el=>{const s=getComputedStyle(el);return {background:s.backgroundColor,color:s.color,padding:s.padding,borderRadius:s.borderRadius,height:el.getBoundingClientRect().height};});
    check(await pressed()==='false','Focus starts off');
    check(await page.locator('#focus-enabled').getAttribute('aria-keyshortcuts')==='Control+Alt+;'&&await page.locator('#focus-settings-enabled').getAttribute('aria-keyshortcuts')==='Control+Alt+;','Both Focus controls expose their global shortcut to screen readers');
    check(await page.locator('#choose-focus-target').textContent()==='Choose Window / Tab…','No saved choice has the original chooser label');
    check(await inline(),'Reset, Focus and chooser retain one row at the original App width');
    check(JSON.stringify(await style('#focus-enabled'))===JSON.stringify(await style('#reset')),'Inactive Focus still matches Reset');
    await page.evaluate(()=>window.dispatchBridge({type:'focusChooseShortcut'}));await ready();
    check(await dialog.isVisible()&&await pressed()==='false'&&await page.locator('#focus-target-kind').evaluate(e=>e===document.activeElement),'First-use global Focus opens the keyboard-accessible chooser and waits for a selection');
    await close();
    await page.evaluate(()=>document.querySelector('#guided-dialog').showModal());
    await page.evaluate(()=>window.dispatchBridge({type:'focusChooseShortcut'}));
    check(!await dialog.isVisible()&&await page.locator('#guided-dialog').evaluate(e=>e.open),'First-use Focus shortcut keeps an existing dialog in front');
    await page.evaluate(()=>document.querySelector('#guided-dialog').close());
    await open();
    check(!await page.locator('#focus-multiple-targets').isChecked()&&!await page.locator('#focus-idle-enabled').isChecked()&&await page.locator('#focus-idle-seconds').inputValue()==='20','Both new options default unchecked; Idle for defaults to 20 seconds');
    check(await page.evaluate(()=>{const idle=document.querySelector('.focus-idle-row').getBoundingClientRect(),multi=document.querySelector('.focus-multiple-row').getBoundingClientRect(),kind=document.querySelector('.focus-target-type-row').getBoundingClientRect();return idle.bottom<=multi.top&&multi.bottom<=kind.top;}),'Idle for is above Multiple Targets, which is above Target type');
    check(await page.getByRole('combobox',{name:'Target type',exact:true}).count()===1&&await page.locator('#focus-target-kind option').allTextContents().then(x=>x.join('|')==='Window|Browser Tab|Browser Tab Groups'),'Target type retains its accessible label and requested casing');
    check(await page.locator('#focus-target-list th').allTextContents().then(x=>x.join('|')==='App|Window Name'),'Windows use App and Window Name headers');
    check(await table.getByRole('checkbox').count()===0,'Single-target lists have no checkboxes');
    check(await row(0,'a').locator('td').allTextContents().then(x=>x.join('|')==='EXCEL|Work window'),'Windows put app names before window titles');
    check(await page.evaluate(()=>{const s=document.querySelector('#focus-target-kind').getBoundingClientRect(),r=document.querySelector('#focus-target-refresh').getBoundingClientRect();return r.x>=s.right&&Math.abs(s.y+s.height/2-r.y-r.height/2)<1;}),'Refresh List remains vertically aligned');
    await page.keyboard.press('Escape');await dialog.waitFor({state:'hidden'});
    check(await saves()===0&&await pressed()==='false'&&await page.locator('#choose-focus-target').evaluate(el=>el===document.activeElement),'Escape cancels the draft and restores opener focus');
    await open('#focus-enabled');await row(0,'a').focus();await page.locator('#focus-target-use').click();await dialog.waitFor({state:'hidden'});
    check(await pressed()==='true'&&await page.locator('#focus-enabled').evaluate(el=>el===document.activeElement),'Focus enables after selecting its first target');
    check(await page.locator('#choose-focus-target .focus-target-type').textContent()==='Window'&&await page.locator('#choose-focus-target .focus-target-label').textContent()==='EXCEL','A single window button displays only its app name below Window');
    check(await page.locator('#choose-focus-target').getAttribute('aria-label')==='Window: EXCEL'&&await page.locator('#choose-focus-target').getAttribute('title').then(x=>x.includes('Work window')),'The button has an accessible short label and full window tooltip');
    check(JSON.stringify(await style('#focus-enabled'))===JSON.stringify(await style('#toggle')),'Enabled Focus matches Start');
    await page.locator('#focus-enabled').press('Space');await page.waitForFunction(()=>!window.settings.focusMode.enabled);
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===1)&&await page.locator('#choose-focus-target').evaluate(el=>el.classList.contains('primary')),'Turning Focus off keeps the saved highlighted choice');
    await page.locator('#focus-enabled').press('Enter');await page.waitForFunction(()=>window.settings.focusMode.enabled);
    check(!await page.evaluate(()=>window.messages.some(m=>m.action==='toggle')),'Focus keyboard activation does not play the timer');
    await page.locator('#session-mode').click();
    await page.evaluate(()=>window.dispatchBridge({type:'state',state:{clock:{seconds:0,text:'0 seconds',status:'Ready',stopwatch:true},timer:{mode:1,durationSeconds:900,autoRestart:false,enabled:false,threshold:15},prompts:[],schedules:[],outbox:[],focusMode:window.settings.focusMode}}));
    check(await inline()&&await pressed()==='true','Stopwatch retains the same focus controls and selected target');
    await page.locator('#focus-enabled').click();await page.locator('#tab-settings').click();
    check(await page.locator('#sound-track-5').inputValue()==='6'&&await page.locator('#sound-message-fade-row-5').count()===0,'Focus audio retains Battle (Trainer) and its applicable fade controls');
    await open('#choose-focus-target-settings');
    check(await page.locator('#focus-delay').isVisible()&&await page.locator('#focus-picker-audio').evaluate(e=>!e.open)&&!await page.locator('#sound-volume-5-picker').isVisible(),'Chooser has the away delay and collapsed Focus audio by default');
    check(await page.evaluate(()=>{const idle=document.querySelector('.focus-idle-row').getBoundingClientRect(),audio=document.querySelector('#focus-picker-audio').getBoundingClientRect(),multiple=document.querySelector('.focus-multiple-row').getBoundingClientRect(),footer=document.querySelector('#focus-target-dialog>.actions').getBoundingClientRect();return idle.bottom<=audio.top&&audio.bottom<=multiple.top&&audio.bottom<=footer.top;}),'Collapsed Focus audio is placed with the alert timing controls and is visible above the fixed Save row');
    await page.locator('#focus-delay').fill('8');await page.locator('#focus-delay').press('Tab');await page.waitForFunction(()=>window.settings.focusMode.delaySeconds===8);
    check(!await page.evaluate(()=>window.messages.some(m=>m.action==='saveSound')),'Editing the away delay leaves audio selections untouched');
    await page.locator('#focus-picker-audio>summary').press('Enter');
    check(await page.locator('#sound-track-5-picker').inputValue()==='6'&&await page.locator('#sound-volume-5-picker').isVisible()&&await page.locator('#sound-fade-seconds-5-picker').isDisabled(),'Expanded Focus audio has the existing track, volume and correct fade availability');
    check(await page.evaluate(()=>{const ids=[...document.querySelectorAll('[id]')].map(e=>e.id);return ids.length===new Set(ids).size;}),'Chooser audio has unique control and description IDs');
    await capture('focus-audio-expanded',dialog);
    await close();
    await open('#choose-focus-target-settings');await kind(1);
    check(await page.locator('#focus-target-list th').allTextContents().then(x=>x.join('|')==='Tab #|Tab Name|App'),'Tabs have number, name and app headers');
    check(await page.locator('#focus-target-list th').first().evaluate(el=>el.getBoundingClientRect().width<=49&&getComputedStyle(el).textAlign==='left'&&el.scrollWidth<=el.clientWidth),'Tab # is narrow and left aligned without clipping its header');
    check(await table.evaluate(el=>el.getBoundingClientRect().top-document.querySelector('.focus-target-type-row').getBoundingClientRect().bottom>=12),'The table has clear spacing below Target type');
    check(await table.locator('tbody tr').nth(2).getAttribute('data-key')==='1-b'&&await row(1,'b').locator('td').nth(1).textContent()==='(current tab) Same tab title','The current tab follows the dynamic options with its own name and actual tab number');
    await row(1,'a').focus();await row(1,'a').press('Enter');await dialog.waitFor({state:'hidden'});
    check(await page.locator('#choose-focus-target-settings .focus-target-label').textContent()==='1 - Same tab title','A single tab button displays Tab # - Tab Name');
    await open('#choose-focus-target-settings');check(await row(1,'a').evaluate(el=>el.classList.contains('picked')),'Reopening retains the saved tab rather than replacing it with the current tab');
    await page.evaluate(()=>{window.currentTabId='1-a';window.repeatTarget=true;});await page.locator('#focus-target-refresh').click();await ready();
    check(await table.locator('tbody tr').count()===4&&await table.locator('tbody tr').nth(2).getAttribute('data-key')==='1-a','Refresh keeps the dynamic options first and deduplicates repeated native keys without merging same-title tabs');
    await close();await page.evaluate(()=>window.repeatTarget=false);
    for(const [k,gesture,expected] of [[0,'Enter','chrome'],[1,'Space','2 - Same tab title'],[2,'Double-click','Work group']]){
      await open('#choose-focus-target-settings');await kind(k);await row(k,'b').focus();const count=await saves();
      if(gesture==='Double-click')await row(k,'b').dblclick();else await row(k,'b').press(gesture);await dialog.waitFor({state:'hidden'});
      check(await saves()===count+1&&await page.evaluate(k=>window.settings.focusMode.targets[0].id===`${k}-b`,k),gesture+' confirms the exact selected target once');
      check(await page.locator('#choose-focus-target-settings .focus-target-label').textContent()===expected&&await page.locator('#choose-focus-target-settings').evaluate(el=>el===document.activeElement),'Single-target button format and opener focus are correct after '+gesture);
      check(await pressed()==='false',gesture+' does not enable Focus unless its toggle requested it');
    }
    await open('#choose-focus-target-settings');
    check(await page.locator('#focus-target-list th').allTextContents().then(x=>x.join('|')==='Grp #|Group Name|App'),'Tab groups have number, group name and app headers');
    check(await page.locator('#focus-target-list th').first().evaluate(el=>el.getBoundingClientRect().width<=49&&getComputedStyle(el).textAlign==='left'&&el.scrollWidth<=el.clientWidth),'Grp # is narrow and left aligned without clipping its header');
    await row(2,'b').focus();await row(2,'b').press('ArrowUp');check(await row(2,'a').evaluate(el=>el===document.activeElement)&&await dialog.isVisible(),'Arrow keys move between rows without confirming');
    let count=await saves();await page.keyboard.down('Space');await page.keyboard.down('Space');check(await saves()===count&&await dialog.isVisible(),'Holding Space waits for release');await page.keyboard.up('Space');await dialog.waitFor({state:'hidden'});check(await saves()===count+1,'Space saves once on release');
    await open('#choose-focus-target-settings');await row(2,'a').focus();count=await saves();await page.keyboard.down('Enter');await dialog.waitFor({state:'hidden'});await page.keyboard.down('Enter');await page.keyboard.up('Enter');check(await saves()===count+1&&!await dialog.isVisible(),'Held Enter cannot repeatedly save or reopen the picker');
    await open('#choose-focus-target-settings');await page.evaluate(()=>window.holdSelection=true);await row(2,'a').focus();count=await saves();await row(2,'a').press('Enter');await page.waitForFunction(()=>document.querySelector('#focus-target-dialog').getAttribute('aria-busy')==='true');
    check(await page.locator('#focus-target-kind').isDisabled()&&await page.locator('#focus-target-refresh').isDisabled()&&await table.evaluate(el=>el.inert)&&await page.locator('#focus-target-cancel').isDisabled(),'All draft controls are locked while a save is in flight');
    await page.keyboard.press('Control+s');await page.keyboard.press('Escape');check(await saves()===count+1&&await dialog.isVisible(),'Overlapping saves and Escape cannot discard or duplicate an in-flight selection');
    await page.evaluate(()=>{window.holdSelection=false;window.releaseSelection();});await dialog.waitFor({state:'hidden'});
    await open('#choose-focus-target-settings');await row(2,'a').focus();await page.evaluate(()=>window.selectionError='That target could not be saved. Refresh and retry.');await row(2,'a').press('Enter');await page.waitForFunction(()=>document.querySelector('#focus-picker-status').textContent.includes('could not be saved'));
    check(await dialog.isVisible()&&!await page.locator('#focus-target-use').isDisabled()&&await row(2,'a').evaluate(el=>el===document.activeElement),'A failed save keeps an accessible inline error and restores row focus for retry');
    await page.evaluate(()=>window.selectionError=null);await row(2,'a').press('Enter');await dialog.waitFor({state:'hidden'});
    for(const [selector,shortcut] of [['#focus-target-kind','Control+Enter'],['#focus-idle-seconds','Control+s'],['#focus-multiple-targets','Control+Enter'],['#focus-target-refresh','Control+s'],['#focus-target-cancel','Control+Enter']]){
      await open('#choose-focus-target-settings');count=await saves();await page.locator(selector).press(shortcut);await dialog.waitFor({state:'hidden'});check(await saves()===count+1,shortcut+' saves while '+selector+' is focused');
    }
    await page.locator('#tab-timer').click();await open();count=await saves();await page.evaluate(()=>window.dispatchBridge({type:'settingsSaveShortcut'}));await dialog.waitFor({state:'hidden'});
    check(await saves()===count+1,'The native WebView save shortcut also saves the picker when opened from Timer');
    await open();await page.locator('#focus-multiple-targets').check();
    check(await table.getByRole('checkbox').count()===5&&await page.locator('#focus-target-use').textContent()==='Save selected targets','Multiple Targets adds native, labelled checkboxes and an explicit Save button');
    await kind(0);await row(0,'a').getByRole('checkbox').check();await kind(1);await row(1,'a').getByRole('checkbox').check();await kind(2);
    check(await row(2,'a').getByRole('checkbox').isChecked(),'Changing categories retains the group selection');
    await kind(0);check(await row(0,'a').getByRole('checkbox').isChecked(),'The window checkbox remains checked after visiting other categories');
    await kind(1);check(await row(1,'a').getByRole('checkbox').isChecked(),'The tab checkbox remains checked after visiting other categories');
    await page.locator('#focus-idle-enabled').check();await page.locator('#focus-idle-seconds').fill('24');await page.locator('#focus-idle-seconds').press('Control+Enter');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===3&&window.settings.focusMode.multipleTargets&&window.settings.focusMode.idleEnabled&&window.settings.focusMode.idleSeconds===24),'One atomic save retains mixed categories and edited idle settings');
    check(await page.locator('#choose-focus-target').textContent().then(x=>x.trim()==='Window, Tab, Tab Group')&&await page.locator('#choose-focus-target-settings').getAttribute('aria-label')==='Window, Tab, Tab Group','Multiple saved targets show just their categories on both chooser buttons');
    await open();check(await page.locator('#focus-multiple-targets').isChecked()&&await page.locator('#focus-idle-enabled').isChecked()&&await page.locator('#focus-idle-seconds').inputValue()==='24','Saved checkbox and idle-time choices are restored on reopening');
    await page.locator('#focus-idle-seconds').fill('0');count=await saves();await page.locator('#focus-idle-seconds').press('Control+s');check(await saves()===count&&await dialog.isVisible()&&!await page.locator('#focus-idle-seconds').evaluate(el=>el.checkValidity()),'Invalid idle time is rejected without closing the chooser or saving partial changes');await page.locator('#focus-idle-seconds').fill('24');
    await kind(1);await row(1,'a').getByRole('checkbox').uncheck();await row(1,'b').getByRole('checkbox').focus();count=await saves();await row(1,'b').getByRole('checkbox').press('Space');
    check(await row(1,'b').getByRole('checkbox').isChecked()&&await saves()===count,'Space toggles a checkbox in multiple mode without saving prematurely');
    await row(1,'b').getByRole('checkbox').press('Enter');check(!await row(1,'b').getByRole('checkbox').isChecked()&&await saves()===count,'Enter toggles the multi-target row without confirming');
    await page.locator('#focus-target-use').click();await dialog.waitFor({state:'hidden'});check(await page.locator('#choose-focus-target').getAttribute('aria-label')==='Window, Tab Group','The category label reflects removing the final selected tab');
    await open();await kind(0);await row(0,'b').dblclick();check(await row(0,'b').getByRole('checkbox').isChecked(),'Double-click toggles a multi-target row once');await close();
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===2),'Cancel discards changes to the selected targets');
    await open();await kind(2);await page.evaluate(()=>window.emptyTargets=true);await page.locator('#focus-target-refresh').click();await ready();
    check(await row(2,'a').evaluate(el=>el.classList.contains('unavailable'))&&await row(2,'a').getByRole('checkbox').isChecked(),'A saved closed target remains visible so it can be removed');
    await row(2,'a').getByRole('checkbox').uncheck();await page.locator('#focus-target-use').click();await dialog.waitFor({state:'hidden'});check(await page.evaluate(()=>window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].kind===0),'A closed target can be unchecked while retaining selections in other categories');
    await page.evaluate(()=>window.emptyTargets=false);await open();await page.locator('#focus-multiple-targets').uncheck();await close();check(await page.evaluate(()=>window.settings.focusMode.multipleTargets),'Cancelling option changes preserves the saved mode');
    await open();await page.locator('#focus-multiple-targets').uncheck();await page.locator('#focus-target-use').click();await dialog.waitFor({state:'hidden'});check(await page.evaluate(()=>!window.settings.focusMode.multipleTargets)&&await page.locator('#choose-focus-target .focus-target-label').textContent()==='EXCEL','Returning to a single target restores the two-line app-name button');
    await open();await page.locator('#focus-multiple-targets').check();await row(0,'a').getByRole('checkbox').uncheck();await page.locator('#focus-target-use').click();await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===0&&window.settings.focusMode.idleEnabled)&&await page.locator('#choose-focus-target').textContent()==='Choose Window / Tab…','Idle-only focus can be saved with no window selection');
    await page.locator('#focus-enabled').click();await page.waitForFunction(()=>window.settings.focusMode.enabled);check(!await dialog.isVisible(),'Idle-only Focus enables without demanding a target');
    await page.locator('#focus-enabled').click();await open();await page.locator('#focus-idle-enabled').uncheck();await close();check(await page.evaluate(()=>window.settings.focusMode.idleEnabled),'Cancel also restores saved idle options');
    await page.locator('#tab-settings').click();await open('#choose-focus-target-settings');await page.locator('#focus-delay').fill('12');await page.locator('#focus-delay').press('Control+s');await dialog.waitFor({state:'hidden'});check(await page.evaluate(()=>window.settings.focusMode.delaySeconds===12),'Chooser Ctrl+S flushes an unblurred away-delay edit');
    await open('#choose-focus-target-settings');
    await page.locator('#focus-delay').fill('-1');await page.locator('#focus-delay').press('Tab');check(!await page.locator('#focus-delay').evaluate(el=>el.checkValidity()),'Negative away delays remain invalid');await page.locator('#focus-delay').fill('5');await page.locator('#focus-delay').press('Tab');
    await close();
    await page.locator('#sound-track-5').selectOption('9');await page.waitForFunction(()=>window.messages.some(m=>m.action==='saveSound'&&m.data.kind===5&&m.data.track===9));check(true,'Focus audio remains independently configurable');
    await open('#choose-focus-target-settings');await capture('focus-audio-collapsed',dialog);
    check(await page.locator('#sound-track-5-picker').inputValue()==='9'&&!await page.locator('#focus-picker-audio').evaluate(e=>e.open),'Reopened chooser reflects the Settings track and resets only its audio disclosure');
    await page.evaluate(()=>{window.settings.showAllExplanations=true;window.dispatchBridge({type:'settings',settings:window.settings});});
    check(!await page.locator('#focus-picker-audio').evaluate(e=>e.open),'Show all explanations does not expand the chooser audio controls');
    await page.evaluate(()=>{window.settings.showAllExplanations=false;window.dispatchBridge({type:'settings',settings:window.settings});});
    await page.locator('#focus-picker-audio>summary').press('Space');
    check(await page.locator('#focus-picker-audio').evaluate(e=>e.open),'Focus audio disclosure supports Space as well as Enter');
    await page.evaluate(()=>{window.holdSoundSave=true;document.querySelector('#sound-volume-5-picker').value='56';document.querySelector('#sound-volume-5-picker').dispatchEvent(new Event('input',{bubbles:true}));});
    await page.waitForFunction(()=>!!window.releaseSoundSave);
    check(await page.locator('#sound-volume-5').inputValue()==='56'&&await page.locator('#sound-volume-caption-5-picker').textContent()==='Volume (56%)','Chooser volume edits immediately synchronize the Settings control and caption');
    await page.locator('#sound-fade-5-picker').check();await page.locator('#sound-fade-seconds-5-picker').fill('11');
    await page.evaluate(()=>{window.holdSoundSave=false;window.releaseSoundSave();});
    await page.waitForFunction(()=>window.settings.sounds.find(s=>s.kind===5).fadeOutAfterSeconds===11);
    check(await page.evaluate(()=>{const s=window.settings.sounds.find(s=>s.kind===5);return s.volume===56&&s.fadeOutEnabled&&s.fadeOutAfterSeconds===11;})&&await page.locator('#sound-fade-5').isChecked()&&await page.locator('#sound-fade-seconds-5').inputValue()==='11','One shared save queue preserves newer fade edits when an older volume save completes');
    const previews=await page.evaluate(()=>window.messages.filter(m=>m.action==='previewSound').length);
    await page.locator('#preview-sound-5-picker').click();
    await page.waitForFunction(count=>window.messages.filter(m=>m.action==='previewSound').length>count,previews);
    check(await page.evaluate(()=>{const m=window.messages.filter(m=>m.action==='previewSound').at(-1);return m.data.kind===5&&!m.data.quiet;})&&await page.locator('#sound-behavior-5-picker').inputValue()==='2','Chooser Preview uses the existing Focus audio event and saved playback/fade settings');
    await page.locator('#browse-sound-5-picker').click();await page.waitForFunction(()=>document.querySelector('#sound-track-5-picker').value==='custom');
    check(await page.locator('#sound-track-5').inputValue()==='custom'&&await page.locator('#sound-track-5-picker option:checked').textContent()==='Custom MP3 · Synthetic focus.mp3','Choose MP3 updates both Focus audio surfaces without losing the custom selection');
    await page.locator('#focus-picker-stop-audio').click();await page.waitForFunction(()=>window.messages.some(m=>m.action==='stopSound'));
    check(true,'Chooser Stop audio reaches the existing audio stop command');
    await page.evaluate(()=>window.soundError='Synthetic audio save failed.');await page.locator('#sound-volume-5-picker').evaluate(e=>{e.value='58';e.dispatchEvent(new Event('input',{bubbles:true}));});
    await page.locator('#sound-volume-5-picker').press('Control+s');await page.waitForFunction(()=>document.querySelector('#focus-picker-status').textContent.includes('audio save failed'));
    check(await dialog.isVisible()&&!await page.locator('#focus-target-cancel').isDisabled()&&await page.locator('#sound-volume-5-picker').inputValue()==='58','A failed audio save retains the chooser edit and an accessible inline error');
    await page.evaluate(()=>window.soundError=null);await page.locator('#sound-volume-5-picker').press('Control+Enter');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.sounds.find(s=>s.kind===5).volume===58)&&await page.locator('#choose-focus-target-settings').evaluate(e=>e===document.activeElement),'Ctrl+Enter retries pending audio and saves targets, restoring opener focus');
    await open('#choose-focus-target-settings');await page.locator('#focus-picker-audio>summary').click();
    const beforeInvalid=await saves();await page.locator('#sound-fade-seconds-5-picker').fill('0');await page.locator('#sound-fade-seconds-5-picker').press('Control+s');
    check(await dialog.isVisible()&&await saves()===beforeInvalid&&await page.locator('#sound-fade-seconds-5-picker').evaluate(e=>e===document.activeElement&&!e.checkValidity()),'Invalid audio timing prevents chooser save and focuses its invalid control');
    await page.locator('#sound-fade-seconds-5-picker').fill('11');await page.locator('#sound-fade-seconds-5-picker').press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.sounds.find(s=>s.kind===5).fadeOutAfterSeconds===11),'Ctrl+S saves the corrected audio timing and target selection');
    for(const theme of [0,1,2,3]){
      await page.evaluate(theme=>{window.settings.theme=theme;window.dispatchBridge({type:'settings',settings:window.settings});window.longNames=true;},theme);
      await open('#choose-focus-target-settings');await kind(1);await page.locator('#focus-multiple-targets').check();await ready();
      check(await table.evaluate(el=>{const rows=[...el.querySelectorAll('tbody tr')];return [...rows[0].cells].every((cell,index)=>Math.abs(cell.getBoundingClientRect().width-rows[1].cells[index].getBoundingClientRect().width)<1);}), 'All cells align to fixed column widths in theme '+theme);
      await page.locator('#focus-target-kind').focus();await page.mouse.move(0,0);
      check(await row(1,'a').locator('.focus-cell').nth(1).evaluate(el=>{const s=getComputedStyle(el);return s.textOverflow==='ellipsis'&&s.whiteSpace==='nowrap'&&el.scrollWidth>el.clientWidth;}),'Long un-targeted names use ellipsis in theme '+theme);
      const unwrappedHeight=await row(1,'a').evaluate(el=>el.getBoundingClientRect().height);await row(1,'a').hover();
      check(await row(1,'a').locator('.focus-cell').nth(1).evaluate(el=>getComputedStyle(el).whiteSpace==='nowrap')&&await row(1,'a').evaluate(el=>el.getBoundingClientRect().height)===unwrappedHeight,'Hovering does not wrap or resize a row in theme '+theme);
      await row(1,'a').getByRole('checkbox').focus();
      check(await row(1,'a').locator('.focus-cell').nth(1).evaluate(el=>getComputedStyle(el).whiteSpace==='normal')&&await row(1,'b').locator('.focus-cell').nth(1).evaluate(el=>getComputedStyle(el).whiteSpace==='nowrap'),'Only the targeted row wraps its full name in theme '+theme);
      check(await page.evaluate(()=>document.querySelector('#focus-target-dialog').scrollWidth<=document.querySelector('#focus-target-dialog').clientWidth+1),'Long names do not widen the dialog in theme '+theme);
      check(await page.locator('#focus-target-use').evaluate(el=>{const r=el.getBoundingClientRect(),dialog=document.querySelector('#focus-target-dialog').getBoundingClientRect();return r.top>=dialog.top&&r.bottom<=dialog.bottom&&r.bottom<=innerHeight;}),'Save stays visible while long names wrap in theme '+theme);
      await capture('focus-target-table-theme-'+theme,dialog);await page.locator('#focus-picker-audio>summary').click();
      check(await page.locator('#sound-volume-5-picker').isVisible()&&await dialog.evaluate(e=>{const body=e.querySelector('.focus-picker-body');return e.scrollWidth<=e.clientWidth+1&&body.scrollWidth<=body.clientWidth+1;}),'Expanded Focus audio fits the themed chooser in theme '+theme);
      await capture('focus-audio-theme-'+theme,dialog);await close();
    }
    await page.evaluate(()=>{window.longNames=false;window.settings.focusMode.targets=[{id:'1-b',key:'1-b',kind:1,name:'Long saved tab '.repeat(30),app:'chrome',windowName:'Browser window',tabPosition:2}];window.dispatchBridge({type:'settings',settings:window.settings});});await page.locator('#tab-timer').click();
    check(await inline()&&await page.locator('#choose-focus-target').evaluate(el=>{const name=el.querySelector('.focus-target-label');return el.getBoundingClientRect().width<=260&&name.scrollWidth>name.clientWidth;}),'Long saved names stay clipped without enlarging the main action row');
    check(await page.locator('#choose-focus-target').getAttribute('aria-label').then(x=>x.startsWith('Tab: 2 - Long saved tab '))&&await page.locator('#choose-focus-target').getAttribute('title').then(x=>x.includes('Browser window')),'Accessible labels retain the full saved name and tooltip context');
    for(const width of [420,336]){
      await page.setViewportSize({width,height:642});await open();await page.locator('#focus-picker-audio>summary').click();
      await capture('focus-audio-width-'+width,dialog);
      const fit=await dialog.evaluate(e=>{const body=e.querySelector('.focus-picker-body');return {width:e.clientWidth,content:e.scrollWidth,bodyWidth:body.clientWidth,bodyContent:body.scrollWidth};});
      check(fit.content<=fit.width+1&&fit.bodyContent<=fit.bodyWidth+1,'Chooser audio stays within the window at '+width+' pixels: '+JSON.stringify(fit));
      check(await page.locator('#focus-target-use').isVisible(),'Save remains available in the narrow chooser at '+width+' pixels');await close();
    }
    await page.setViewportSize({width:739,height:642});
    await page.evaluate(()=>{window.settings.focusMode={...window.settings.focusMode,enabled:false,targets:[],multipleTargets:false};window.dispatchBridge({type:'settings',settings:window.settings});});
    for(const [k,gesture] of [[0,'Enter'],[1,'Space'],[2,'Double-click']]){
      await open();await kind(k);
      check(await table.locator('tbody tr').first().getAttribute('data-key')===`${k}-focused`&&await row(k,'focused').getAttribute('aria-label')==='Use focused '+['window','tab','tab group'][k],'Dynamic '+k+' is first with its own accessible name');
      check(await row(k,'focused').locator('td').allTextContents().then(x=>x.includes('Use focused '+['window','tab','tab group'][k])&&!x.includes('0')&&!x.includes('?')),'Dynamic '+k+' has no invented tab number or app name');
      await row(k,'focused').focus();if(gesture==='Double-click')await row(k,'focused').dblclick();else await row(k,'focused').press(gesture);await dialog.waitFor({state:'hidden'});
      check(await page.evaluate(k=>window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].useFocused&&window.settings.focusMode.targets[0].kind===k,k),'Keyboard/mouse confirmation saves dynamic '+k+' as a choice rather than an ordinary target');
      check(await page.locator('#choose-focus-target').getAttribute('aria-label')==='Use focused '+['window','tab','tab group'][k],'The button identifies the saved dynamic '+k+' choice');
      await open();check(await row(k,'focused').evaluate(e=>e.classList.contains('picked')),'Reopening retains dynamic '+k+' instead of the current ordinary target');await close();
    }
    await open();await page.locator('#focus-multiple-targets').check();
    for(const k of [0,1,2]){await kind(k);await row(k,'focused').getByRole('checkbox').check();}
    await kind(0);await row(0,'a').getByRole('checkbox').check();await page.locator('#focus-target-kind').press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===4&&window.settings.focusMode.targets.filter(t=>t.useFocused).length===3),'Ctrl+S saves all three dynamic categories together with an ordinary target');
    check(await page.locator('#choose-focus-target').getAttribute('aria-label')==='Window, Tab, Tab Group','Mixed dynamic choices retain the category-only multi-target button');
    await open();for(const k of [0,1,2]){await kind(k);check(await row(k,'focused').getByRole('checkbox').isChecked(),'Reopening keeps dynamic '+k+' checked across categories');}
    await page.evaluate(()=>window.emptyTargets=true);await page.locator('#focus-target-refresh').click();await ready();
    check(await table.locator('tbody tr').count()===3&&await row(2,'focused').getByRole('checkbox').isChecked()&&!await row(2,'focused').evaluate(e=>e.classList.contains('unavailable')),'Dynamic choices remain selectable when no ordinary targets are open');
    await page.locator('#focus-target-cancel').press('Control+Enter');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets.filter(t=>t.useFocused).length===3),'Ctrl+Enter retains all dynamic choices from an empty ordinary list');
    await page.evaluate(()=>{window.settings.focusMode.targets=window.settings.focusMode.targets.filter(t=>t.useFocused);window.dispatchBridge({type:'settings',settings:window.settings});});
    await open();
    const expectedDynamic=[['Use focused window','Use open windows (including background)'],['Use focused tab','Use focused tabs (including background)'],['Use focused tab group','Use focused tab groups (including background)','Use open tab groups (including background)']];
    for(const k of [0,1,2]){
      await kind(k);
      check(await table.locator('tbody tr').evaluateAll(rows=>rows.map(r=>r.getAttribute('aria-label'))).then(names=>JSON.stringify(names)===JSON.stringify(expectedDynamic[k])),'Empty category '+k+' lists each capture scope in the requested order with its full accessible name');
      await row(k,'background').getByRole('checkbox').check();if(k===2)await row(k,'open').getByRole('checkbox').check();
    }
    await page.locator('#focus-target-use').press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===7&&new Set(window.settings.focusMode.targets.map(t=>`${t.kind}:${t.captureScope}`)).size===7),'Multiple Targets saves all seven original/background scopes without merging choices of the same category');
    await open();for(const k of [0,1,2]){await kind(k);check(await table.getByRole('checkbox').evaluateAll(boxes=>boxes.every(b=>b.checked)),'Every saved capture scope stays checked when reopening category '+k);}
    await row(2,'open').getByRole('checkbox').uncheck();await close();
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===7),'Cancelling a changed background choice leaves saved capture scopes intact');
    await page.evaluate(()=>{
      window.emptyTargets=false;
      const old=suffix=>({id:`0-old${suffix}`,key:`0-old${suffix}`,kind:0,name:'Work window',app:'EXCEL',windowName:'Work window'});
      window.oldWindows=[old(1),old(2)];
      window.windowReplacements=[{id:'0-restored',key:'0-restored',kind:0,name:'Work window',app:'EXCEL',windowName:'Work window',replacesKeys:window.oldWindows.map(t=>t.key),selected:true}];
      window.settings.focusMode={...window.settings.focusMode,targets:window.oldWindows,multipleTargets:true};window.dispatchBridge({type:'settings',settings:window.settings});
    });
    await open();
    check(await table.locator('tbody tr.unavailable').count()===0&&await table.locator('tbody tr').count()===3&&await table.getByRole('checkbox').evaluateAll(boxes=>boxes.filter(b=>b.checked).length===1),'Reconnected duplicate bookmarks become one checked live window with no gray stale rows');
    await row(0,'restored').getByRole('checkbox').press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].id==='0-restored'),'Saving the restored selection sends the current window identity');
    await page.evaluate(()=>{
      window.windowReplacements=[{...window.oldWindows[0],selected:true}];window.settings.focusMode.targets=[window.oldWindows[0]];window.dispatchBridge({type:'settings',settings:window.settings});
    });await open();await row(0,'old1').getByRole('checkbox').uncheck();
    await page.evaluate(()=>window.windowReplacements=[{id:'0-restored',key:'0-restored',kind:0,name:'Work window',app:'EXCEL',windowName:'Work window',replacesKeys:['0-old1'],selected:true}]);
    await page.locator('#focus-target-refresh').click();await ready();
    check(!await row(0,'restored').getByRole('checkbox').isChecked()&&await table.locator('tbody tr.unavailable').count()===0,'Refreshing a reconnect respects an unchecked unsaved choice instead of rechecking it from the saved profile');await close();
    await page.evaluate(()=>{
      window.windowReplacements=[1,2].map(i=>({id:`0-distinct${i}`,key:`0-distinct${i}`,kind:0,name:'Same window title',app:'chrome',windowName:'Same window title'}));
      window.settings.focusMode.targets=[];window.dispatchBridge({type:'settings',settings:window.settings});
    });await open();
    check(await row(0,'distinct1').count()===1&&await row(0,'distinct2').count()===1,'Separate live windows sharing an app and title retain their own selectable rows');await close();
    // A global shortcut edits the same saved checkbox and any open chooser draft.
    const toggleTarget=async(target,checked,keys=[target.key])=>page.evaluate(({target,checked,keys})=>{
      window.choices.set(target.id,target);
      const remaining=window.settings.focusMode.targets.filter(t=>!keys.includes(t.key));
      const targets=checked?[...remaining,target]:remaining;
      window.settings.focusMode={...window.settings.focusMode,targets,enabled:window.settings.focusMode.enabled&&(targets.length>0||window.settings.focusMode.idleEnabled),
        multipleTargets:window.settings.focusMode.multipleTargets||checked&&remaining.length>0};
      window.dispatchBridge({type:'settings',settings:window.settings});
      window.dispatchBridge({type:'focusTargetToggled',target,checked,keys,multipleTargets:window.settings.focusMode.multipleTargets,enabled:window.settings.focusMode.enabled});
    },{target,checked,keys});
    const windowA={id:'0-a',key:'0-a',kind:0,name:'Work window',app:'EXCEL',windowName:'Work window'};
    const tabB={id:'1-b',key:'1-b',kind:1,name:'Same tab title',app:'chrome',windowName:'Browser window',tabPosition:2};
    await page.evaluate(({windowA,tabB})=>{
      window.windowReplacements=null;window.settings.focusMode={...window.settings.focusMode,targets:[windowA,tabB],multipleTargets:true,idleEnabled:false,idleSeconds:20};
      window.dispatchBridge({type:'settings',settings:window.settings});
    },{windowA,tabB});
    await open();await row(0,'b').getByRole('checkbox').check();await page.locator('#focus-idle-enabled').check();await page.locator('#focus-idle-seconds').fill('31');
    await row(0,'a').getByRole('checkbox').focus();await page.evaluate(()=>window.originalCheckbox=document.querySelector('tr[data-key="0-a"] input'));
    await toggleTarget(windowA,false);
    check(!await row(0,'a').getByRole('checkbox').isChecked()&&await row(0,'b').getByRole('checkbox').isChecked(),'The shortcut immediately unchecks its open chooser checkbox and keeps another unsaved target checked');
    check(await row(0,'a').getByRole('checkbox').evaluate(e=>e===window.originalCheckbox&&e===document.activeElement),'Updating a live checkbox preserves its DOM identity and keyboard focus');
    check(await page.locator('#focus-idle-enabled').isChecked()&&await page.locator('#focus-idle-seconds').inputValue()==='31','A global target toggle retains pending idle settings');
    await page.locator('#focus-target-refresh').click();await ready();
    check(!await row(0,'a').getByRole('checkbox').isChecked(),'Refreshing cannot recheck a target removed by the shortcut');
    await toggleTarget(windowA,true);check(await row(0,'a').getByRole('checkbox').isChecked(),'Pressing again checks that same live target without reopening the chooser');
    await page.locator('#focus-target-kind').press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>['0-a','0-b','1-b'].every(key=>window.settings.focusMode.targets.some(t=>t.key===key))&&window.settings.focusMode.targets.length===3&&window.settings.focusMode.idleEnabled&&window.settings.focusMode.idleSeconds===31),'Ctrl+S saves the toggled checkbox together with unsaved targets and idle options');
    await toggleTarget(windowA,false);
    check(!await dialog.isVisible()&&await page.evaluate(()=>!window.settings.focusMode.targets.some(t=>t.key==='0-a')),'The shortcut updates saved targets without opening a closed chooser');
    await open();await kind(0);
    const newWindow={id:'0-new',key:'0-new',kind:0,name:'New document',app:'NOTEPAD',windowName:'New document'};
    await toggleTarget(newWindow,true);
    check(await row(0,'new').getByRole('checkbox').isChecked(),'A newly checked target is added to an already-open list without Refresh List');
    await page.locator('#focus-target-kind').press('Control+Enter');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets.some(t=>t.id==='0-new')),'The new target can be saved immediately using its registered identity');
    await open();await kind(1);await row(1,'a').getByRole('checkbox').check();await toggleTarget(newWindow,false);
    await page.locator('#focus-target-kind').press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>!window.settings.focusMode.targets.some(t=>t.key==='0-new')&&window.settings.focusMode.targets.some(t=>t.key==='1-a')),'Toggling a different category retains the current category draft without saving a removed target');
    await page.evaluate(({windowA})=>{
      const old=suffix=>({...windowA,id:'0-obsolete'+suffix,key:'0-obsolete'+suffix});
      window.settings.focusMode.targets=[old(1),old(2)];window.dispatchBridge({type:'settings',settings:window.settings});
    },{windowA});await open();
    check(await table.locator('tbody tr.unavailable').count()===2,'The fixture represents two obsolete saved aliases separately');
    await row(0,'b').getByRole('checkbox').check();await toggleTarget(windowA,false,['0-a','0-obsolete1','0-obsolete2']);
    check(await table.locator('tbody tr.unavailable').count()===0&&!await row(0,'a').getByRole('checkbox').isChecked()&&await row(0,'b').getByRole('checkbox').isChecked(),'Unchecking the current window removes its obsolete gray aliases while retaining unrelated choices');
    await page.locator('#focus-target-kind').press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].key==='0-b'),'Saving after the shortcut cannot resurrect obsolete checked aliases');
    const returned={...newWindow,id:'0-returned',key:'0-returned',name:'Returned document',windowName:'Returned document'};
    await page.evaluate(({returned})=>{window.settings.focusMode.targets=[returned];window.dispatchBridge({type:'settings',settings:window.settings});},{returned});await open();
    check(await row(0,'returned').evaluate(e=>e.classList.contains('unavailable')),'The saved returning-window fixture starts unavailable');
    await toggleTarget(returned,true);
    check(await row(0,'returned').getByRole('checkbox').isChecked()&&!await row(0,'returned').evaluate(e=>e.classList.contains('unavailable')),'A captured live target replaces its gray placeholder and remains checked');await close();
    await page.evaluate(({windowA})=>{window.settings.focusMode.targets=[windowA];window.dispatchBridge({type:'settings',settings:window.settings});},{windowA});await open();
    await page.evaluate(()=>window.holdSelection=true);const beforeRace=await saves();await page.locator('#focus-target-kind').press('Control+s');
    await page.waitForFunction(()=>document.querySelector('#focus-target-dialog').getAttribute('aria-busy')==='true');
    await toggleTarget(windowA,false);await page.evaluate(()=>window.releaseSelection());
    await page.waitForFunction(n=>window.messages.filter(m=>m.action==='focusSelect').length===n+2,beforeRace);
    await page.evaluate(()=>{window.holdSelection=false;window.releaseSelection();});await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>!window.settings.focusMode.targets.some(t=>t.key==='0-a')),'A shortcut arriving during a pending chooser save wins over that older save');
    await page.evaluate(({windowA})=>{window.settings.focusMode={...window.settings.focusMode,targets:[windowA],enabled:true,idleEnabled:false};window.dispatchBridge({type:'settings',settings:window.settings});},{windowA});await open();
    await page.locator('#focus-delay').fill('11');await toggleTarget(windowA,false);
    check(await pressed()==='false'&&await page.locator('#focus-delay').inputValue()==='11'&&!await page.locator('#focus-target-use').isDisabled(),'Removing the last target updates Focus off while retaining an unsaved delay and allowing an empty selection to be saved');
    await page.locator('#focus-target-use').press('Control+s');await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(()=>!window.settings.focusMode.enabled&&window.settings.focusMode.delaySeconds===11&&window.settings.focusMode.targets.length===0),'Saving the pending delay cannot turn Focus back on after the shortcut unchecked its final target');
    check(errors.length===0,'Focus controls have no browser script errors');
    console.log(`${passed} focus browser checks passed.`);
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
