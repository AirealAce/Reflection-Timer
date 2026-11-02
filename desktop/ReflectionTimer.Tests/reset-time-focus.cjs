// The native host sends focusTime only after accepting its reset warning.
// These synthetic pages verify the clock target and keep native policy separate.
module.exports=async function resetTimeFocus(context,initial,settings,check){
  for(const view of ['main','compact','time-only'])for(const stopwatch of [false,true]){
    const page=await context.newPage(),label=view+' '+(stopwatch?'Stopwatch':'Timer');
    try{
      await page.goto(view==='main'?'https://reflection-timer.invalid/index.html?view=main':'https://reflection-timer.invalid/compact.html');
      await page.waitForFunction(()=>window.previewMessages.some(message=>message.action==='ready'));
      await page.evaluate(()=>window.previewDispatch({type:'focusTime'}));
      check(!await page.evaluate(()=>['visual-clock','read-time'].includes(document.activeElement.id)),label+' ignores a focus request before the interface state is initialized');
      const state={...initial,timer:{...initial.timer,mode:stopwatch?1:0,durationSeconds:900},clock:{seconds:stopwatch?0:900,text:stopwatch?'0 seconds':'15 minutes',status:'Ready',stopwatch}};
      await page.evaluate(({state,settings,view})=>{window.previewDispatch({type:'init',state,timeOnly:view==='time-only'});window.previewDispatch({type:'settings',settings});},{state,settings,view});
      if(view==='main'){
        await page.locator('#tab-settings').focus();await page.keyboard.press('Enter');await page.keyboard.press('Tab');
      }else {await page.locator('#read-time').focus();await page.keyboard.press('Tab');}
      const target=page.locator(view==='main'?'#visual-clock':'#read-time');
      const inputs=await page.locator('#hours,#minutes,#seconds').evaluateAll(nodes=>nodes.map(input=>input.value));
      const layout=await page.evaluate(()=>({tiny:document.body.dataset.tiny,width:document.body.getBoundingClientRect().width,height:document.body.getBoundingClientRect().height}));
      await page.evaluate(()=>window.previewDispatch({type:'focusTime'}));
      await page.waitForFunction(id=>document.activeElement.id===id,view==='main'?'visual-clock':'read-time');
      const expected=(stopwatch?'Stopwatch, 0:00 elapsed.':'Timer, 15:00 remaining.')+' Ready.';
      check(await target.isVisible()&&await target.getAttribute('aria-label')===expected,label+' focuses the displayed clock and exposes its reset numeric value and mode');
      check(view!=='main'||await page.locator('#tab-timer').getAttribute('aria-selected')==='true',label+' returns App to Timer instead of leaving focus in Settings');
      check(await target.getAttribute('aria-live')==='off'&&await target.getAttribute('aria-hidden')!=='true',label+' focused clock is accessible and explicitly excludes live ticking speech');
      if(view==='main')check(await target.getAttribute('role')==='timer'&&await target.getAttribute('tabindex')==='-1'
        &&await target.evaluate(element=>element.matches(':focus-visible')&&parseFloat(getComputedStyle(element).outlineWidth)>=2),label+' displayed text has a visible focus outline without becoming an extra Tab stop');
      else check(JSON.stringify(layout)===JSON.stringify(await page.evaluate(()=>({tiny:document.body.dataset.tiny,width:document.body.getBoundingClientRect().width,height:document.body.getBoundingClientRect().height}))),label+' clock focus preserves the existing Compact or Time-only size and layout');
      check(JSON.stringify(inputs)===JSON.stringify(await page.locator('#hours,#minutes,#seconds').evaluateAll(nodes=>nodes.map(input=>input.value))),label+' clock focus does not edit or select duration inputs');
      const cdp=await context.newCDPSession(page),tree=await cdp.send('Accessibility.getFullAXTree');
      check(tree.nodes.some(node=>!node.ignored&&node.name?.value===expected&&node.role?.value===(view==='main'?'timer':'button')&&node.properties?.some(property=>property.name==='focused'&&property.value.value)),label+' reader focus points to the named time rather than the play button or a number field');await cdp.detach();
      const running={...state,clock:{...state.clock,status:'Running'},timer:{...state.timer,endTime:stopwatch?null:Date.now()+900000}};
      await page.evaluate(state=>{window.previewDispatch({type:'state',state,keepTimeOnly:true});window.previewDispatch({type:'focusTime'});},running);
      await page.waitForFunction(id=>document.activeElement.id===id,view==='main'?'visual-clock':'read-time');
      const focusName=await target.getAttribute('aria-label');
      for(const tick of [1,2,3])await page.evaluate(clock=>window.previewDispatch({type:'clock',clock}),{...running.clock,seconds:stopwatch?tick:900-tick,text:stopwatch?tick+' seconds':(900-tick)+' seconds'});
      check(await target.evaluate(element=>element===document.activeElement)&&await target.getAttribute('aria-label')===focusName&&await page.locator('#visual-clock').textContent()===(stopwatch?'0:03':'14:57'),label+' real countdown/elapsed ticks update the visible time without renaming the focused clock');
      check(await page.locator('#status').textContent()===''&&await page.locator('#session-status').textContent()==='',label+' clock focus and ticks add no live status announcement');
      await page.evaluate(()=>window.previewDispatch({type:'focusTime'}));
      await page.waitForFunction(id=>document.activeElement.id===id,view==='main'?'visual-clock':'read-time');
      check(await target.getAttribute('aria-label').then(name=>name.includes(stopwatch?'0:03 elapsed':'14:57 remaining')),label+' a repeated deliberate focus request refreshes its readable time snapshot');
      const alternate=view==='main'?'#tab-timer':'#close';
      await page.evaluate(id=>{window.previewDispatch({type:'focusTime'});document.querySelector(id).focus();},alternate);
      await page.waitForTimeout(40);
      check(await page.locator(alternate).evaluate(element=>element===document.activeElement),label+' navigation before a repeated focus refresh cancels that delayed focus');
      if(view==='main'){await page.locator('#tab-settings').click();await page.locator('#settings-search-input').focus();}
      await page.evaluate(()=>{const dialog=document.createElement('dialog');dialog.id='clock-focus-test-dialog';dialog.innerHTML='<label>Keep this dialog<input id="clock-modal-input" value="Do not change"></label>';document.body.append(dialog);dialog.showModal();document.querySelector('#clock-modal-input').focus();});
      const tab=await page.evaluate(()=>document.body.dataset.tab);
      await page.evaluate(()=>window.previewDispatch({type:'focusTime'}));
      check(await page.locator('#clock-modal-input').evaluate(element=>element===document.activeElement)&&await page.locator('#clock-modal-input').inputValue()==='Do not change'&&await page.evaluate(tab=>document.body.dataset.tab===tab,tab),label+' a clock focus request cannot disrupt an active dialog or switch the App tab behind it');
      await page.evaluate(()=>document.querySelector('#clock-focus-test-dialog').close());
      check(await page.evaluate(()=>!window.previewMessages.some(message=>['toggle','reset','resetAndReload','end','queue','skip','saveForLater','saveOrSendReflection'].includes(message.action))),label+' focusing the time never operates playback or submits a reflection');
    }finally{await page.close();}
  }
  const reflection=await context.newPage();
  try{
    await reflection.goto('https://reflection-timer.invalid/index.html?view=reflection');await reflection.waitForFunction(()=>window.previewMessages.some(message=>message.action==='ready'));
    await reflection.evaluate(initial=>window.previewDispatch({type:'init',promptId:'clock-reflection',state:{...initial,prompts:[{id:'clock-reflection',isCheckIn:true,draft:'Keep this draft',earlyEndReason:'',actual:'2 minutes',allotted:'15 minutes',completed:'Today'}]}}),initial);
    await reflection.locator('#reflection-text').focus();await reflection.evaluate(()=>window.previewDispatch({type:'focusTime'}));
    check(await reflection.locator('#reflection-text').evaluate(element=>element===document.activeElement)&&await reflection.locator('#reflection-text').inputValue()==='Keep this draft','A focusTime message cannot move a session-end editor to its hidden timer controls');
  }finally{await reflection.close();}
};
