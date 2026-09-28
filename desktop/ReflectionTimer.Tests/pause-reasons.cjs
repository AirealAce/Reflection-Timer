// Synthetic bridge only: never touches the installed app, audio or real Sheets.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let passed=0;
  const check=(ok,label)=>{assert.ok(ok,label);passed++;console.log('PASS '+label);};
  try{
    const context=await browser.newContext(),errors=[];context.on('page',p=>p.on('pageerror',e=>errors.push(e.message)));
    await context.route('**/*',async route=>{const name=path.basename(new URL(route.request().url()).pathname);
      if(!/\.(html|css|js)$/.test(name))return route.abort();
      await route.fulfill({body:await fs.readFile(path.join(web,name)),contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'});
    });
    await context.addInitScript(()=>{
      const listeners=[];window.previewMessages=[];window.previewDispatch=data=>listeners.forEach(f=>f({data}));
      window.chrome={webview:{addEventListener:(_,f)=>listeners.push(f),postMessage:m=>{
        window.previewMessages.push(m);queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:m.requestId}));
      }}};
    });
    const base={clock:{seconds:50,text:'50 seconds',status:'Paused'},timer:{durationSeconds:60,enabled:true},prompts:[],schedules:[],outbox:[]};
    const prompt={id:'session-one',mode:0,draft:'Completed useful work.',earlyEndReason:'',actual:'10 seconds',allotted:'1 minute',completed:'9/28/2026 10:30 AM'};
    const pauses=Array.from({length:6},(_,i)=>({id:`pause-${i}`,reason:'',paused:`9/28/2026 10:${20+i} AM`,duration:`${i+1} minutes`}));
    async function open(options={}){
      const page=await context.newPage();await page.goto('https://reflection-timer.invalid/index.html?view=reflection');
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
      const data={...prompt,...options};await page.evaluate(({base,p})=>window.previewDispatch({type:'init',promptId:p.id,state:{...base,prompts:[p]}}),{base,p:data});return page;
    }
    const bounds=page=>page.evaluate(()=>Object.fromEntries(['reflection-text','early-reason','reflection-timestamp','later','skip-reflection'].map(id=>{
      const r=document.getElementById(id).getBoundingClientRect();return[id,{x:r.x,y:r.y,width:r.width,height:r.height}];})));
    for(const early of [false,true])for(const [width,height] of [[544,early?486:401],[432,early?381:313],[357,early?311:254]]){
      const page=await open({showEarlyEndReason:early});await page.setViewportSize({width,height});
      for(const theme of [0,1,2,3]){
        const p={...prompt,showEarlyEndReason:early};
        await page.evaluate(({base,p,theme})=>window.previewDispatch({type:'showReflection',promptId:p.id,state:{...base,theme,prompts:[p]}}),{base,p,theme});
        const before=await bounds(page);
        await page.evaluate(({base,p,theme,pauses})=>window.previewDispatch({type:'state',state:{...base,theme,prompts:[{...p,pauses}]}}),{base,p,theme,pauses});
        const after=await bounds(page);
        if(JSON.stringify(before)!==JSON.stringify(after))console.log({before,after});
        check(JSON.stringify(before)===JSON.stringify(after),`Original fields/actions keep exact bounds at ${width}×${height}, early=${early}, theme=${theme}`);
        check(await page.locator('main').evaluate(e=>e.scrollHeight>e.clientHeight&&e.scrollWidth===e.clientWidth),`Pause fields scroll vertically without horizontal overflow at ${width}×${height}, theme=${theme}`);
      }
      await page.close();
    }
    const page=await open({pauses});
    check(await page.getByRole('textbox',{name:'Reason for pause 1 (optional)',exact:true}).count()===1,'Each pause has a separately labelled optional textbox');
    check(await page.locator('#pause-reasons textarea').first().getAttribute('aria-describedby')===await page.locator('.pause-timestamp').first().getAttribute('id'),'Pause time is associated with its field for screen readers');
    const field=page.locator('#pause-reasons textarea').first();await field.fill('Phone call');await field.focus();
    await page.evaluate(({base,prompt,pauses})=>window.previewDispatch({type:'state',state:{...base,prompts:[{...prompt,pauses:[...pauses,{id:'new-pause',reason:'',paused:'9/28/2026 10:28 AM',duration:'Still paused'}]}]}}),{base,prompt,pauses});
    check(await field.inputValue()==='Phone call'&&await field.evaluate(e=>e===document.activeElement),'A new pause or stale snapshot preserves typing and focus in existing reason fields');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='draft'&&m.data.pauseReasons?.[0]?.reason==='Phone call'));
    await page.keyboard.press('Control+s');await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveForLater'));
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='saveForLater').data.pauseReasons[0].reason==='Phone call'),'Ctrl+S flushes all pause reasons with the reflection');await page.close();
    for(const key of ['Escape','Control+Enter','Alt+s']){
      const page=await open({draft:'',pauses:[{...pauses[0],reason:'Only pause notes'}]});
      await page.locator('#pause-reasons textarea').focus();await page.keyboard.press(key);
      const action=key==='Escape'?'saveForLater':'queue';await page.waitForFunction(action=>window.previewMessages.some(m=>m.action===action),action);
      check(await page.evaluate(({action})=>window.previewMessages.some(m=>m.action===action&&m.data.pauseReasons[0].reason==='Only pause notes')&&!window.previewMessages.some(m=>m.action==='skip'),{action}),`${key} retains a pause-only response instead of skipping it`);await page.close();
    }
    const empty=await open({draft:'',pauses});await empty.locator('#pause-reasons textarea').first().focus();await empty.keyboard.press('Control+Enter');
    await empty.waitForFunction(()=>window.previewMessages.some(m=>m.action==='skip'));check(true,'Completely empty fields still use Ctrl+Enter to Skip');await empty.close();
    const navigation=await open({pauses});await navigation.locator('#pause-reasons textarea').first().fill('Last keystroke before replacement');
    await navigation.evaluate(()=>window.previewDispatch({type:'flush',freeze:true}));
    await navigation.waitForFunction(()=>window.previewMessages.some(m=>m.action==='flushed'));
    check(await navigation.locator('#pause-reasons textarea').first().evaluate(e=>e.readOnly)&&await navigation.evaluate(()=>window.previewMessages.some(m=>m.action==='draft'&&m.data.pauseReasons[0].reason==='Last keystroke before replacement')),'Automatic replacement freezes pause fields and saves their latest contents before acknowledging');
    const other={...prompt,id:'other-session',pauses:[{...pauses[0],id:'other-pause',reason:'Different session'}]};
    await navigation.evaluate(({base,p})=>window.previewDispatch({type:'showReflection',promptId:p.id,state:{...base,prompts:[p]}}),{base,p:other});
    check(await navigation.locator('#pause-reasons textarea').count()===1&&await navigation.locator('#pause-reasons textarea').inputValue()==='Different session'&&await navigation.locator('#pause-reasons textarea').isEditable(),'Reusing the popup for another reflection replaces only its pause fields without leaking old reasons');
    await navigation.evaluate(({base,prompt,pauses})=>window.previewDispatch({type:'showReflection',promptId:prompt.id,state:{...base,prompts:[{...prompt,pauses:[{...pauses[0],reason:'Last keystroke before replacement'}]}]}}),{base,prompt,pauses});
    check(await navigation.locator('#pause-reasons textarea').inputValue()==='Last keystroke before replacement','Returning to a saved reflection restores its pause reason');await navigation.close();
    await require('./reflection-lifecycle.cjs')(context,base,check);
    const capture=await open({pauses});await capture.setViewportSize({width:544,height:401});
    await fs.mkdir(path.resolve(__dirname,'../../../../outputs'),{recursive:true});
    await capture.screenshot({path:path.resolve(__dirname,'../../../../outputs/pause-prompt-top.png')});
    await capture.locator('#pause-reasons textarea').first().scrollIntoViewIfNeeded();
    await capture.screenshot({path:path.resolve(__dirname,'../../../../outputs/pause-prompt-scrolled.png')});
    await capture.close();check(errors.length===0,'No browser errors with pause fields, updates or shortcuts');
    console.log(`${passed} pause UI checks passed.`);
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
