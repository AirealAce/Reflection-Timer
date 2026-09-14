// Exercise the added App controls through the existing handlers and form.
module.exports=async function appPlayback(context,initial,check){
  const page=await context.newPage();await page.goto('https://reflection-timer.invalid/index.html?view=main');
  await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
  await page.evaluate(initial=>{
    window.playbackState=structuredClone(initial);const normal=window.chrome.webview.postMessage;
    window.chrome.webview.postMessage=message=>{
      if(!['toggle','repeat','reset','end'].includes(message.action))return normal(message);
      window.previewMessages.push(message);
      const finish=()=>{
        if(window.failPlayback){window.previewDispatch({type:'reply',requestId:message.requestId,error:'Test save failed.'});return;}
        const state=window.playbackState;
        if(message.action==='toggle'){state.clock.status=state.clock.status==='Running'?'Paused':'Running';state.timer.endTime=state.clock.status==='Running'?123456789:null;}
        if(message.action==='repeat')state.timer.autoRestart=message.data.enabled;
        if(message.action==='reset')state.clock.status='Ready';
        if(message.action==='end')state.clock.status='Finished';
        window.previewDispatch({type:'state',state:structuredClone(state)});
        window.previewDispatch({type:'reply',requestId:message.requestId});
      };
      window.finishPlayback=finish;if(!window.holdPlayback)queueMicrotask(finish);
    };
    window.previewDispatch({type:'init',state:structuredClone(initial)});
  },initial);
  const controls=page.locator('.app-playback'),toggle=page.locator('#playback-toggle'),repeat=page.locator('#playback-repeat');
  check(await controls.getByRole('button').count()===5&&await page.locator('#timer').evaluate(e=>e.querySelector('.timer-notice').nextElementSibling.id==='timer-state'&&e.querySelector('#visual-clock').nextElementSibling.classList.contains('app-playback')),'App shows the status below Desktop active and all five Compact-style controls directly below the clock');
  for(const [key,label] of [['Enter','Pause timer'],['Space','Resume timer'],['Enter','Pause timer']]){
    const before=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length);
    await toggle.press(key);await page.waitForFunction(label=>document.querySelector('#playback-toggle').getAttribute('aria-label')===label,label);
    check(await page.evaluate(before=>window.previewMessages.filter(m=>m.action==='toggle').length===before+1,before)&&await toggle.evaluate(e=>e===document.activeElement),'App playback '+key+' submits once through the existing timer form and retains focus');
  }
  await page.evaluate(()=>window.holdPlayback=true);await toggle.click();await page.locator('#toggle').click();await toggle.press('Enter');
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length===4),'New and original App toggle controls share one pending-save guard');
  await page.evaluate(()=>{window.holdPlayback=false;window.finishPlayback();});await page.waitForFunction(()=>document.querySelector('#playback-toggle').title==='Resume timer');
  await page.locator('#minutes').fill('2');
  check(await toggle.getAttribute('aria-label')==='Start timer'&&await page.locator('#toggle').textContent()==='Start','Editing a paused duration updates both App start/resume labels');
  await page.locator('#playback-reset').click();await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='reset'));
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='reset').length===1&&window.previewMessages.find(m=>m.action==='reset').data.seconds===120),'App reset arrow uses the existing edited-duration reset handler');
  await page.locator('#playback-end').press('Enter');
  check(!await page.evaluate(()=>window.previewMessages.some(m=>m.action==='end')),'Unavailable App forward arrow cannot end an idle timer');
  await page.evaluate(()=>{window.failPlayback=true;window.holdPlayback=true;});await repeat.click();await repeat.press('Enter');
  check(await repeat.getAttribute('aria-pressed')==='true'&&await page.locator('#repeat').isChecked()&&await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='repeat').length===1),'Auto-start button updates the checkbox and blocks competing saves while pending');
  await page.evaluate(()=>window.finishPlayback());await page.waitForFunction(()=>document.querySelector('#error').textContent==='Test save failed.');
  check(await repeat.getAttribute('aria-pressed')==='false'&&!await page.locator('#repeat').isChecked()&&await repeat.getAttribute('aria-disabled')==='false','Failed Auto-start save restores the button and checkbox for retry');
  await page.evaluate(()=>{window.failPlayback=false;window.holdPlayback=false;});await repeat.press('Enter');await page.waitForFunction(()=>window.playbackState.timer.autoRestart);
  await page.locator('#repeat').uncheck();await page.waitForFunction(()=>!window.playbackState.timer.autoRestart);
  check(await repeat.getAttribute('aria-pressed')==='false','The original Auto-start checkbox also updates the new button');
  await page.locator('#playback-compact').click();
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='compact').length===1&&!window.previewMessages.some(m=>m.action==='toggleCompact')),'Comp requests opening/focusing Compact, without toggling its visibility off');
  await page.evaluate(()=>{window.playbackState.showFloatingTimer=true;window.previewDispatch({type:'state',state:structuredClone(window.playbackState)});});
  check(await page.locator('#playback-compact').getAttribute('data-view-visible')==='true','Comp highlights when the floating viewer is visible');
  await toggle.click();await page.waitForFunction(()=>window.playbackState.clock.status==='Running');await page.locator('#playback-end').click();await page.waitForFunction(()=>window.playbackState.clock.status==='Finished');
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='end').length===1&&!window.previewMessages.some(m=>m.action==='queue')),'App forward arrow delegates to End early without sending a reflection');
  await page.close();
};
