// Synthetic WebView bridge: no installed profile, sound device or Sheets access.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'),path=require('node:path'),assert=require('node:assert/strict');
const web=path.resolve(__dirname,'../ReflectionTimer.Desktop/Web');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let passed=0;
  const check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);};
  try{
    const page=await browser.newPage({viewport:{width:940,height:780}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',async route=>{const name=path.basename(new URL(route.request().url()).pathname);
      if(!/\.(html|js|css)$/.test(name))return route.abort();
      await route.fulfill({body:await fs.readFile(path.join(web,name)),contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'});
    });
    await page.addInitScript(()=>{
      const handlers=[];window.messages=[];window.dispatchBridge=m=>handlers.forEach(h=>h({data:m}));
      const names=['Original extension sound','Obtained an Item','Level Up','Pokémon Healed','Obtained a Key Item','Battle (Trainer)','Battle (Champion)','Out of Health'];
      const songs=['Pokemon Red Green Blue Yellow - 10. Battle! (Trainer Battle)','Pokemon Red Green Blue Yellow - 14 Battle! (Wild Pokémon)',
        'Pokemon Gold Silver Crystal - 17. Battle! (Wild Pokémon - Johto - Day)','Pokemon Gold Silver Crystal - 18. Battle! (Wild Pokémon - Johto - Night)',
        'Pokemon Red Green Blue Yellow - 28. Battle! (Gym Leader)','Pokemon Diamond Pearl Platinum - 200. Battle! (Regirock - Regice - Registeel)'];
      window.settings=JSON.parse(sessionStorage.getItem('random-settings')||'null')??{sheetUrl:'',webAppUrl:'',sheetMode:'date',sheetName:'',connected:false,
        volume:50,threshold:15,showFloatingTimer:true,theme:0,popup:4,placement:4,overlap:2,focusMode:{enabled:false,delaySeconds:5,targets:[],multipleTargets:false,idleEnabled:false,idleSeconds:20},
        tracks:[{id:10,name:'Random'},{id:0,name:'Default'},{id:9,name:'None'},...names.map((name,i)=>({id:i+1,name,song:[5,6].includes(i)})),...songs.map((name,i)=>({id:i+11,name,song:true}))],
        sounds:[0,1,2,3,4,5].map(kind=>({kind,track:[1,3,8,6,7,6][kind],behavior:0,volume:100,fadeOutEnabled:false,fadeOutAfterSeconds:10,defaultName:names[0]}))};
      window.state=JSON.parse(sessionStorage.getItem('random-state')||'null')??{clock:{seconds:900,text:'15 minutes',status:'Ready'},timer:{durationSeconds:900,mode:0,enabled:true,threshold:15,low:{enabled:true,inherit:true,threshold:15,track:0,custom:false}},prompts:[],schedules:[],outbox:[]};
      window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
        window.messages.push(m);queueMicrotask(()=>{
          if(m.action==='saveSound'){
            const sound=window.settings.sounds.find(s=>s.kind===m.data.kind);Object.assign(sound,{track:m.data.track,randomTracks:m.data.randomTracks,volume:m.data.volume,behavior:m.data.behavior,fadeOutEnabled:m.data.fade,fadeOutAfterSeconds:m.data.fadeSeconds});
            sessionStorage.setItem('random-settings',JSON.stringify(window.settings));window.dispatchBridge({type:'settings',settings:window.settings});
          }
          if(m.action==='lowTime'){window.state.timer.low={...m.data,custom:m.data.keepCustom};sessionStorage.setItem('random-state',JSON.stringify(window.state));window.dispatchBridge({type:'state',state:window.state});}
          window.dispatchBridge({type:'reply',requestId:m.requestId,...(m.action==='focusTargets'?{targets:[]}:{} )});
        });
      }}};
    });
    async function load(){await page.goto('https://reflection-timer.invalid/index.html?view=main');await page.waitForFunction(()=>window.messages.some(m=>m.action==='ready'));await page.evaluate(()=>{window.dispatchBridge({type:'init',state:window.state});window.dispatchBridge({type:'settings',settings:window.settings});});}
    await load();await page.locator('#tab-settings').click();
    for(const id of ['sound-track-0','sound-track-1','sound-track-2','sound-track-3','sound-track-4','sound-track-5','sound-track-5-picker','timer-low-track','schedule-low-track']){
      check(await page.locator('#'+id+' option').first().textContent()==='Random',`Random is first in ${id}`);
      check(await page.locator('#'+id+' option').evaluateAll(options=>[11,12,13,14,15,16].every(id=>options.some(o=>o.value===String(id)))),`All six added songs appear in ${id}`);
    }
    check(await page.locator('#sound-track-1').inputValue()==='3'&&await page.locator('#sound-track-3').inputValue()==='6','Adding Random does not replace existing selected sounds');
    for(const kind of [0,1,2,3,4,5]){
      await page.locator('#sound-track-'+kind).selectOption('10');
      await page.waitForFunction(kind=>window.messages.some(m=>m.action==='saveSound'&&m.data.kind===kind&&m.data.track===10),kind);
      const details=page.locator(`#sound-form-${kind}-random`);
      check(await details.isVisible()&&!await details.evaluate(e=>e.open),`Random probabilities appear collapsed for event ${kind}`);
      await details.locator('summary').click();
      check(await details.locator('input[type=checkbox]:checked').count()===([3,4,5].includes(kind)?8:6),`Event ${kind} starts with only its intended song/notification pool checked`);
      check(await details.locator('tbody tr').evaluateAll((rows,songs)=>rows.every(row=>{const checkbox=row.querySelector('input[type=checkbox]'),weight=row.querySelector('input[type=number]'),song=[6,7].includes(Number(checkbox.id.split('-').at(-2)))||Number(checkbox.id.split('-').at(-2))>=11;return checkbox.checked===(song===songs)&&weight.value==='1'&&weight.disabled===!checkbox.checked;}),[3,4,5].includes(kind)),`Event ${kind} has equal weights and editable enabled tracks`);
      check(await details.locator('input[type=checkbox]:checked').first().evaluate(e=>document.getElementById(e.getAttribute('aria-describedby')).textContent)===(kind<3?'16.67%':'12.5%'),`Event ${kind} exposes the normalized percentage to screen readers`);
    }
    const successWeight=page.locator('#sound-form-1-random-3-weight');await successWeight.fill('4');
    await page.keyboard.press('Control+s');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='saveSound'&&m.data.kind===1&&m.data.randomTracks.find(t=>t.track===3).weight===4));
    check(await page.locator('#sound-form-1-random-3-chance').textContent()==='44.44%'&&await successWeight.evaluate(e=>e===document.activeElement),'Ctrl+S persists the unblurred weight and keeps focus; percentages update immediately');
    await successWeight.fill('5');await page.keyboard.press('Control+Enter');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='saveSound'&&m.data.kind===1&&m.data.randomTracks.find(t=>t.track===3).weight===5));
    check(await successWeight.evaluate(e=>e===document.activeElement),'Ctrl+Enter also saves probabilities from the focused number field');
    await page.locator('#sound-form-1-random-11-enabled').check();
    check(await page.locator('#sound-form-1-random-11-weight').isEnabled(),'A song excluded by default can be included for Success messages');
    await page.locator('#sound-track-1').selectOption('3');check(await page.locator('#sound-form-1-random').isHidden(),'Selecting a fixed track hides the extra probability controls');
    await page.locator('#sound-track-1').selectOption('10');
    check(await successWeight.inputValue()==='5'&&await page.locator('#sound-form-1-random-11-enabled').isChecked(),'Switching away from Random and back retains the edited pool');
    await page.locator('#tab-timer').click();await page.locator('#timer-low-track').selectOption('10');await page.locator('#timer-low-random summary').click();
    await page.locator('#timer-low-random-11-weight').fill('3');await page.locator('#timer-low-random-12-weight').focus();
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='lowTime'&&m.data.track===10&&m.data.randomTracks.find(t=>t.track===11).weight===3));
    check(await page.locator('#timer-low-random-11-chance').textContent()==='30%','Timer low-time Random has its own editable pool and normalized chances');
    await page.locator('#tab-schedules').click();await page.locator('#schedule-low-track').selectOption('10');await page.locator('#schedule-low-random summary').click();
    await page.locator('#schedule-low-random-12-weight').fill('4');await page.locator('#schedule-start').fill('2027-01-01T10:00');await page.locator('#schedule-save').click();
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='schedule'));
    check(await page.evaluate(()=>window.messages.findLast(m=>m.action==='schedule').data.lowOptions.randomTracks.find(t=>t.track===12).weight===4),'Saving a scheduled session sends its separate Random probabilities');
    await page.locator('#tab-timer').click();await page.locator('#choose-focus-target').click();await page.locator('#focus-picker-audio summary').first().click();
    await page.locator('#sound-form-5-picker-random summary').click();await page.locator('#sound-form-5-picker-random-6-weight').fill('7');await page.locator('#preview-sound-5-picker').click();
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='saveSound'&&m.data.kind===5&&m.data.randomTracks.find(t=>t.track===6).weight===7));
    check(await page.locator('#sound-form-5-random-6-weight').inputValue()==='7','Focus chooser and Settings share one probability editor and save queue');
    check(await page.evaluate(()=>window.messages.findLast(m=>m.action==='previewSound').data.kind===5),'Focus Random preview uses the selected event and its saved probabilities');
    await page.keyboard.press('Escape');await load();await page.locator('#tab-settings').click();
    check(await page.locator('#sound-track-1').inputValue()==='10'&&await successWeight.inputValue()==='5'&&await page.locator('#sound-form-1-random-11-enabled').isChecked(),'Reload restores Random, weights and inclusions');
    check(!await page.locator('#sound-form-1-random').evaluate(e=>e.open),'Reload keeps probability controls collapsed by default');
    await page.evaluate(()=>{window.settings.tracks=window.settings.tracks.filter(t=>t.id!==11);window.dispatchBridge({type:'settings',settings:window.settings});});
    await page.locator('#sound-track-1').selectOption('3');
    await page.waitForFunction(()=>window.messages.some(m=>m.action==='saveSound'&&m.data.kind===1&&m.data.track===3));
    check(await page.evaluate(()=>window.messages.findLast(m=>m.action==='saveSound'&&m.data.kind===1).data.randomTracks.find(t=>t.track===11)?.enabled===true),'A temporarily unavailable recording retains its saved probability instead of silently resetting it');
    await page.locator('#sound-track-1').selectOption('10');
    await page.locator('#sound-form-1-random summary').click();
    for(const theme of [0,1,2,3])for(const width of [940,739,336]){
      await page.setViewportSize({width,height:780});await page.evaluate(theme=>document.documentElement.dataset.theme=String(theme),theme);
      check(await page.locator('main').evaluate(e=>e.scrollWidth<=e.clientWidth)&&await page.locator('#sound-form-1-random table').evaluate(e=>e.scrollWidth<=e.clientWidth),`Long track names and probability columns fit at ${width}px in theme ${theme}`);
    }
    await page.setViewportSize({width:739,height:642});await page.evaluate(()=>document.documentElement.dataset.theme='0');
    await page.locator('#sound-form-1-random summary').scrollIntoViewIfNeeded();
    await fs.mkdir(path.resolve(__dirname,'../../../../outputs'),{recursive:true});await page.screenshot({path:path.resolve(__dirname,'../../../../outputs/random-audio-editor.png')});
    check(errors.length===0,'No browser errors while editing, mirroring, saving or reloading Random pools');
    console.log(`${passed} Random audio browser checks passed.`);
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
