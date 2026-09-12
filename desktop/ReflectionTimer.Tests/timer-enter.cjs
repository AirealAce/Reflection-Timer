// Synthetic native replies only: these tests never start the installed timer.
module.exports=async function timerEnter(context,initial,check){
  for(const view of ['App','Compact']){
    const page=await context.newPage();
    await page.goto('https://reflection-timer.invalid/'+(view==='App'?'index.html?view=main':'compact.html'));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(initial=>{
      window.enterState=structuredClone(initial);
      const post=window.chrome.webview.postMessage;
      window.chrome.webview.postMessage=message=>{
        if(message.action!=='toggle')return post(message);
        window.previewMessages.push(message);
        window.finishToggle=()=>{
          if(window.failToggle){window.previewDispatch({type:'reply',requestId:message.requestId,error:'Test toggle failed.'});return;}
          const state=window.enterState,wasRunning=state.clock.status==='Running';
          if(!wasRunning){
            if(state.clock.status!=='Paused'||message.data.seconds!==state.timer.durationSeconds)state.clock.seconds=message.data.seconds;
            state.timer.durationSeconds=message.data.seconds;
          }
          state.clock.status=wasRunning?'Paused':'Running';
          state.timer.endTime=wasRunning?null:123456789;
          window.previewDispatch({type:'state',state:structuredClone(state)});
          if(!wasRunning)window.previewDispatch({type:'durationDraft',parts:null});
          window.previewDispatch({type:'reply',requestId:message.requestId});
        };
        if(!window.holdToggle)queueMicrotask(()=>window.finishToggle());
      };
      window.previewDispatch({type:'init',state:structuredClone(initial)});
    },initial);
    async function reset(status){
      await page.evaluate(({initial,status})=>{
        window.holdToggle=false;window.failToggle=false;window.previewMessages=[];
        window.enterState={...structuredClone(initial),clock:{seconds:status==='Ready'||status==='Finished'?900:117,text:'Remaining time',status},timer:{...initial.timer,endTime:status==='Running'?123456789:null}};
        window.previewDispatch({type:'durationDraft',parts:null});
        window.previewDispatch({type:'state',state:structuredClone(window.enterState)});
        window.previewDispatch({type:'expandCompact'});
      },{initial,status});
    }
    async function toggles(){return page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle'));}
    for(const field of ['hours','minutes','seconds']){
      for(const status of ['Running','Paused','Ready','Finished']){
        await reset(status);
        await page.locator('#'+field).press('Enter');
        const expected=status==='Running'?'Paused':'Running';
        await page.waitForFunction(expected=>window.enterState.clock.status===expected,expected);
        const commands=await toggles();
        check(commands.length===1,`${view} ${field}: Enter sends exactly one toggle from ${status}`);
        check(await page.evaluate(()=>window.enterState.clock.seconds)===(status==='Running'||status==='Paused'?117:900),`${view} ${field}: ${status} keeps paused progress or starts the specified duration`);
        check(await page.evaluate(()=>!window.previewMessages.some(m=>['end','reset','checkIn','queue','startOrEnd'].includes(m.action))),`${view} ${field}: Enter does not end early, reset, or submit a reflection`);
        if(status==='Running'){
          check(await page.locator('#'+field).evaluate(e=>document.activeElement===e&&!e.readOnly),`${view} ${field}: pausing retains input focus and enables editing`);
          await page.locator('#'+field).press('Enter');
          await page.waitForFunction(()=>window.enterState.clock.status==='Running');
          check((await toggles()).length===2,`${view} ${field}: the next separate Enter resumes`);
        }
      }
    }
    await reset('Running');
    await page.locator('#hours').focus();
    await page.keyboard.down('Enter');
    await page.waitForFunction(()=>window.enterState.clock.status==='Paused');
    await page.keyboard.down('Enter');
    await page.keyboard.down('Enter');
    await page.keyboard.up('Enter');
    check((await toggles()).length===1,view+' ignores held-key repeats even after the pause reply');
    await reset('Running');
    await page.evaluate(()=>{window.holdToggle=true;});
    await page.locator('#minutes').press('Enter');
    await page.locator('#minutes').press('Enter');
    await page.locator('#toggle').click();
    check((await toggles()).length===1,view+' ignores another Enter or button submit while a toggle is in flight');
    await page.evaluate(()=>window.finishToggle());
    await page.evaluate(()=>{window.holdToggle=false;window.failToggle=true;});
    await page.locator('#seconds').press('Enter');
    await page.waitForFunction(()=>document.querySelector('#error').textContent==='Test toggle failed.');
    check(await page.locator('#seconds').evaluate(e=>document.activeElement===e),view+' reports a failed toggle without losing field focus');
    await page.evaluate(()=>{window.failToggle=false;});
    await page.locator('#seconds').press('Enter');
    await page.waitForFunction(()=>window.enterState.clock.status==='Running');
    check((await toggles()).length===3,view+' can retry Enter after a failed toggle');
    await reset('Running');
    await page.evaluate(()=>window.previewDispatch({type:'durationDraft',parts:['0','0','0']}));
    await page.locator('#hours').press('Enter');
    await page.waitForFunction(()=>window.enterState.clock.status==='Paused');
    check((await toggles()).length===1&&await page.locator('#error').textContent()==='',view+' can pause even with an invalid shared duration draft');
    await reset('Ready');
    for(const field of ['hours','minutes','seconds'])await page.locator('#'+field).fill('0');
    await page.locator('#seconds').press('Enter');
    await page.waitForFunction(()=>document.querySelector('#error').textContent.includes('one second'));
    check((await toggles()).length===0,view+' still rejects starting a zero-length duration');
    await page.locator('#seconds').fill('100');
    await page.locator('#seconds').press('Enter');
    await page.waitForFunction(()=>window.enterState.clock.status==='Running');
    check((await toggles())[0].data.seconds===100&&await page.locator('#minutes').inputValue()==='1'&&await page.locator('#seconds').inputValue()==='40',view+' retries after validation failure and normalizes excess seconds');
    await reset('Paused');
    await page.locator('#minutes').fill('2');
    await page.locator('#minutes').press('Enter');
    await page.waitForFunction(()=>window.enterState.clock.status==='Running');
    check((await toggles())[0].data.seconds===120,view+' starts the edited duration when Enter is pressed while paused');
    await reset('Ready');
    await page.locator('#hours').dispatchEvent('keydown',{key:'Enter',isComposing:true});
    check((await toggles()).length===0,view+' does not start while Enter is committing composed text');
    if(view==='Compact'){
      for(const tiny of [false,true]){
        const targets=tiny?['#read-time','#shrink','#expand','#close','background']:['#hours','#minutes','#seconds','#repeat','#app','#reset','#toggle','#end','#shrink','#expand','#close','#read-time','background'];
        for(const target of targets)for(const status of ['Ready','Running','Paused','Finished']){
          await reset(status);
          if(tiny)await page.evaluate(()=>window.previewDispatch({type:'shrinkCompact'}));
          if(target==='background')await page.evaluate(()=>document.activeElement.blur());else await page.locator(target).focus();
          await page.keyboard.press('Control+Enter');
          await page.waitForFunction(expected=>window.enterState.clock.status===expected,status==='Running'?'Paused':'Running');
          const commands=await toggles();
          check(commands.length===1&&await page.evaluate(()=>window.enterState.clock.seconds)===(status==='Running'||status==='Paused'?117:900),`${tiny?'Time-only':'Compact'} Ctrl+Enter from ${target} toggles ${status} exactly once and preserves the correct progress`);
          check(await page.evaluate(()=>!window.previewMessages.some(m=>['repeat','main','end','reset','close','readTime','checkIn','queue','startOrEnd'].includes(m.action))),`${tiny?'Time-only':'Compact'} Ctrl+Enter from ${target} overrides the focused control without side effects: ${status}`);
        }
      }
      await reset('Running');await page.locator('#close').focus();
      await page.keyboard.down('Control');await page.keyboard.down('Enter');
      await page.waitForFunction(()=>window.enterState.clock.status==='Paused');
      await page.keyboard.down('Enter');await page.keyboard.down('Enter');
      await page.keyboard.up('Enter');await page.keyboard.up('Control');
      check((await toggles()).length===1,'Holding Compact Ctrl+Enter cannot alternate pause and resume after a reply');
      await reset('Running');await page.evaluate(()=>window.holdToggle=true);
      await page.locator('#repeat').press('Control+Enter');
      await page.locator('#hours').press('Enter');await page.locator('#toggle').click();await page.locator('#close').press('Control+Enter');
      check((await toggles()).length===1,'Compact Ctrl+Enter shares the Enter/button in-flight guard');
      await page.evaluate(()=>window.finishToggle());await page.waitForFunction(()=>window.enterState.clock.status==='Paused');
      await page.evaluate(()=>{window.holdToggle=false;window.failToggle=true;});
      await page.locator('#read-time').press('Control+Enter');
      await page.waitForFunction(()=>document.querySelector('#error').textContent==='Test toggle failed.');
      check(await page.locator('#read-time').evaluate(e=>e===document.activeElement),'Failed Compact Ctrl+Enter retains focus and reports its error');
      await page.evaluate(()=>window.failToggle=false);await page.keyboard.press('Control+Enter');
      await page.waitForFunction(()=>window.enterState.clock.status==='Running');
      check((await toggles()).length===3,'Compact Ctrl+Enter can retry after a failed native save');
      await reset('Ready');
      for(const field of ['hours','minutes','seconds'])await page.locator('#'+field).fill('0');
      await page.locator('#close').press('Control+Enter');
      await page.waitForFunction(()=>document.querySelector('#error').textContent.includes('one second'));
      check((await toggles()).length===0,'Compact Ctrl+Enter validates the duration without closing the viewer');
      await page.locator('#seconds').fill('100');await page.locator('#app').press('Control+Enter');
      await page.waitForFunction(()=>window.enterState.clock.status==='Running');
      check((await toggles())[0].data.seconds===100&&await page.locator('#minutes').inputValue()==='1'&&await page.locator('#seconds').inputValue()==='40','Compact Ctrl+Enter normalizes excess time and uses the shared edited duration');
      await reset('Paused');await page.locator('#minutes').fill('2');await page.locator('#reset').press('Control+Enter');
      await page.waitForFunction(()=>window.enterState.clock.status==='Running');
      check((await toggles())[0].data.seconds===120,'Compact Ctrl+Enter starts the edited duration while paused, just like Enter');
      await reset('Running');await page.evaluate(()=>window.previewDispatch({type:'durationDraft',parts:['0','0','0']}));
      await page.locator('#end').press('Control+Enter');await page.waitForFunction(()=>window.enterState.clock.status==='Paused');
      check((await toggles()).length===1&&await page.locator('#error').textContent()==='','Compact Ctrl+Enter can pause even when the edited duration is invalid');
      await reset('Ready');
      await page.locator('#hours').dispatchEvent('keydown',{key:'Enter',ctrlKey:true,isComposing:true});
      check((await toggles()).length===0,'Compact Ctrl+Enter does not toggle while committing composed text');
      await page.evaluate(()=>{const dialog=document.createElement('dialog');dialog.innerHTML='<input aria-label="Modal input">';document.body.append(dialog);dialog.showModal();});
      await page.keyboard.press('Control+Enter');
      check((await toggles()).length===0,'A modal dialog keeps Ctrl+Enter instead of toggling the Compact timer behind it');
      await page.goto('https://reflection-timer.invalid/compact.html');
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
      for(const target of ['#hours','#toggle','#close'])await page.locator(target).press('Control+Enter');
      check((await toggles()).length===0&&await page.locator('#error').textContent()==='','Compact Ctrl+Enter waits for initialization without activating its focused control');
    }
    await page.close();
  }
};
