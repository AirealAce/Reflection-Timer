// Synthetic bridge, browser focus events and drafts only. Native HWND/UIA focus
// and actual NVDA speech are separate integration/reader acceptance checks.
module.exports=async function reflectionFocus(context,initial,check){
  const prompt={id:'reflection-focus',isCheckIn:true,endedEarly:false,showEarlyEndReason:true,
    draft:('An existing reflection keeps its selection and scroll.\n').repeat(45),
    earlyEndReason:'Saved reason',actual:'10 seconds',allotted:'15 minutes',completed:'Today'};
  const state={...initial,prompts:[prompt]};
  async function open({focused=true,initialize=true}={}){
    const page=await context.newPage();
    await page.goto('https://reflection-timer.invalid/index.html?view=reflection');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.bringToFront();
    await page.evaluate(focused=>{
      window.simulatedDocumentFocus=focused;
      // Headless Chrome cannot model a native WebView child losing HWND focus.
      // The host integration tests cover that part of the handoff separately.
      Object.defineProperty(document,'hasFocus',{configurable:true,value:()=>window.simulatedDocumentFocus});
    },focused);
    if(initialize)await page.evaluate(state=>window.previewDispatch({type:'init',state,promptId:'reflection-focus'}),state);
    return page;
  }
  const actions=page=>page.evaluate(()=>window.previewMessages.map(message=>message.action));
  async function captureRefresh(page,message={type:'focusReflection',refreshFocus:true}){
    return page.evaluate(message=>{
      window.originalReflectionRAF=window.requestAnimationFrame;
      window.reflectionFocusFrames=[];
      window.requestAnimationFrame=callback=>{window.reflectionFocusFrames.push(callback);return window.reflectionFocusFrames.length;};
      window.previewDispatch(message);
      return document.activeElement===document.body;
    },message);
  }
  async function finishRefresh(page){
    await page.evaluate(()=>{
      window.requestAnimationFrame=window.originalReflectionRAF;
      const callbacks=window.reflectionFocusFrames.splice(0);
      for(const callback of callbacks)callback(performance.now());
    });
  }

  const cold=await open({focused:false});
  check(await cold.locator('#reflection-text').inputValue()===prompt.draft
    &&await cold.evaluate(()=>document.activeElement!==document.querySelector('#reflection-text')),
    'A background reflection preload populates the draft without claiming DOM focus');
  check(await cold.evaluate(()=>window.previewMessages.some(message=>message.action==='reflectionReady')),
    'A background preload still acknowledges editor readiness without a focus requirement');
  await cold.evaluate(()=>{window.simulatedDocumentFocus=true;window.previewDispatch({type:'focusReflection',refreshFocus:true});});
  check(await cold.locator('#reflection-text').evaluate(input=>document.activeElement===input),
    'An explicit host focus request reaches the preloaded response after document focus is acquired');
  check(await cold.getByRole('textbox',{name:'Your reflection',exact:true}).isEditable()
    &&await cold.locator('[role=application]').count()===0,
    'The reflection retains its labelled native multiline editor and ordinary document reading semantics');
  await cold.close();

  const fresh=await open();
  const before=await fresh.evaluate(()=>{
    const input=document.querySelector('#reflection-text');
    input.focus();input.setSelectionRange(11,43,'backward');input.scrollTop=180;
    window.reflectionFocusTransitions=[];
    input.addEventListener('blur',()=>window.reflectionFocusTransitions.push('blur'));
    input.addEventListener('focus',()=>window.reflectionFocusTransitions.push('focus'));
    const before={value:input.value,start:input.selectionStart,end:input.selectionEnd,direction:input.selectionDirection,top:input.scrollTop,left:input.scrollLeft};
    window.previewDispatch({type:'focusReflection',refreshFocus:true});
    return {...before,blurredBeforeFrame:document.activeElement===document.body};
  });
  check(before.blurredBeforeFrame,'Refreshing retained editor focus creates a blur before scheduling the fresh focus event');
  await fresh.waitForFunction(()=>window.reflectionFocusTransitions.includes('focus'));
  const after=await fresh.locator('#reflection-text').evaluate(input=>({value:input.value,start:input.selectionStart,end:input.selectionEnd,direction:input.selectionDirection,top:input.scrollTop,left:input.scrollLeft,active:document.activeElement===input,events:window.reflectionFocusTransitions}));
  check(after.active&&after.events.join(',')==='blur,focus',
    'A retained response receives a fresh browser focus event in a later animation frame');
  check(after.value===before.value&&after.start===before.start&&after.end===before.end&&after.direction===before.direction
    &&after.top===before.top&&after.left===before.left,
    'Fresh focus preserves the complete existing response, selection direction, caret range and textarea scroll');
  check(!(await actions(fresh)).some(action=>['draft','queue','saveForLater','skip','close','toggle'].includes(action)),
    'A focus refresh neither saves nor submits the draft and cannot change the timer');
  await fresh.close();

  const stale=await open();
  await stale.evaluate(()=>{window.simulatedDocumentFocus=false;window.previewDispatch({type:'reflectionShortcut',allowSave:true});});
  check(!(await actions(stale)).includes('saveForLater')
    &&await stale.locator('#reflection-text').inputValue()===prompt.draft,
    'A retained DOM activeElement without document focus cannot be misclassified as Save');
  await stale.evaluate(()=>window.simulatedDocumentFocus=true);
  check(await captureRefresh(stale,{type:'reflectionShortcut',allowSave:false}),
    'The host can explicitly require focus-only behavior even when the retained response is already active');
  await finishRefresh(stale);
  check(await stale.locator('#reflection-text').evaluate(input=>document.activeElement===input)
    &&!(await actions(stale)).includes('saveForLater'),
    'Focus-only slash refreshes the editor without closing a retained draft');
  await stale.evaluate(()=>window.previewDispatch({type:'reflectionShortcut',allowSave:true}));
  await stale.waitForFunction(()=>window.previewMessages.some(message=>message.action==='saveForLater'));
  check((await actions(stale)).filter(action=>action==='saveForLater').length===1,
    'A subsequent slash from a genuinely focused editor still saves once with the original shortcut behavior');
  await stale.close();

  const typing=await open();
  await typing.locator('#later').focus();
  await typing.evaluate(()=>window.previewDispatch({type:'prepareReflectionFocus',focusRequestId:1}));
  check(await typing.locator('#reflection-text').evaluate(input=>document.activeElement===input),
    'Preparing native focus accepts immediate typing without waiting for the shortcut-release delay');
  await typing.locator('#reflection-text').evaluate(input=>{input.setSelectionRange(19,19);input.scrollTop=140;});
  await typing.keyboard.type('quick input');
  const typed=await typing.locator('#reflection-text').evaluate(input=>({value:input.value,start:input.selectionStart,end:input.selectionEnd,top:input.scrollTop}));
  await typing.evaluate(()=>window.previewDispatch({type:'focusReflection',refreshFocus:true,focusRequestId:1}));
  await typing.waitForFunction(()=>document.activeElement.id==='reflection-text');
  check(await typing.locator('#reflection-text').evaluate((input,before)=>input.value===before.value&&input.selectionStart===before.start
    &&input.selectionEnd===before.end&&input.scrollTop===before.top,typed),
    'The matching native refresh preserves text and caret edits made during shortcut-release settling');
  check(!(await actions(typing)).some(action=>['queue','saveForLater','skip','close','toggle'].includes(action)),
    'Typing during focus preparation cannot submit, close or change the timer');
  await typing.close();

  for(const interaction of ['click another control','Tab to another field','click response background','window blur','busy handoff','new prompt']){
    const page=await open();
    await page.evaluate(()=>window.previewDispatch({type:'prepareReflectionFocus',focusRequestId:12}));
    if(interaction==='click another control')await page.locator('#early-reason').click();
    else if(interaction==='Tab to another field')await page.keyboard.press('Tab');
    else await page.evaluate(({interaction,state,prompt})=>{
      if(interaction==='click response background'){
        document.body.dispatchEvent(new Event('pointerdown',{bubbles:true}));document.querySelector('#reflection-text').setSelectionRange(31,31);
      }else if(interaction==='window blur'){
        window.dispatchEvent(new Event('blur'));window.simulatedDocumentFocus=false;
        window.simulatedDocumentFocus=true;window.dispatchEvent(new Event('focus'));
      }else if(interaction==='busy handoff')window.previewDispatch({type:'flush',freeze:true});
      else window.previewDispatch({type:'showReflection',state:{...state,prompts:[prompt]},promptId:prompt.id});
    },{interaction,state,prompt:{...prompt,id:'ticket-new-prompt',draft:'A newly opened prompt',earlyEndReason:''}});
    const beforeFinal=await page.evaluate(()=>{
      window.ticketBlurCount=0;document.querySelector('#reflection-text').addEventListener('blur',()=>window.ticketBlurCount++);
      return {active:document.activeElement.id,value:document.querySelector('#reflection-text').value,start:document.querySelector('#reflection-text').selectionStart,end:document.querySelector('#reflection-text').selectionEnd};
    });
    await page.evaluate(()=>window.previewDispatch({type:'focusReflection',refreshFocus:true,focusRequestId:12}));
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    check(await page.evaluate(before=>document.activeElement.id===before.active&&window.ticketBlurCount===0
      &&document.querySelector('#reflection-text').value===before.value&&document.querySelector('#reflection-text').selectionStart===before.start
      &&document.querySelector('#reflection-text').selectionEnd===before.end,beforeFinal),
      'A prepared native focus ticket is cancelled by '+interaction+' before its delayed final message');
    check(!(await actions(page)).some(action=>['queue','saveForLater','skip','close','toggle'].includes(action)),
      'Cancelling the native ticket after '+interaction+' retains the draft without a submission or timer action');
    await page.close();
  }

  const tickets=await open();
  await tickets.evaluate(()=>{
    const input=document.querySelector('#reflection-text');input.setSelectionRange(9,22,'backward');window.ticketRefreshes=0;
    input.addEventListener('blur',()=>window.ticketRefreshes++);
    window.previewDispatch({type:'prepareReflectionFocus',focusRequestId:20});
    window.previewDispatch({type:'prepareReflectionFocus',focusRequestId:21});
    window.previewDispatch({type:'focusReflection',refreshFocus:true,focusRequestId:20});
  });
  check(await tickets.evaluate(()=>window.ticketRefreshes===0&&document.activeElement.id==='reflection-text'),
    'An obsolete final native ticket cannot consume or act on the newer prepared focus request');
  await tickets.evaluate(()=>window.previewDispatch({type:'focusReflection',refreshFocus:true,focusRequestId:21}));
  await tickets.waitForFunction(()=>document.activeElement.id==='reflection-text'&&window.ticketRefreshes===1);
  check(await tickets.locator('#reflection-text').evaluate(input=>input.selectionStart===9&&input.selectionEnd===22&&input.selectionDirection==='backward'),
    'The newest matching ticket refreshes focus once and retains the original backward selection');
  await tickets.evaluate(()=>window.previewDispatch({type:'focusReflection',refreshFocus:true,focusRequestId:21}));
  await tickets.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
  check(await tickets.evaluate(()=>window.ticketRefreshes===1),'A consumed native focus ticket cannot refresh the editor twice');
  await tickets.close();

  const unprepared=await open({focused:false});
  await unprepared.evaluate(()=>{
    window.previewDispatch({type:'prepareReflectionFocus',focusRequestId:30});window.simulatedDocumentFocus=true;
    window.previewDispatch({type:'focusReflection',refreshFocus:true,focusRequestId:30});
  });
  await unprepared.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
  check(await unprepared.locator('#reflection-text').evaluate(input=>document.activeElement!==input),
    'A background document cannot arm a native ticket that later reclaims editor focus');
  await unprepared.close();

  const other=await open();
  await other.locator('#later').focus();
  await other.evaluate(()=>window.previewDispatch({type:'focusReflection',refreshFocus:true}));
  check(await other.locator('#reflection-text').evaluate(input=>document.activeElement===input),
    'Focusing from another popup control reaches the response immediately without an unnecessary blur cycle');
  await other.evaluate(()=>{window.simulatedDocumentFocus=false;document.querySelector('#later').focus();});
  const next={...prompt,id:'next-focus',draft:'A different saved response',earlyEndReason:''};
  await other.evaluate(({state,prompt})=>window.previewDispatch({type:'showReflection',state:{...state,prompts:[prompt]},promptId:prompt.id}),{state,prompt:next});
  check(await other.locator('#reflection-text').inputValue()===next.draft
    &&await other.locator('#later').evaluate(button=>document.activeElement===button),
    'Loading another background reflection updates its text without redirecting the retained document focus');
  await other.close();

  for(const scenario of ['window blur','background click','other control','modal dialog','busy handoff','new prompt','new request']){
    const page=await open();
    await page.locator('#reflection-text').evaluate(input=>{input.focus();input.setSelectionRange(3,17,'backward');});
    await captureRefresh(page);
    await page.evaluate(({scenario,state,prompt})=>{
      if(scenario==='window blur'){
        window.dispatchEvent(new Event('blur'));window.simulatedDocumentFocus=false;
        // Even a quick return must not revive a request abandoned on blur.
        window.simulatedDocumentFocus=true;window.dispatchEvent(new Event('focus'));
      }else if(scenario==='background click')document.body.dispatchEvent(new Event('pointerdown',{bubbles:true}));
      else if(scenario==='other control')document.querySelector('#later').focus();
      else if(scenario==='modal dialog')document.querySelector('#delivery-dialog').showModal();
      else if(scenario==='busy handoff')window.previewDispatch({type:'flush',freeze:true});
      else if(scenario==='new prompt')window.previewDispatch({type:'showReflection',state:{...state,prompts:[prompt]},promptId:prompt.id});
      else{
        window.previewDispatch({type:'focusReflection',refreshFocus:true});
        document.querySelector('#reflection-text').setSelectionRange(29,29);
      }
    },{scenario,state,prompt:next});
    await finishRefresh(page);
    const result=await page.evaluate(()=>({active:document.activeElement.id,body:document.activeElement===document.body,
      modal:!!document.activeElement.closest('dialog[open]'),value:document.querySelector('#reflection-text').value,
      start:document.querySelector('#reflection-text').selectionStart,end:document.querySelector('#reflection-text').selectionEnd}));
    if(scenario==='window blur'||scenario==='background click'||scenario==='busy handoff')check(result.body,
      'A pending response focus is cancelled by '+scenario+' instead of reclaiming keyboard focus');
    else if(scenario==='other control')check(result.active==='later',
      'A pending response focus respects the control selected by the user');
    else if(scenario==='modal dialog')check(result.modal,
      'A pending response focus cannot escape an opened modal dialog');
    else if(scenario==='new prompt')check(result.value===next.draft&&result.start===next.draft.length&&result.end===next.draft.length,
      'An obsolete focus request cannot restore the old caret selection into a different reflection');
    else check(result.active==='reflection-text'&&result.start===29&&result.end===29,
      'A newer focus request cancels the earlier request without restoring a stale selection');
    check(!(await actions(page)).some(action=>['queue','saveForLater','skip','close','toggle'].includes(action)),
      'Cancelling response focus after '+scenario+' cannot submit, close, skip or change the timer');
    await page.close();
  }
};
