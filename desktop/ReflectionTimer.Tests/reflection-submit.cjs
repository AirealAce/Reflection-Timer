// Synthetic bridge only: exercises real keyboard events without sending to Sheets.
module.exports=async function reflectionSubmit(context,initial,check){
  async function open(kind='early',view='reflection',initialize=true){
    const page=await context.newPage();
    await page.goto('https://reflection-timer.invalid/index.html?view='+view);
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    if(initialize)await page.evaluate(state=>window.previewDispatch({type:'init',state,promptId:'submit-test'}),{
      ...initial,prompts:['older','submit-test','newer'].map(id=>({id,isCheckIn:kind==='check-in',endedEarly:kind==='early',showEarlyEndReason:kind!=='natural',
        draft:'Saved response',earlyEndReason:'Saved reason',actual:'2 minutes',allotted:'15 minutes',completed:'Today'}))
    });
    return page;
  }
  const messages=page=>page.evaluate(()=>window.previewMessages);
  const submitted=page=>page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='queue'));
  async function focus(page,target){
    if(target==='background')await page.evaluate(()=>document.activeElement.blur());
    else await page.locator(target).focus();
  }
  for(const kind of ['natural','early','check-in']){
    const targets=['#reflection-text','#reflection-heading','#later','#skip-reflection','#reflection-prev','#reflection-next','#reflection-form button[type=submit]','background'];
    if(kind!=='natural')targets.push('#early-reason');
    for(const target of targets)for(const key of ['Control+Enter','Alt+Enter']){
      const page=await open(kind);
      await focus(page,target);await page.keyboard.press(key);await submitted(page);
      const sent=await messages(page),entry=sent.filter(m=>m.action==='queue');
      check(entry.length===1&&entry[0].data.id==='submit-test'&&entry[0].data.text==='Saved response'&&entry[0].data.reason==='Saved reason'&&entry[0].data.endSession===(key==='Control+Enter'),`${kind} ${key} sends the current fields with the correct end-session intent from ${target}`);
      check(!sent.some(m=>['saveForLater','skip','navigateReflection','toggle','end','checkIn'].includes(m.action)),`${kind} ${key} overrides the focused action without a separate unscoped timer command`);
      await page.close();
    }
  }

  const page=await open();
  await page.evaluate(()=>{
    const normal=window.chrome.webview.postMessage;
    window.chrome.webview.postMessage=message=>{
      if(!['draft','queue'].includes(message.action))return normal(message);
      window.previewMessages.push(message);window[message.action+'Request']=message;
    };
    for(const [id,value] of [['reflection-text','Final keystrokes'],['early-reason','Final reason']]){
      const input=document.getElementById(id);input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));
    }
  });
  await page.locator('#later').focus();await page.keyboard.press('Control+Enter');
  await page.waitForFunction(()=>window.draftRequest);
  await page.keyboard.down('Control');await page.keyboard.down('Enter');await page.keyboard.down('Enter');await page.keyboard.up('Enter');await page.keyboard.up('Control');
  await page.keyboard.press('Alt+Enter');
  check((await messages(page)).filter(m=>m.action==='draft').length===1&&!(await messages(page)).some(m=>m.action==='queue')&&await page.locator('#reflection-text').evaluate(e=>e.readOnly),'Repeated Ctrl+Enter waits for one durable draft save and freezes edits');
  await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.draftRequest.requestId,error:'Draft save failed.'}));
  await page.waitForFunction(()=>document.querySelector('#error').textContent==='Draft save failed.');
  check(await page.locator('#reflection-text').isEditable()&&await page.locator('#reflection-text').inputValue()==='Final keystrokes'&&!(await messages(page)).some(m=>m.action==='queue'),'Failed draft save restores editing and retains the response without sending');
  await page.keyboard.press('Control+Enter');
  await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='draft').length===2);
  await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.draftRequest.requestId}));await submitted(page);
  await page.keyboard.press('Control+Enter');
  check((await messages(page)).filter(m=>m.action==='queue').length===1,'Repeated Ctrl+Enter cannot duplicate a pending submission');
  await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.queueRequest.requestId,error:'Outbox save failed.'}));
  await page.waitForFunction(()=>document.querySelector('#error').textContent==='Outbox save failed.');
  check(await page.locator('#reflection-text').isEditable()&&await page.locator('#early-reason').inputValue()==='Final reason','Failed Outbox save restores editing and keeps both fields for retry');
  await page.keyboard.press('Control+Enter');
  await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='queue').length===2);
  const sent=await messages(page),retry=sent.filter(m=>m.action==='queue')[1];
  check(retry.data.text==='Final keystrokes'&&retry.data.reason==='Final reason'&&retry.data.endSession===true&&sent.filter(m=>m.action==='draft').length===2,'Retry retains Ctrl+Enter ending intent and the latest durable fields without rewriting an unchanged draft');
  await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.queueRequest.requestId}));
  await page.keyboard.press('Control+Enter');
  check((await messages(page)).filter(m=>m.action==='queue').length===2,'Successful send remains guarded until the native window closes or loads another reflection');
  await page.close();

  const clicked=await open('check-in');await clicked.locator('#reflection-form button[type=submit]').click();await submitted(clicked);
  check((await messages(clicked)).find(m=>m.action==='queue').data.endSession===false,'The Save & send button keeps its existing non-ending check-in behavior');
  await clicked.close();

  const blank=await open();
  await blank.locator('#reflection-text').fill('   ');await blank.locator('#skip-reflection').focus();await blank.keyboard.press('Control+Enter');
  await blank.waitForFunction(()=>document.querySelector('#reflection-text').getAttribute('aria-invalid')==='true');
  check(await blank.locator('#reflection-text').evaluate(e=>e===document.activeElement)&&!(await messages(blank)).some(m=>['queue','skip'].includes(m.action)),'Blank Ctrl+Enter focuses the required response instead of sending or activating Skip');
  await blank.close();
  const guarded=await open();
  await guarded.locator('#reflection-form button[type=submit]').focus();
  for(const key of ['Control+Alt+Enter','Control+Shift+Enter','Alt+Shift+Enter'])await guarded.keyboard.press(key);
  await guarded.locator('#reflection-text').dispatchEvent('keydown',{key:'Enter',altKey:true,isComposing:true,bubbles:true});
  check(!(await messages(guarded)).some(m=>m.action==='queue'),'Other modifier combinations and composition cannot accidentally send or end a session');
  await guarded.evaluate(()=>{const dialog=document.createElement('dialog');document.body.append(dialog);dialog.showModal();});
  await guarded.keyboard.press('Control+Enter');await guarded.keyboard.press('Alt+Enter');
  check(!(await messages(guarded)).some(m=>m.action==='queue'),'An open modal retains Ctrl+Enter instead of submitting the reflection behind it');
  await guarded.evaluate(()=>{document.querySelector('dialog[open]').close();window.previewDispatch({type:'flush',freeze:true});});
  await guarded.locator('#later').focus();await guarded.keyboard.press('Control+Enter');await guarded.keyboard.press('Alt+Enter');
  check(!(await messages(guarded)).some(m=>m.action==='queue'),'Window-wide Ctrl+Enter respects automatic replacement and navigation freezes');
  await guarded.close();
  for(const [view,initialize] of [['main',true],['compact',true],['reflection',false]]){
    const other=await open('early',view,initialize);await other.keyboard.press('Control+Enter');await other.keyboard.press('Alt+Enter');
    check(!(await messages(other)).some(m=>m.action==='queue'),`Ctrl+Enter does not send from ${initialize?view:'an uninitialized reflection'}`);
    await other.close();
  }
};
