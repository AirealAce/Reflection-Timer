// Synthetic bridge only: no installed settings, audio, or Sheets requests.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let passed=0;
  const check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);};
  try{
    const page=await browser.newPage({viewport:{width:739,height:642}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',async route=>{
      const name=path.basename(new URL(route.request().url()).pathname);
      if(!/\.(html|js|css)$/.test(name))return route.abort();
      await route.fulfill({body:await fs.readFile(path.join(web,name)),contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'});
    });
    await page.addInitScript(()=>{
      const handlers=[];window.messages=[];window.failHelp=false;
      window.settings={sheetUrl:'',webAppUrl:'',sheetMode:'date',sheetName:'',connected:false,volume:0,threshold:15,showFloatingTimer:true,
        theme:0,popup:4,placement:4,overlap:2,tracks:[{id:0,name:'Default'},{id:6,name:'Battle (Trainer)'}],
        sounds:[0,1,2,3,4,5].map(kind=>({kind,track:0,behavior:2,volume:100,fadeOutAfterSeconds:10,defaultName:'Bundled track'}))};
      if(sessionStorage.getItem('synthetic-help')!==null)window.settings.showAllExplanations=JSON.parse(sessionStorage.getItem('synthetic-help'));
      window.state={clock:{seconds:900,text:'15 minutes',status:'Ready'},timer:{durationSeconds:900,mode:0,autoRestart:false,enabled:true,threshold:15},prompts:[],schedules:[],outbox:[]};
      window.dispatchBridge=m=>handlers.forEach(h=>h({data:m}));
      window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
        window.messages.push(m);queueMicrotask(()=>{
          if(window.failHelp&&m.action==='displayOption'&&m.data.option==='showAllExplanations'){window.dispatchBridge({type:'reply',requestId:m.requestId,error:'Synthetic help save failure'});return;}
          if(m.action==='displayOption'&&m.data.option==='showAllExplanations'||m.action==='saveAppearance'){
            window.settings.showAllExplanations=m.action==='displayOption'?m.data.value===1:m.data.showAllExplanations;
            sessionStorage.setItem('synthetic-help',JSON.stringify(window.settings.showAllExplanations));
            window.dispatchBridge({type:'settings',settings:window.settings});
          }
          if(m.action==='settingsLoad')window.dispatchBridge({type:'settings',settings:window.settings});
          if(m.action==='focusTargets')window.dispatchBridge({type:'focusTargets',kind:m.data.kind,targets:[]});
          window.dispatchBridge({type:'reply',requestId:m.requestId});
        });
      }}};
    });
    async function ready(){await page.waitForFunction(()=>window.messages.some(m=>m.action==='ready'));await page.evaluate(()=>window.dispatchBridge({type:'init',state:window.state}));await page.waitForFunction(()=>window.messages.some(m=>m.action==='interfaceReady'));}
    await page.goto('https://reflection-timer.invalid/index.html?view=main');await ready();
    async function capture(name){if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});await page.screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,name+'.png')});}}
    await capture('Help-default-Timer');
    check(await page.locator('[data-help]').evaluateAll(nodes=>nodes.length>30&&nodes.every(n=>n.hidden)),'All marked explanations default to collapsed, including generated audio guidance');
    check(await page.locator('.help-button').count()===21,'Every guidance section has one context-specific help button');
    check(await page.locator('.help-button').evaluateAll(buttons=>buttons.every(b=>b.type==='button'&&b.textContent==='?'&&b.getAttribute('aria-expanded')==='false'&&b.getAttribute('aria-label').startsWith('Help for ')&&b.getAttribute('aria-controls').split(' ').every(id=>document.getElementById(id)))),'Circled question marks expose names, controls and collapsed state without submitting forms');
    check(await page.locator('#minutes').getAttribute('aria-describedby')===null,'Collapsed duration guidance is removed from routine input descriptions');
    const cdp=await page.context().newCDPSession(page);
    const axButton=async()=>{const tree=await cdp.send('Accessibility.getFullAXTree');return tree.nodes.find(n=>!n.ignored&&n.role?.value==='button'&&n.name?.value==='Help for Timer and views');};
    check((await axButton()).properties.some(p=>p.name==='expanded'&&p.value.value===false),'Browser accessibility tree exposes the named help button as collapsed');
    await page.locator('#help-toggle-timer').focus();await page.keyboard.press('Enter');
    check(await page.locator('#duration-help').isVisible()&&await page.locator('#help-toggle-timer').getAttribute('aria-expanded')==='true'&&await page.locator('#minutes').getAttribute('aria-describedby')==='duration-help','Enter opens Timer guidance and restores its input description');
    check(await page.locator('#help-toggle-timer').evaluate(e=>document.activeElement===e),'Opening guidance keeps keyboard focus on its button');
    check((await axButton()).properties.some(p=>p.name==='expanded'&&p.value.value===true),'Browser accessibility tree exposes the expanded help state');
    await page.keyboard.press('Space');
    check(await page.locator('#duration-help').isHidden()&&await page.locator('#minutes').getAttribute('aria-describedby')===null,'Space closes guidance and removes its input description');
    await page.locator('#tab-settings').click();
    check(!await page.locator('#showAllExplanations').isChecked()&&await page.locator('#theme').isVisible()&&await page.locator('#confirmBeforeReset').isVisible()&&await page.locator('#sound-track-5').isVisible(),'Legacy settings keep guidance off while every control remains visible');
    check(await page.locator('#theme-notice').textContent()==='Dark theme.','Current theme stays visible without repeating its optional instructions');
    check(await page.locator('#connection-token').getAttribute('aria-describedby')==='token-help'&&await page.locator('#token-help').isVisible(),'Connection state and its essential token-field description remain available');
    check(await page.evaluate(()=>scrollY===0&&document.documentElement.scrollHeight<=innerHeight+1),'Opening and closing help keeps the App header visible without an outer scroll area');
    await capture('Help-default-Settings');
    await require('./settings-search.cjs')(page,check);
    for(const theme of [0,1,2,3]){
      await page.evaluate(theme=>window.dispatchBridge({type:'settings',settings:{...window.settings,theme}}),theme);
      for(const key of ['theme','display','reset','reflections','audio','voice','connection','startup','guidance']){
        const gap=await page.locator('#help-toggle-'+key).evaluate(button=>{
          const label=document.getElementById('help-label-'+button.id.slice('help-toggle-'.length)),range=document.createRange();range.selectNodeContents(label);
          return button.getBoundingClientRect().left-range.getBoundingClientRect().right;
        });
        check(gap>=6&&gap<=10,'The '+key+' help icon stays directly beside its text in theme '+theme);
      }
      check(await page.locator('#appearance-heading').evaluate(heading=>Math.abs(heading.getBoundingClientRect().width-heading.parentElement.getBoundingClientRect().width)<1),'The existing theme heading bar retains its full width in theme '+theme);
    }
    await page.evaluate(()=>window.dispatchBridge({type:'settings',settings:{...window.settings,theme:0}}));
    const headings=(await cdp.send('Accessibility.getFullAXTree')).nodes.filter(node=>!node.ignored&&node.role?.value==='heading');
    check(headings.some(node=>node.name?.value==='App theme')&&headings.some(node=>node.name?.value==='Voice announcements'),'Inline help icons retain the original accessible heading names');
    for(const width of [739,420,336]){
      await page.setViewportSize({width,height:642});
      for(const tab of ['timer','settings']){
        await page.locator('#tab-'+tab).click();
        check(await page.locator('.help-label>.help-button').evaluateAll(buttons=>buttons.filter(button=>button.getClientRects().length).every(button=>{
          const range=document.createRange();range.setStart(button.parentNode,0);range.setEndBefore(button);
          const text=[...range.getClientRects()].at(-1),icon=button.getBoundingClientRect();
          if(Math.abs((icon.top+icon.bottom-text.top-text.bottom)/2)<15)return icon.left-text.right>=6&&icon.left-text.right<=10;
          return icon.top>=text.bottom-2&&icon.top-text.bottom<=12&&icon.left-button.parentElement.getBoundingClientRect().left<=10;
        })),'Paragraph and audio-legend help icons follow their text in '+tab+' at '+width+'px');
      }
    }
    await page.setViewportSize({width:739,height:642});
    await page.locator('#settings-search-input').fill('focus');await page.waitForTimeout(220);await capture('Settings-search-Focus');
    await page.locator('#settings-search-clear').click();
    const voice=page.locator('#help-toggle-voice');await voice.focus();await page.keyboard.press('Space');
    check(await page.locator('#voice-help').isVisible()&&await page.locator('#voice-announcements').getAttribute('aria-describedby')==='voice-help voice-preset-help','A multi-paragraph section opens both explanations and related descriptions');
    await page.keyboard.press('Enter');
    check(await page.locator('#voice-help').isHidden()&&await page.locator('#voice-announcements').getAttribute('aria-describedby')===null,'Closing a section removes every optional description');
    for(const key of ['display','reset','reflections','audio','audio-low','audio-reached','focus','connection','startup']){
      const button=page.locator('#help-toggle-'+key);await button.click();
      check(await page.locator(`[data-help="${key}"]`).evaluateAll(nodes=>nodes.every(n=>!n.hidden)),key+' help expands its own guidance');
      await button.click();
    }
    await page.locator('#choose-focus-target-settings').click();
    check(await page.locator('#focus-picker-status').isVisible()&&await page.locator('#focus-picker-keys').isHidden(),'Chooser loading and selection status stay visible with keyboard guidance collapsed');
    check(await page.locator('#focus-target-dialog').getAttribute('aria-describedby')===null&&await page.locator('#focus-target-list table').getAttribute('aria-describedby')==='focus-picker-status','Closed chooser help preserves the operational table description');
    await page.locator('#help-toggle-focus-picker').focus();await page.keyboard.press('Enter');
    check(await page.locator('#focus-picker-keys').isVisible()&&await page.locator('#focus-target-dialog').getAttribute('aria-describedby')==='focus-picker-help','Chooser help works from the keyboard inside its modal dialog');
    await page.locator('#help-toggle-focus-picker').click();await page.locator('#focus-target-cancel').click();
    await page.locator('#showAllExplanations').check();await page.waitForFunction(()=>window.settings.showAllExplanations===true);
    check(await page.locator('[data-help]').evaluateAll(nodes=>nodes.every(n=>!n.hidden)),'Show all explanations opens all App and chooser guidance');
    check(await page.evaluate(()=>window.messages.filter(m=>m.action==='displayOption').at(-1).data.quiet),'Help visibility autosaves silently');
    await page.locator('#help-toggle-display').click();await page.evaluate(()=>window.dispatchBridge({type:'state',state:{...window.state,showAllExplanations:true}}));
    check(await page.locator('#viewer-hide-help').isHidden()&&await page.locator('#reset-confirmation-help').isVisible(),'Individual sections can close with Show all enabled and stay closed across timer updates');
    for(const theme of [0,1,2,3]){
      await page.evaluate(theme=>window.dispatchBridge({type:'settings',settings:{...window.settings,theme}}),theme);
      await page.keyboard.press('Tab');await page.locator('#help-toggle-theme').focus();
      check(await page.locator('#help-toggle-theme').evaluate(e=>{const css=getComputedStyle(e);return css.borderRadius==='50%'&&css.width===css.height&&parseFloat(css.width)>=24&&css.color===getComputedStyle(document.querySelector('#save-settings')).color;}),'Circled help icon follows theme '+theme+' with an adequate target size');
      check(await page.locator('#help-toggle-theme').evaluate(e=>getComputedStyle(e).outlineStyle==='solid'),'Theme '+theme+' shows a visible focus outline');
      if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});await page.screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,`Help-theme-${theme}.png`)});}
    }
    await page.locator('#showAllExplanations').uncheck();await page.waitForFunction(()=>window.settings.showAllExplanations===false);
    await page.evaluate(()=>window.dispatchBridge({type:'shortcuts',shortcuts:Array.from({length:12},(_,i)=>({available:i!==5}))}));
    check(await page.locator('#shortcut-availability').isVisible()&&await page.locator('#shortcut-availability').textContent().then(t=>t.includes('Ctrl+Space')),'Unavailable shortcuts remain visible in Settings when detailed instructions are in Help');
    await page.locator('a[href="#help-shortcuts"]').click();
    check(await page.locator('#tab-help').getAttribute('aria-selected')==='true'&&await page.locator('#help-shortcuts-heading').evaluate(e=>document.activeElement===e),'Settings shortcut link selects Help and focuses the right heading');
    check(await page.locator('#shortcut-notices kbd').count()===12&&await page.locator('#shortcut-notices kbd').last().textContent()==='Ctrl+Alt+]'&&await page.locator('#help-topics').textContent().then(t=>t.includes('Disruptive')&&t.includes('Fade')&&t.includes('Ctrl+S')&&t.includes('setupReflectionTimer')),'Help collects audio, reflection, receiver setup and all keyboard instructions including the Focus target shortcut');
    check(await page.locator('#help').locator('input,select,textarea').count()===0,'Help copies guidance without any editable controls or private data fields');
    check(await page.locator('[id]').evaluateAll(nodes=>new Set(nodes.map(n=>n.id)).size===nodes.length),'Guide copies keep all document IDs unique');
    await page.locator('#main').evaluate(e=>e.scrollTop=0);await capture('Help-page');
    await page.locator('#tab-outbox').click();check(await page.locator('#delivery-status').isVisible(),'Outbox delivery state stays visible while its explanation is closed');
    await page.evaluate(()=>window.dispatchBridge({type:'reply',requestId:'unknown',error:'Ignored reply'}));
    await page.locator('#tab-settings').click();await page.evaluate(()=>window.failHelp=true);await page.locator('#showAllExplanations').check();
    await page.waitForFunction(()=>document.querySelector('#error').textContent.includes('Synthetic help save failure'));
    check(await page.locator('#error').isVisible()&&await page.locator('#showAllExplanations').isChecked(),'Failed preference saves show the error and retain the choice for retry');
    await page.evaluate(()=>window.failHelp=false);await page.locator('#showAllExplanations').focus();await page.keyboard.press('Control+s');
    await page.waitForFunction(()=>window.settings.showAllExplanations===true&&document.querySelector('#status').textContent==='Settings saved.');
    check(await page.locator('#showAllExplanations').evaluate(e=>document.activeElement===e),'Ctrl+S retries help preference saving and retains focus');
    await page.reload();await ready();
    check(await page.locator('[data-help]').evaluateAll(nodes=>nodes.every(n=>!n.hidden)),'A saved Show all choice opens guidance after reload');
    await page.locator('#tab-settings').click();await page.locator('#showAllExplanations').uncheck();await page.waitForFunction(()=>window.settings.showAllExplanations===false);await page.reload();await ready();
    check(await page.locator('[data-help]').evaluateAll(nodes=>nodes.every(n=>n.hidden)),'A saved collapsed choice keeps guidance closed after reload');
    check(await page.evaluate(()=>window.messages.every(m=>!['toggle','reset','queue','previewSound','connectionSave'].includes(m.action))),'Help interactions do not start a session, play sound, or submit a reflection or connection');
    check(errors.length===0,'No web-interface exceptions during guidance interactions: '+errors.join('; '));
    console.log(`${passed} help checks passed.`);
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
