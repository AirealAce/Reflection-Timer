// Run with Playwright on NODE_PATH. The browser receives synthetic state only.
const { chromium } = require('playwright');
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const web = path.resolve(__dirname, '../ReflectionTimer.Desktop/Web');
(async () => {
  const browser = await chromium.launch({channel:'msedge',headless:true});
  let count=0;
  const check = (condition, message) => { assert.ok(condition,message); count++; console.log('PASS '+message); };
  try {
    const context=await browser.newContext({viewport:{width:940,height:780}});
    const failures=[];context.on('page',p=>p.on('pageerror',e=>failures.push(e.message)));
    let page = await context.newPage();
    await page.context().route('**/*', async route=>{
      const name=new URL(route.request().url()).pathname.slice(1);
      if (!['index.html','app.js','app.css','ui.js','settings.js','setup.js','audio.js','low-time.js','layout.js','themes.js','themes.css','compact.html','compact.js','compact.css'].includes(name)) return route.abort();
      await route.fulfill({body:await fs.readFile(path.join(web,name)),contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'});
    });
    await page.context().addInitScript(() => {
      window.previewMessages=[];
      const listeners=[];
      window.previewDispatch = data => listeners.forEach(fn=>fn({data}));
      window.chrome ||= {};
      window.chrome.webview={addEventListener:(name,fn)=>listeners.push(fn),postMessage:message=>{
        window.previewMessages.push(message);
        queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId}));
      }};
    });
    const initial={clock:{seconds:900,text:'15 minutes',status:'Ready'},timer:{durationSeconds:900,autoRestart:false,enabled:true,threshold:15},prompts:[],
      schedules:[{id:'s1',start:'09/17/2026 10:00 AM',editStart:'2026-09-17T10:00',durationSeconds:900,volume:50,duration:'15 minutes',repeat:'Off',lowTime:'On',status:'Scheduled'}],
      outbox:[{id:'o1',saved:'09/10/2026 10:00 AM',destination:'Local preview only',status:'Pending',attempts:0,message:'A sample reflection.',duration:'15 minutes'}]};
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(state=>window.previewDispatch({type:'init',state}),initial);
    async function capture(name,target=page){if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});await target.locator('body').screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,name+'.png')});}}
    async function emptyDurationFields(target,view){
      await target.locator('#hours').fill('');
      check(await target.locator('#visual-clock').textContent()==='15:00',view+' treats an empty Hours field as zero');
      await target.keyboard.press('Tab');
      check(await target.locator('#hours').inputValue()==='0',view+' restores zero when leaving an empty duration field');
      await target.locator('#minutes').fill('');
      check(await target.locator('#visual-clock').textContent()==='0:00',view+' shows zero time while all duration units are empty or zero');
      const starts=await target.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length);
      await target.locator('#toggle').click();
      await target.waitForFunction(()=>document.querySelector('#error').textContent.includes('one second'));
      check(await target.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length)===starts,view+' cannot start a zero-length timer');
      await target.locator('#seconds').fill('25');
      await target.evaluate(()=>window.previewDispatch({type:'durationDraft',parts:['','', '25']}));
      check(await target.locator('#visual-clock').textContent()==='0:25',view+' renders a shared duration draft with empty units');
      if(view==='Compact'){
        await target.locator('#shrink').click();
        check(await target.locator('#visual-clock').textContent()==='0:25'&&await target.locator('#minutes').isHidden(),'Time-only retains a numeric time for an empty-unit draft');
        await target.locator('#expand').click();
      }
      await target.locator('#toggle').click();
      await target.waitForFunction(()=>window.previewMessages.some(m=>m.action==='toggle'&&m.data.seconds===25));
      check(true,view+' starts a positive duration with omitted units treated as zero');
      await target.evaluate(state=>{window.previewDispatch({type:'durationDraft',parts:null});window.previewDispatch({type:'state',state});},initial);
    }
    async function completedDuration(target,view){
      const running={...initial,clock:{seconds:1,text:'1 second',status:'Running'},timer:{...initial.timer,endTime:123456789}};
      const finished={...initial,clock:{seconds:900,text:'15 minutes',status:'Finished'}};
      await target.evaluate(state=>{window.previewDispatch({type:'durationDraft',parts:null});window.previewDispatch({type:'state',state});},running);
      await target.evaluate(()=>window.previewDispatch({type:'clock',clock:{seconds:0,text:'0 seconds',status:'Running'}}));
      check(await target.locator('#visual-clock').textContent()==='0:00',view+' can show zero at the end of the countdown');
      await target.evaluate(state=>window.previewDispatch({type:'state',state}),finished);
      await target.evaluate(clock=>window.previewDispatch({type:'clock',clock}),finished.clock);
      check(await target.locator('#visual-clock').textContent()==='15:00'&&await target.locator('#minutes').inputValue()==='15'&&await target.locator('#toggle').getAttribute('aria-label')!=='Resume timer',view+' returns to the input duration after completion and later ticks');
      check(await target.locator('#time-snapshot').textContent()==='Time checked: 15 minutes set. Finished.',view+' accessible snapshot describes the next duration as set after completion');
      const zeroFinished={...finished,clock:{seconds:0,text:'0 seconds',status:'Finished'}};
      await target.evaluate(state=>window.previewDispatch({type:'state',state}),zeroFinished);
      check(await target.locator('#visual-clock').textContent()==='15:00',view+' derives a finished display from its input boxes even when a state snapshot contains zero remaining time');
      for(let tick=0;tick<3;tick++)await target.evaluate(clock=>window.previewDispatch({type:'clock',clock}),zeroFinished.clock);
      check(await target.locator('#visual-clock').textContent()==='15:00',view+' repeated zero-valued finished ticks cannot overwrite the entered duration');
      await target.evaluate(()=>window.previewDispatch({type:'clock',clock:{seconds:0,text:'0 seconds',status:'Running'}}));
      check(await target.locator('#visual-clock').textContent()==='15:00',view+' ignores a late running countdown frame after the finished state');
      await target.evaluate(()=>window.previewDispatch({type:'durationDraft',parts:null}));
      check(await target.locator('#visual-clock').textContent()==='15:00',view+' clearing a shared edit after completion also restores the input duration');
      await target.evaluate(clock=>window.previewDispatch({type:'timeRead',clock}),zeroFinished.clock);
      check(await target.locator('#time-snapshot').textContent()==='Time checked: 15 minutes set. Finished.',view+' explicit time feedback matches the finished input preview even when remaining time is zero');
      await target.waitForFunction(()=>document.querySelector('#status').textContent.includes('set. Finished.'));
      await target.evaluate(()=>window.previewDispatch({type:'announcement',message:''}));
      await target.waitForFunction(()=>document.querySelector('#status').textContent==='');
      if(view==='Compact'){
        await target.locator('#shrink').click();
        check(await target.locator('#visual-clock').textContent()==='15:00'&&await target.locator('#minutes').isHidden(),'Time-only also shows the input duration after completion');
        await target.locator('#expand').click();
      }
      await target.locator('#minutes').fill('12');
      await target.evaluate(clock=>window.previewDispatch({type:'clock',clock}),finished.clock);
      check(await target.locator('#visual-clock').textContent()==='12:00',view+' keeps a newly entered duration after completion');
      await target.locator('#minutes').fill('0');
      await target.evaluate(clock=>window.previewDispatch({type:'clock',clock}),finished.clock);
      check(await target.locator('#visual-clock').textContent()==='0:00'&&await target.locator('#hours').inputValue()==='0'&&await target.locator('#seconds').inputValue()==='0',view+' stays at zero after completion when every input is zero');
      await target.evaluate(state=>{window.previewDispatch({type:'durationDraft',parts:null});window.previewDispatch({type:'state',state});},initial);
    }
    async function themes(target,state,view){
      const palettes=[
        ['Dark','rgb(22, 26, 34)','rgb(35, 42, 54)','rgb(239, 244, 250)'],
        ['Light','rgb(243, 246, 250)','rgb(255, 255, 255)','rgb(24, 37, 55)'],
        ['High Contrast','rgb(0, 0, 0)','rgb(0, 0, 0)','rgb(255, 255, 255)'],
        ['Glamour','rgb(255, 241, 247)','rgb(234, 220, 245)','rgb(74, 25, 52)']
      ];
      const field=target.locator(view==='Session-end'?'#reflection-text':'#minutes');
      await field.focus();const draft=await field.inputValue();
      for(const [theme,[name,background,surface,ink]] of palettes.entries()){
        await target.evaluate(state=>window.previewDispatch({type:'state',state}),{...state,theme});
        check(await target.evaluate(({background,surface,ink,field})=>{
          const input=document.querySelector(field);
          return getComputedStyle(document.documentElement).backgroundColor===background&&getComputedStyle(input).backgroundColor===surface&&getComputedStyle(input).color===ink;
        },{background,surface,ink,field:view==='Session-end'?'#reflection-text':'#minutes'}),view+' uses the original '+name+' palette');
        check(await field.inputValue()===draft&&await field.evaluate(e=>document.activeElement===e),view+' theme switch retains the focused field and its draft: '+name);
        if(view==='Compact')check(await target.locator('#app').evaluate(e=>e.dataset.appVisible==='true'&&getComputedStyle(e).backgroundColor===getComputedStyle(document.querySelector('#toggle')).backgroundColor),'App visibility highlight follows the '+name+' palette');
        if(view==='App'){
          await target.getByRole('tab',{name:'Settings',exact:true}).click();
          check(await target.getByRole('img',{name:new RegExp('^'+name+' theme preview')}).count()===1&&await target.locator('#theme-preview').locator('button,input,select,textarea,[tabindex]').count()===0,'Settings exposes a descriptive, non-interactive '+name+' theme sample');
          await target.locator('#theme-preview').scrollIntoViewIfNeeded();await capture('Theme-'+name+'-Settings',target);
          await target.getByRole('tab',{name:'Timer',exact:true}).click();await field.focus();
          if(theme===3){
            check(await target.locator('#page-title').evaluate(e=>getComputedStyle(e).fontStyle==='italic')&&await target.locator('#page-title .theme-ornament').isVisible(),'Glamour restores the italic heading and decorative bow');
          }
        }
        await capture('Theme-'+name+'-'+view,target);
      }
      await target.emulateMedia({forcedColors:'active'});
      check(await target.locator('.theme-ornament').evaluateAll(elements=>elements.every(e=>getComputedStyle(e).display==='none'))&&await target.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--selection').trim()==='Highlight'),view+' honors Windows contrast and hides decorative ornaments');
      if(view==='Compact'){
        await target.evaluate(state=>window.previewDispatch({type:'state',state}),{...state,timer:{...state.timer,autoRestart:true}});
        check(await target.locator('#repeat').evaluate(e=>e.getAttribute('aria-pressed')==='true'&&getComputedStyle(e).borderStyle==='double'),'Auto-start remains visibly distinct in Windows contrast mode');
        check(await target.locator('#app').evaluate(e=>e.dataset.appVisible==='true'&&getComputedStyle(e).borderStyle==='double'),'App visibility remains distinct in Windows contrast mode');
        check(await target.locator('.transport svg').evaluateAll(icons=>icons.every(e=>getComputedStyle(e).stroke===getComputedStyle(e.closest('button')).color)),'Compact arrow icons preserve their button text colors in Windows contrast mode');
        await capture('Windows-contrast-Compact-auto-start-on',target);
      }
      await target.emulateMedia({forcedColors:'none'});
      await target.evaluate(state=>window.previewDispatch({type:'state',state}),{...state,theme:0});
    }
    await capture('App-view');
    check(await page.locator('#quick-schedule-form').evaluate(row=>{const [label,input,button]=[row.querySelector('label'),row.querySelector('input'),row.querySelector('button')].map(e=>e.getBoundingClientRect());return label.right<=input.left&&input.right<=button.left&&Math.abs((label.top+label.bottom-input.top-input.bottom)/2)<2&&Math.abs((button.top+button.bottom-input.top-input.bottom)/2)<2;}),'Start timer at keeps its label, input, and button on one centered horizontal row');
    for(const name of ['Timer','Scheduler','Outbox','Settings','Diagnostics']){await page.getByRole('tab',{name,exact:true}).click();check(await page.getByRole('button',{name:'Save settings',exact:true}).isVisible()===(name==='Settings'),'Save settings visibility matches original on '+name);}
    await page.getByRole('tab',{name:'Timer',exact:true}).click();
    check(await page.getByRole('tab').allTextContents().then(t=>JSON.stringify(t)===JSON.stringify(['Timer','Scheduler','Outbox','Settings','Diagnostics'])),'App exposes the five tabs with the renamed Scheduler');
    await emptyDurationFields(page,'App');
    await completedDuration(page,'App');
    await page.locator('#minutes').fill('12');
    for(const [chord,names] of [['Control+Tab',['Scheduler','Outbox','Settings','Diagnostics','Timer']],['Control+Shift+Tab',['Diagnostics','Settings','Outbox','Scheduler','Timer']]]){
      for(const name of names){
        await page.keyboard.press(chord);
        const tab=page.getByRole('tab',{name,exact:true});
        check(await tab.getAttribute('aria-selected')==='true'&&await tab.evaluate(e=>e===document.activeElement),chord+' switches to '+name+' and focuses its accessible tab');
      }
    }
    check(await page.locator('#minutes').inputValue()==='12','Cycling App tabs retains an unfinished duration edit');
    await page.evaluate(()=>window.previewDispatch({type:'cycleAppTab',backward:true}));
    check(await page.getByRole('tab',{name:'Diagnostics',exact:true}).getAttribute('aria-selected')==='true','Native Ctrl+Shift+Tab forwarding wraps from Timer to Diagnostics');
    await page.evaluate(()=>window.previewDispatch({type:'cycleAppTab',backward:false}));
    check(await page.getByRole('tab',{name:'Timer',exact:true}).getAttribute('aria-selected')==='true','Native Ctrl+Tab forwarding wraps from Diagnostics to Timer');
    await page.locator('#minutes').focus();await page.keyboard.press('Tab');
    check(await page.getByRole('tab',{name:'Timer',exact:true}).getAttribute('aria-selected')==='true'&&await page.locator('#seconds').evaluate(e=>e===document.activeElement),'Ordinary Tab continues moving between input controls');
    await page.evaluate(state=>{window.previewDispatch({type:'durationDraft',parts:null});window.previewDispatch({type:'state',state});},initial);
    for(const width of [940,739,420,336]){
      await page.setViewportSize({width,height:642});
      for(const name of ['Timer','Scheduler','Outbox','Settings','Diagnostics']){
        await page.getByRole('tab',{name,exact:true}).click();
        check(await page.locator('nav[role=tablist]').evaluate(nav=>{
          const bounds=nav.getBoundingClientRect();
          return nav.scrollWidth<=nav.clientWidth&&[...nav.children].every(button=>{
            const box=button.getBoundingClientRect(),range=document.createRange();range.selectNodeContents(button);
            const text=range.getBoundingClientRect();
            return box.left>=0&&box.right<=innerWidth&&box.top>=bounds.top&&box.bottom<=bounds.bottom&&text.left>box.left&&text.right<box.right&&text.top>=box.top&&text.bottom<=box.bottom;
          });
        }),`All tab captions fit at ${width}px with ${name} selected`);
        check(await page.evaluate(()=>document.documentElement.scrollHeight<=innerHeight+1),`${name} at ${width}px keeps hidden reading text from creating a blank outer scroll area`);
      }
      await capture('Diagnostics-'+width);
    }
    await page.getByRole('tab',{name:'Timer',exact:true}).focus();await page.keyboard.press('End');
    check(await page.getByRole('tab',{name:'Diagnostics',exact:true}).evaluate(e=>e===document.activeElement&&e.getAttribute('aria-selected')==='true'),'End reaches and selects Diagnostics on the wrapped tab row');
    await page.keyboard.press('Home');
    check(await page.getByRole('tab',{name:'Timer',exact:true}).evaluate(e=>e===document.activeElement&&e.getAttribute('aria-selected')==='true'),'Home returns to Timer on the wrapped tab row');
    for(const [name,list] of [['Outbox','#outbox .table-scroll'],['Diagnostics','#diagnostic-recent']]){
      await page.getByRole('tab',{name,exact:true}).click();
      await page.setViewportSize({width:940,height:900});const before=(await page.locator(list).boundingBox()).height;
      await page.setViewportSize({width:940,height:1200});const after=(await page.locator(list).boundingBox()).height;
      check(after-before>=290,name+' list uses spare height when the window grows');
      check(await page.locator('.messages').evaluate(e=>e.getBoundingClientRect().height===0),name+' does not reserve an empty footer');
      await capture(name+'-tall');
    }
    await page.getByRole('tab',{name:'Settings',exact:true}).click();
    check(await page.locator('#save-settings').evaluate(e=>{const button=e.getBoundingClientRect(),main=document.querySelector('main').getBoundingClientRect();return button.top>=main.bottom&&button.bottom<=innerHeight;}),'Settings keeps Save settings below the scrolling content');
    await page.setViewportSize({width:940,height:780});
    await page.getByRole('tab',{name:'Timer',exact:true}).click();
    check(await page.getByRole('heading',{name:'Reflection Timer',exact:true}).count()===1,'Document exposes its main heading');
    check(await page.getByRole('spinbutton',{name:'Minutes',exact:true}).inputValue()==='15','Duration has a native label');
    await page.getByRole('spinbutton',{name:'Minutes',exact:true}).fill('12');
    await page.evaluate(()=>{
      window.clockMutations=[];window.focusEvents=[];
      document.addEventListener('focusin',e=>window.focusEvents.push(e.target.id));
      new MutationObserver(records=>window.clockMutations.push(...records.map(r=>r.target.parentElement?.id||r.target.id))).observe(document.body,{subtree:true,childList:true,characterData:true});
      for(let seconds=899;seconds>779;seconds--) window.previewDispatch({type:'clock',clock:{seconds,text:'Tick',status:'Running'}});
    });
    check(await page.locator('#minutes').evaluate(e=>document.activeElement===e)&&await page.locator('#minutes').inputValue()==='12','120 countdown ticks preserve input focus and draft');
    check(await page.locator('#visual-clock').textContent()==='12:00','Idle visual preview retains the edited duration during background clock messages');
    check(await page.locator('#status').textContent()===''&&await page.locator('#time-snapshot').textContent()==='Time checked: 15 minutes remaining. Ready.','Ticks do not update live speech or accessible time snapshot');
    check(await page.locator('#visual-clock').getAttribute('aria-hidden')==='true','Animated countdown is separate from the readable time snapshot');
    check((await page.evaluate(()=>window.focusEvents)).length===0,'Ticks generate no DOM focus changes');
    await page.getByRole('button',{name:'Read remaining time',exact:true}).focus();await page.keyboard.press('Enter');
    check(await page.evaluate(()=>window.previewMessages.at(-1).action)==='readTime','Remaining time is requested explicitly');
    await page.evaluate(()=>window.previewDispatch({type:'timeRead',clock:{seconds:780,text:'13 minutes',status:'Running'}}));
    await page.waitForFunction(()=>document.getElementById('status').textContent.includes('13 minutes'));
    check((await page.locator('#status').textContent())==='13 minutes remaining. Running.','Time request provides human-readable status');
    await page.getByRole('tab',{name:'Scheduler',exact:true}).click();
    check(await page.getByRole('table',{name:'Scheduled sessions',exact:true}).count()===1 && await page.getByRole('columnheader',{name:'Duration',exact:true}).count()===1,'Schedule has native table and header semantics');
    await page.getByRole('tab',{name:'Outbox',exact:true}).click();
    const success=page.getByRole('radio',{name:'Select entry saved 09/10/2026 10:00 AM'});
    await success.focus();
    await page.evaluate(()=>{ window.savedRow=document.querySelector('#outbox-rows tr');window.savedCell=window.savedRow.cells[2];window.savedButton=document.activeElement; });
    const changed=structuredClone(initial);changed.outbox[0].status='Simulated success';
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),changed);
    check(await page.evaluate(()=>window.savedRow===document.querySelector('#outbox-rows tr')&&window.savedCell===window.savedRow.cells[2]&&window.savedButton===document.activeElement),'Status update preserves row, cell, and focused action identity');
    check((await page.locator('#outbox-rows').textContent()).includes('Simulated success'),'Changed table cell is updated');
    check(await page.locator('#outbox-detail').textContent().then(text=>text.includes('A sample reflection.')),'Selected Outbox text is readable on the page');
    const autoSentState=structuredClone(changed);Object.assign(autoSentState.outbox[0],{autoSent:true,endedEarly:true,earlyEndReason:'Appointment'});
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),autoSentState);
    check((await page.locator('#outbox-rows').textContent()).includes('auto-sent')&&await page.locator('#outbox-detail').textContent().then(text=>text.includes('auto-sent')&&text.includes('ended early')&&text.includes('Appointment')),'Outbox exposes auto-sent alongside early-end status, reason, and delivery status');
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),changed);
    check(await page.locator('#outbox .actions button').allTextContents().then(labels=>JSON.stringify(labels)===JSON.stringify(['Send pending now','Retry selected…','Already in Sheet','Open Google Sheet'])),'Outbox actions match the original shared button row');
    check(await page.locator('#outbox-rows button').count()===0&&await page.locator('#outbox thead th').count()===4,'Outbox preserves its original columns without per-row action buttons');
    const settings={sheetUrl:'',webAppUrl:'',sheetMode:'date',sheetName:'Reflections',hasToken:false,connected:false,volume:50,threshold:15,
      showFloatingTimer:true,placement:4,popup:4,theme:0,overlap:2,loggingEnabled:true,
      tracks:[{id:0,name:'Default'},{id:3,name:'Level up'},{id:9,name:'None'}],
      sounds:[0,1,2,3].map(kind=>({kind,track:0,behavior:0,volume:100,fadeOutEnabled:false,fadeOutAfterSeconds:10,custom:false,defaultName:'Bundled default'}))};
    await page.getByRole('tab',{name:'Settings',exact:true}).click();
    await page.evaluate(settings=>window.previewDispatch({type:'settings',settings}),settings);
    await page.evaluate(()=>window.previewDispatch({type:'shortcuts',shortcuts:Array.from({length:7},(_,id)=>({id,available:id!==5}))}));
    check(await page.locator('#shortcut-notices kbd').allTextContents().then(keys=>keys.length===7&&keys[5]==='Ctrl+Space'&&keys[6]==='Ctrl+Alt+Space'),'Settings exposes both timer toggle shortcuts as styled, readable keys');
    check(await page.locator('#shortcut-notices p').nth(5).textContent().then(text=>text.includes('any app')&&text.includes('Unavailable:'))&&await page.locator('#shortcut-notices p').last().textContent().then(text=>!text.includes('Unavailable:')),'A Ctrl+Space registration conflict leaves its Ctrl+Alt+Space alias available');
    await page.evaluate(()=>window.previewDispatch({type:'shortcuts',shortcuts:Array.from({length:7},(_,id)=>({id,available:id!==6}))}));
    check(await page.locator('#shortcut-notices p').last().textContent().then(text=>text.includes('any app')&&text.includes('Unavailable:'))&&await page.locator('#shortcut-notices p').nth(5).textContent().then(text=>!text.includes('Unavailable:')),'A Ctrl+Alt+Space registration conflict leaves Ctrl+Space available');
    await page.evaluate(()=>window.previewDispatch({type:'shortcuts',shortcuts:Array.from({length:7},(_,id)=>({id,available:true}))}));
    check(await page.locator('#shortcut-notices').textContent().then(text=>!text.includes('Unavailable:')),'Recovered timer shortcuts clear their unavailable notices');
    check(await page.getByRole('combobox',{name:'Compact timer position',exact:true}).inputValue()==='4'&&await page.getByRole('checkbox',{name:'Show compact floating timer',exact:true}).isChecked(),'Display controls expose the intended defaults');
    for(const [id,name] of [['compactAlwaysOnTop','Compact view always on top'],['timeOnlyAlwaysOnTop','Time-only view always on top'],['promptAlwaysOnTop','Reflection prompts always on top']]){
      const option=page.getByRole('checkbox',{name,exact:true});check(await option.isChecked(),name+' defaults on');
      await option.uncheck();await page.waitForFunction(id=>window.previewMessages.some(m=>m.action==='displayOption'&&m.data.option===id&&m.data.value===0),id);
      check(await option.evaluate(e=>e===document.activeElement),name+' saves immediately and retains keyboard focus');
      await option.check();
    }
    await page.locator('#compactAlwaysOnTop').uncheck();
    await page.getByRole('button',{name:'Save settings',exact:true}).click();
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveAppearance'&&m.data.compactAlwaysOnTop===false&&m.data.timeOnlyAlwaysOnTop===true&&m.data.promptAlwaysOnTop===true));
    check(true,'Save settings keeps Compact, Time-only, and prompt layering independent');
    await page.evaluate(settings=>window.previewDispatch({type:'settings',settings}),settings);
    check(await page.locator('#audio fieldset legend').allTextContents().then(names=>JSON.stringify(names)===JSON.stringify(['Success messages','Failure messages','Low on time audio','Session end · time limit reached'])),'All four original audio event sections are present together');
    await page.evaluate(()=>{window.savedAudioOption=document.querySelector('#sound-track-0 option');});
    await page.evaluate(settings=>window.previewDispatch({type:'settings',settings}),settings);
    check(await page.evaluate(()=>window.savedAudioOption===document.querySelector('#sound-track-0 option')),'Background settings updates preserve audio option identity for the reader');
    check(await page.getByRole('checkbox',{name:'Start in the tray when I sign in to Windows',exact:true}).count()===1&&await page.getByRole('checkbox',{name:'Record local diagnostic events',exact:true}).count()===1,'Settings includes startup and diagnostic preferences');
    await page.getByRole('button',{name:'Guided setup / another PC',exact:true}).click();
    check(await page.getByRole('dialog',{name:'Your timer. Your spreadsheet.',exact:true}).isVisible()&&await page.getByRole('tab',{name:'1 · Your sheet',exact:true}).isVisible(),'Guided setup opens its own labeled three-step dialog');
    await page.getByRole('textbox',{name:'Google Sheets URL',exact:true}).fill('https://docs.google.com/spreadsheets/d/setup-draft/edit');
    await page.keyboard.press('Control+Tab');await page.keyboard.press('Control+Shift+Tab');
    await page.evaluate(()=>window.previewDispatch({type:'cycleAppTab',backward:false}));
    check(await page.getByRole('tab',{name:'Settings',exact:true}).getAttribute('aria-selected')==='true'&&await page.getByRole('dialog',{name:'Your timer. Your spreadsheet.',exact:true}).evaluate(e=>e.contains(document.activeElement)),'App tab shortcuts do not move behind an open dialog');
    await page.evaluate(()=>window.previewDispatch({type:'focusTimer'}));
    check(await page.getByRole('textbox',{name:'Google Sheets URL',exact:true}).evaluate(e=>document.activeElement===e),'Opening App does not move focus behind a setup dialog');
    await page.getByRole('button',{name:'Next',exact:true}).click();
    check(await page.getByRole('button',{name:'Save private receiver script',exact:true}).isVisible(),'Guided setup exposes receiver generation on its Google setup step');
    await page.getByRole('button',{name:'Next',exact:true}).click();
    check(await page.getByRole('button',{name:'Save & test connection',exact:true}).isVisible(),'Guided setup exposes connection verification on its Connect step');
    await page.getByRole('button',{name:'Close setup',exact:true}).click();
    check(await page.locator('#sheet-url').inputValue().then(value=>value.includes('setup-draft'))&&await page.locator('#sheet-url').evaluate(e=>e.form.id==='connection-form')&&await page.locator('#setup-import').evaluate(e=>e.form.id==='import-form'),'Closing setup preserves drafts and restores each form association');
    await page.getByRole('textbox',{name:'Google Sheets URL',exact:true}).fill('https://docs.google.com/spreadsheets/d/my-unsaved-draft/edit');
    const eventEditor=page.getByRole('group',{name:'Session end · time limit reached',exact:true});
    await eventEditor.getByRole('checkbox',{name:'SessionEnd fade out after',exact:true}).check();
    await eventEditor.getByRole('spinbutton',{name:'SessionEnd fade out after seconds',exact:true}).fill('37');
    await page.evaluate(settings=>window.previewDispatch({type:'settings',settings}),settings);
    check((await page.getByRole('textbox',{name:'Google Sheets URL',exact:true}).inputValue()).includes('my-unsaved-draft')&&await eventEditor.getByRole('spinbutton',{name:'SessionEnd fade out after seconds',exact:true}).inputValue()==='37','Background settings responses preserve unsaved connection and audio edits');
    await eventEditor.getByRole('spinbutton',{name:'SessionEnd fade out after seconds',exact:true}).press('Tab');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveSound'));
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='saveSound').data.fadeSeconds)===37,'Audio form sends the entered fade duration');
    await page.getByRole('spinbutton',{name:'Default low-time threshold in seconds',exact:true}).fill('18');
    await page.keyboard.press('Control+Enter');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveAppearance'));
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='saveAppearance').data.threshold)===18,'Original Save settings keyboard shortcut saves entered preferences');
    check(await page.getByRole('slider',{name:'Settings app sound volume',exact:true}).isVisible(),'Audio includes the original master App sound slider in Settings');
    await page.getByRole('slider',{name:'Settings app sound volume',exact:true}).press('Home');
    await page.getByRole('slider',{name:'Settings app sound volume',exact:true}).press('ArrowRight');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='volume'&&m.data.volume===1));
    check(await page.locator('#app-volume').inputValue()==='1'&&await page.locator('#settings-volume').inputValue()==='1','Settings and Timer App sound sliders share the same value');
    for(const [name,key] of [['Success messages','Success'],['Failure messages','Failure'],['Low on time audio','LowTime'],['Session end · time limit reached','SessionEnd']]){
      const group=page.getByRole('group',{name,exact:true});
      check(await group.getByRole('combobox',{name:key+' playback behavior',exact:true}).locator('option').allTextContents().then(labels=>JSON.stringify(labels)===JSON.stringify(['Disruptive','Assertive','Polite']))&&await group.getByRole('slider',{name:key+' audio volume',exact:true}).count()===1&&await group.getByRole('button',{name:'Preview audio',exact:true}).count()===1&&await group.getByRole('button',{name:'Choose MP3…',exact:true}).count()===1,'Complete original audio controls for '+name);
    }
    const successAudio=page.getByRole('group',{name:'Success messages',exact:true});
    await successAudio.getByRole('slider',{name:'Success audio volume',exact:true}).press('Home');
    await successAudio.getByRole('slider',{name:'Success audio volume',exact:true}).press('ArrowRight');
    await successAudio.getByRole('combobox',{name:'Success playback behavior',exact:true}).selectOption('2');
    await successAudio.getByRole('checkbox',{name:'Success fade out after',exact:true}).check();
    check(await successAudio.getByRole('spinbutton',{name:'Success fade out after seconds',exact:true}).isEnabled(),'Fade duration is enabled only when its checkbox is selected');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveSound'&&m.data.kind===1&&m.data.behavior===2&&m.data.volume===1&&m.data.fade));
    check(true,'Playback, per-event volume, and fade choices autosave together to the correct event');
    await page.getByRole('button',{name:'Stop all app audio',exact:true}).click();
    check(await page.evaluate(()=>window.previewMessages.at(-1).action)==='stopSound','Shared Stop all app audio button reaches the audio backend');
    await page.getByRole('tab',{name:'Timer',exact:true}).click();
    const savesBefore=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='saveAppearance').length);
    await page.keyboard.press('Control+Enter');
    check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='saveAppearance').length)===savesBefore,'Save settings shortcut is inactive outside Settings');
    await page.locator('#timer-low-inherit').uncheck();
    await page.locator('#threshold').fill('27');
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),initial);
    check(await page.locator('#threshold').inputValue()==='27','Background timer state preserves an unfinished low-time threshold');
    await page.locator('#threshold').press('Tab');
    await page.locator('#timer-low-track').selectOption('3');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='lowTime'&&m.data.track===3));
    check(await page.evaluate(()=>{const options=window.previewMessages.findLast(m=>m.action==='lowTime').data;return !options.inherit&&options.threshold===27&&options.track===3;}),'Session low-time changes retain the explicit threshold and sound');
    await page.getByRole('tab',{name:'Scheduler',exact:true}).click();
    check(await page.locator('#schedules thead th').allTextContents().then(labels=>JSON.stringify(labels)===JSON.stringify(['Start time','Duration','Auto-start','Auto-start cutoff','Sound','Low on time','Status'])),'Scheduling columns match the original');
    check(await page.locator('#schedules>.actions button').allTextContents().then(labels=>JSON.stringify(labels)===JSON.stringify(['Edit selected','Remove selected','Import extension schedules…']))&&await page.locator('#schedule-rows button').count()===0,'Scheduling actions are below the table and act on the selected entry');
    await page.getByRole('button',{name:'Edit selected',exact:true}).click();
    check(await page.locator('#schedule-start').evaluate(e=>document.activeElement===e)&&await page.locator('#schedule-minutes').inputValue()==='15','Schedule editing moves focus to a populated labeled form');
    await page.locator('#schedule-hours').fill('1');await page.locator('#schedule-seconds').fill('9');
    await page.getByRole('button',{name:'Save changes',exact:true}).click();
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='schedule').data.id)==='s1','Editing a schedule retains its stable ID');
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='schedule').data.seconds)===4509,'Schedule preserves separate hours, minutes, and seconds');
    const connected=structuredClone(initial);connected.connected=true;connected.outbox[0].localOnly=false;connected.outbox[0].status='NeedsReview';connected.outbox[0].error='write_uncertain';
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),connected);
    await page.getByRole('tab',{name:'Outbox',exact:true}).click();
    await page.getByRole('button',{name:'Retry selected…',exact:true}).click();
    check(await page.getByRole('dialog',{name:'Review delivery',exact:true}).isVisible()&&!await page.evaluate(()=>window.previewMessages.some(m=>m.action==='retry')),'An uncertain write opens review before sending a retry');
    await page.keyboard.press('Escape');
    check(await page.getByRole('button',{name:'Retry selected…',exact:true}).evaluate(e=>document.activeElement===e),'Cancelling a delivery review returns focus');
    await page.getByRole('tab',{name:'Diagnostics',exact:true}).click();
    await page.evaluate(()=>window.previewDispatch({type:'diagnostics',report:{privacy:'Synthetic diagnostic test',events:[]}}));
    check(await page.locator('#diagnostic-summary').textContent().then(text=>text.includes('/1200 events'))&&await page.getByRole('button',{name:'Refresh',exact:true}).isVisible(),'Diagnostics restores its readable event summary, history, and Refresh action');
    await capture('Diagnostics');
    for(const [tab,name] of [['Scheduler','Scheduler'],['Outbox','Outbox'],['Settings','Settings']]){await page.getByRole('tab',{name:tab,exact:true}).click();await page.locator('main').evaluate(e=>e.scrollTop=0);await capture(name);}
    if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){for(const id of ['sound-form-1','sound-form-2','sound-form-3','sound-form-0','startup']){await page.locator('#'+id).screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,id+'.png')});}}
    await page.setViewportSize({width:420,height:750});
    check(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'Narrow view reflows without horizontal page overflow');
    await page.setViewportSize({width:940,height:780});
    await page.getByRole('combobox',{name:'Color theme',exact:true}).selectOption('3');
    check(await page.evaluate(()=>window.previewMessages.some(m=>m.action==='displayOption'&&m.data.option==='theme'&&m.data.value===3)),'Choosing a theme reaches the native persistence command');
    await page.getByRole('tab',{name:'Timer',exact:true}).click();
    await themes(page,connected,'App');
    await page.getByRole('tab',{name:'Settings',exact:true}).click();
    await page.evaluate(()=>window.previewDispatch({type:'shortcuts',shortcuts:[0,1,2,3,4,5,6].map(id=>({id,available:id!==2}))}));
    check(await page.locator('#shortcut-notices p').count()===7&&(await page.locator('#shortcut-notices p').nth(2).textContent()).includes('Unavailable'),'Settings identifies the particular unavailable global shortcut');
    await page.evaluate(()=>window.previewDispatch({type:'shortcuts',shortcuts:[0,1,2,3,4,5,6].map(id=>({id,available:true}))}));
    check(!(await page.locator('#shortcut-notices').textContent()).includes('Unavailable'),'Recovered shortcut availability updates in Settings');
    await page.evaluate(()=>window.previewDispatch({type:'focusTimer',selectTimer:true}));
    check(await page.getByRole('tab',{name:'Timer',exact:true}).getAttribute('aria-selected')==='true'&&await page.locator('#minutes').evaluate(e=>e===document.activeElement),'Double-period action selects Timer and focuses its duration');
    await page.goto('https://reflection-timer.invalid/compact.html');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(state=>window.previewDispatch({type:'init',state,appViewVisible:true}),initial);
    check(await page.getByRole('spinbutton').count()===3&&await page.getByRole('button').count()===9&&await page.getByRole('button',{name:'Auto-start next session',pressed:false,exact:true}).count()===1,'Compact exposes its original actions with an accessible Auto-start toggle button');
    const appButton=page.getByRole('button',{name:'App',exact:true});
    check(await appButton.getAttribute('data-app-visible')==='true'&&await appButton.getAttribute('aria-describedby')==='app-view-status'&&await page.locator('#app-view-status').textContent()==='App view is visible.','Compact receives current App visibility on opening, including an accessible description');
    await page.locator('#minutes').fill('12');
    await page.evaluate(()=>window.previewDispatch({type:'appViewVisibility',visible:false}));
    check(await appButton.getAttribute('data-app-visible')==='false'&&await page.locator('#app-view-status').textContent()==='App view is hidden or minimized.'&&await page.locator('#minutes').inputValue()==='12','Hiding or minimizing App view dims its button without losing the compact draft');
    await page.evaluate(()=>{window.previewDispatch({type:'appViewVisibility',visible:true});window.previewDispatch({type:'clock',clock:{seconds:840}});});
    check(await appButton.getAttribute('data-app-visible')==='true'&&await page.locator('#minutes').evaluate(e=>e===document.activeElement)&&await page.locator('#minutes').inputValue()==='12','App stays highlighted while the Compact input has focus and the clock updates');
    await capture('Compact-App-visible');
    await appButton.click();await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='main'));
    check(await appButton.getAttribute('data-app-visible')==='true'&&!await appButton.getAttribute('aria-pressed'),'The highlighted App button still brings App view forward rather than toggling it closed');
    await page.evaluate(()=>window.previewDispatch({type:'durationDraft',parts:null}));
    check(await page.locator('body').evaluate(body=>{
      const bounds=body.getBoundingClientRect(),fields=[...document.querySelectorAll('.duration input')],buttons=[...document.querySelector('.footer').children];
      return bounds.width<=228&&bounds.height<=200&&[...fields,...buttons].every(e=>{const r=e.getBoundingClientRect();return r.left>=bounds.left+3&&r.right<=bounds.right-3&&r.bottom<=bounds.bottom-3;})&&fields.every(e=>{const s=getComputedStyle(e);return parseFloat(s.lineHeight)+parseFloat(s.paddingTop)+parseFloat(s.paddingBottom)+2<=e.getBoundingClientRect().height;});
    }),'Narrow compact fields and footer fit without taller layout or clipped input text');
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),{...initial,clock:{seconds:31536000,text:'365 days',status:'Ready'},timer:{...initial.timer,durationSeconds:31536000}});
    check(await page.locator('#visual-clock').evaluate(e=>{const r=document.createRange();r.selectNodeContents(e);return r.getBoundingClientRect().width<=e.parentElement.clientWidth;}),'Compact still fits the longest supported countdown');
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),initial);
    const autoStart=page.getByRole('button',{name:'Auto-start next session',exact:true});
    const compactStarts=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length);
    await page.locator('#minutes').fill('12');await autoStart.focus();await page.keyboard.press('Enter');
    await page.waitForFunction(()=>document.querySelector('#repeat').getAttribute('aria-pressed')==='true'&&!document.querySelector('#repeat').hasAttribute('aria-disabled'));
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='repeat').data.enabled===true)&&await autoStart.evaluate(e=>e===document.activeElement)&&await page.locator('#minutes').inputValue()==='12','Enter on the Auto-start button enables it while retaining focus and the edited duration');
    await capture('Compact-auto-start-on');
    await page.keyboard.press('Enter');
    await page.waitForFunction(()=>document.querySelector('#repeat').getAttribute('aria-pressed')==='false'&&!document.querySelector('#repeat').hasAttribute('aria-disabled'));
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='repeat').data.enabled===false&&window.previewMessages.filter(m=>m.action==='toggle').length===0),'Enter turns Auto-start off without starting the timer');
    for(const enabled of [true,false]){
      await page.evaluate(state=>window.previewDispatch({type:'state',state}),{...initial,timer:{...initial.timer,autoRestart:enabled}});
      check(await autoStart.getAttribute('aria-pressed')===String(enabled),'Compact reflects Auto-start changes from another view: '+enabled);
    }
    await page.evaluate(()=>{window.compactNormalPost=window.chrome.webview.postMessage;window.chrome.webview.postMessage=message=>{if(message.action!=='repeat')return window.compactNormalPost(message);window.previewMessages.push(message);queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:'Auto-start could not be saved.'}));};});
    await autoStart.click();await page.waitForFunction(()=>document.querySelector('#error').textContent==='Auto-start could not be saved.');
    check(await autoStart.getAttribute('aria-pressed')==='false'&&await page.locator('#minutes').inputValue()==='12'&&await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length)===compactStarts,'Failed Auto-start save restores its previous state and keeps the timer draft');
    await page.evaluate(()=>window.chrome.webview.postMessage=window.compactNormalPost);
    await page.locator('#minutes').fill('15');
    await autoStart.focus();await page.keyboard.press('Enter');
    await page.waitForFunction(()=>document.querySelector('#repeat').getAttribute('aria-pressed')==='true'&&!document.querySelector('#repeat').hasAttribute('aria-disabled'));
    await emptyDurationFields(page,'Compact');
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='toggle').data.repeat===true),'Starting from Compact uses the Auto-start toggle state');
    await completedDuration(page,'Compact');
    await capture('Compact-view');
    await themes(page,initial,'Compact');
    const mode=page.getByRole('button',{name:'Shrink to time-only view',exact:true});await mode.click();
    check(await page.getByRole('spinbutton',{name:'Minutes',exact:true}).isHidden(),'Time-only and compact controls cannot appear together');
    await page.getByRole('button',{name:'Expand compact view',exact:true}).focus();await page.keyboard.press('Enter');
    check(await page.getByRole('spinbutton',{name:'Minutes',exact:true}).isVisible(),'Original compact controls can be restored with the keyboard');
    const running=structuredClone(initial);running.clock.status='Running';running.timer.endTime=123456789;
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),running);
    check(await page.getByRole('spinbutton',{name:'Minutes',exact:true}).isHidden()&&await page.locator('body').evaluate(e=>e.getBoundingClientRect().width<=100),'Starting automatically restores the original small time-only view');
    for(const [theme,color] of [[0,'rgb(239, 244, 250)'],[1,'rgb(24, 37, 55)'],[2,'rgb(255, 255, 255)'],[3,'rgb(74, 25, 52)']]){
      await page.evaluate(state=>window.previewDispatch({type:'state',state}),{...running,theme});
      check(await page.locator('#read-time').evaluate((e,color)=>getComputedStyle(e).color===color,color)&&await page.locator('body').getAttribute('data-tiny')==='true','Time-only keeps its small layout with theme '+theme);
      await capture('Theme-'+theme+'-Time-only');
    }
    await page.mouse.move(400,700);await page.locator('#read-time').focus();
    await capture('Time-only-view');
    const quietBefore=await page.locator('#time-snapshot').textContent();
    await page.evaluate(()=>{for(let seconds=899;seconds>779;seconds--)window.previewDispatch({type:'clock',clock:{seconds}});});
    check(await page.locator('#read-time').evaluate(e=>e===document.activeElement)&&await page.locator('#time-snapshot').textContent()===quietBefore&&await page.locator('#read-time').getAttribute('aria-label')==='Read remaining time','Focused time-only clock keeps a stable accessible name and quiet reading snapshot during ticks');
    const compactWindow=page,appWindow=await page.context().newPage();
    await appWindow.goto('https://reflection-timer.invalid/index.html?view=main');await appWindow.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));await appWindow.evaluate(state=>window.previewDispatch({type:'init',state}),initial);
    page=await page.context().newPage();
    await page.setViewportSize({width:544,height:401});
    await page.goto('https://reflection-timer.invalid/index.html?view=reflection');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    const reflection=structuredClone(initial);reflection.prompts=[{id:'p1',isCheckIn:false,endedEarly:false,draft:'',earlyEndReason:'',actual:'2 minutes',allotted:'15 minutes',completed:'Today'}];
    await page.evaluate(state=>window.previewDispatch({type:'init',state,promptId:'p1'}),reflection);
    await page.getByRole('textbox',{name:'Your reflection',exact:true}).fill('Unsaved final keystroke');
    await themes(page,reflection,'Session-end');
    await page.locator('#later').focus();
    await page.evaluate(()=>window.previewDispatch({type:'focusReflection'}));
    check(await page.locator('#reflection-text').evaluate(e=>e===document.activeElement)&&await page.locator('#reflection-text').inputValue()==='Unsaved final keystroke','Check-in shortcut refocuses the existing reflection without replacing its draft');
    await capture('Session-end-view');
    check(await page.getByRole('button',{name:'Save & send',exact:true}).evaluate(e=>e.getBoundingClientRect().bottom<=innerHeight),'Session-end actions fit the original window size');
    await compactWindow.getByRole('button',{name:'Expand compact view',exact:true}).focus();await compactWindow.keyboard.press('Enter');
    check(await compactWindow.getByRole('spinbutton',{name:'Minutes',exact:true}).isVisible()&&await appWindow.getByRole('tab',{name:'Timer',exact:true}).isVisible()&&await page.getByRole('textbox',{name:'Your reflection',exact:true}).inputValue()==='Unsaved final keystroke','Changing Compact/Time-only mode leaves the separate App and session-end pages intact');
    await page.evaluate(()=>window.previewDispatch({type:'flush'}));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='flushed'));
    const messages=await page.evaluate(()=>window.previewMessages);
    check(messages.findIndex(m=>m.action==='draft'&&m.data.text==='Unsaved final keystroke')<messages.findIndex(m=>m.action==='flushed'),'Closing flush saves the newest draft before acknowledging');
    await page.locator('#reflection-text').fill('Final text before automatic replacement');
    const autoQueues=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='queue').length);
    await page.evaluate(()=>window.previewDispatch({type:'flush',freeze:true}));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='draft'&&m.data.text==='Final text before automatic replacement'));
    check(await page.locator('#reflection-text').evaluate(e=>e.readOnly)&&await page.locator('#early-reason').evaluate(e=>e.readOnly)&&await page.locator('#later').getAttribute('aria-disabled')==='true','Auto-send freezes both text fields and actions while the newest draft is saved');
    await page.locator('#reflection-text').press('Control+Enter');
    check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='queue').length)===autoQueues,'Ctrl+Enter cannot duplicate a submission during automatic replacement');
    await page.evaluate(()=>window.previewDispatch({type:'resumeReflection'}));
    check(await page.locator('#reflection-text').isEditable()&&await page.locator('#reflection-text').inputValue()==='Final text before automatic replacement'&&await page.locator('#later').getAttribute('aria-disabled')==='false','An interrupted auto-send restores editing without changing text');
    const promptLayout=await page.context().newPage();
    for(const [kind,isCheckIn,endedEarly] of [['Session-end',false,false],['Check-in',true,false],['Early-end',false,true]]){
      // CSS viewports inside the existing 560px-wide native prompt at common display scales.
      for(const [scale,width,height] of [[100,544,isCheckIn||endedEarly?486:401],[125,432,isCheckIn||endedEarly?381:313],[150,357,isCheckIn||endedEarly?311:254]]){
        await promptLayout.setViewportSize({width,height});
        await promptLayout.goto('https://reflection-timer.invalid/index.html?view=reflection');
        await promptLayout.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
        const promptState={...initial,prompts:[{id:'layout',isCheckIn,endedEarly,showEarlyEndReason:isCheckIn||endedEarly,draft:'',earlyEndReason:'',actual:'12 minutes 34 seconds',allotted:'15 minutes',completed:'12/31/2026 11:59 PM'}]};
        await promptLayout.evaluate(state=>window.previewDispatch({type:'init',state,promptId:'layout'}),promptState);
        for(const theme of [0,1,2,3]){
          await promptLayout.evaluate(state=>window.previewDispatch({type:'state',state}),{...promptState,theme});
          check(await promptLayout.evaluate(()=>{
            const root=document.documentElement,main=document.querySelector('main');
            const controls=[...document.querySelectorAll('#reflection textarea,#reflection button')].filter(e=>e.getClientRects().length);
            return root.scrollHeight<=innerHeight+1&&root.scrollWidth<=innerWidth&&main.scrollHeight<=main.clientHeight+1&&main.scrollWidth<=main.clientWidth&&controls.every(e=>{const r=e.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight&&(!e.matches('textarea')||e.scrollHeight<=e.clientHeight);});
          }),`${kind} prompt fits without page or empty-field scrollbars at ${scale}% display scale, theme ${theme}`);
          for(const status of ['Draft not yet changed.','Saving draft…','Draft saved locally.']){
            check(await promptLayout.evaluate(status=>{
              const draft=document.querySelector('#draft-status'),timestamp=document.querySelector('#reflection-timestamp');
              draft.textContent=status;
              const left=draft.getBoundingClientRect(),right=timestamp.getBoundingClientRect(),row=draft.parentElement.getBoundingClientRect();
              return timestamp.parentElement===draft.parentElement&&Math.abs(left.top-right.top)<1&&Math.abs(right.right-row.right)<1
                &&left.right+11<=right.left&&row.left>=0&&row.right<=innerWidth&&row.bottom<=innerHeight
                &&[draft,timestamp].every(e=>e.scrollWidth<=e.clientWidth&&e.clientHeight<=parseFloat(getComputedStyle(e).lineHeight)+1);
            },status),`${kind} timestamp shares one line with "${status}" and aligns right at ${scale}%, theme ${theme}`);
          }
          if(theme===0||theme===3)await capture(`Prompt-${kind}-${scale}-theme-${theme}`,promptLayout);
        }
        await promptLayout.locator('#reflection-text').fill(('A long reflection stays inside the writing area.\n').repeat(50));
        check(await promptLayout.locator('#reflection-text').evaluate(e=>e.scrollHeight>e.clientHeight)&&await promptLayout.locator('main').evaluate(e=>e.scrollHeight<=e.clientHeight+1)&&await promptLayout.getByRole('button',{name:'Save & send',exact:true}).evaluate(e=>e.getBoundingClientRect().bottom<=innerHeight),`${kind} long text scrolls within its field and leaves the actions visible at ${scale}%`);
      }
    }
    await promptLayout.close();
    await require('./reflection-save.cjs')(context,initial,check);
    await require('./reflection-submit.cjs')(context,initial,check);
    await require('./reflection-dismiss.cjs')(context,initial,check);
    await require('./reflection-lifecycle.cjs')(context,initial,check);
    await require('./reflection-navigation.cjs')(context,initial,check,settings);
    await require('./reflection-separators.cjs')(context,initial,settings,check);
    await require('./settings-success.cjs')(context,initial,settings,check);
    await require('./settings-startup.cjs')(context,initial,settings,check);
    await require('./message-sent-fade.cjs')(context,initial,settings,check);
    await require('./timer-enter.cjs')(context,initial,check);
    await require('./compact-escape.cjs')(context,initial,check);
    await require('./app-escape.cjs')(context,initial,settings,check);
    await require('./select-announcements.cjs')(context,initial,settings,check);
    check(failures.length===0,'No browser JavaScript errors');
    console.log(`${count} browser checks passed.`);
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
