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
  const submitted=(page,action='queue')=>page.waitForFunction(action=>window.previewMessages.some(m=>m.action===action),action);
  async function focus(page,target){
    if(target==='background')await page.evaluate(()=>document.activeElement.blur());
    else await page.locator(target).focus();
  }
  for(const kind of ['natural','early','check-in']){
    const targets=['#reflection-text','#reflection-heading','#later','#skip-reflection','#reflection-prev','#reflection-next','#reflection-form button[type=submit]','background'];
    if(kind!=='natural')targets.push('#early-reason');
    for(const target of targets)for(const key of ['Control+Enter','Alt+s','Alt+Enter','Control+s']){
      const page=await open(kind);
      const action=key==='Control+s'?'saveForLater':'queue',endSession=key==='Control+Enter'||key==='Alt+s';
      await focus(page,target);await page.keyboard.press(key);await submitted(page,action);
      const sent=await messages(page),entry=sent.filter(m=>m.action===action);
      check(entry.length===1&&entry[0].data.id==='submit-test'&&entry[0].data.text==='Saved response'&&entry[0].data.reason==='Saved reason'&&(action!=='queue'||entry[0].data.endSession===endSession),`${kind} ${key} delegates the current fields to ${action} with its intended save/send mode from ${target}`);
      check(!sent.some(m=>['skip','navigateReflection','toggle','end','checkIn',action==='queue'?'saveForLater':'queue'].includes(m.action)),`${kind} ${key} overrides the focused action without a separate unscoped timer command`);
      await page.close();
    }
  }

  const page=await open();
  await page.evaluate(()=>{
    const normal=window.chrome.webview.postMessage;
    window.chrome.webview.postMessage=message=>{
      if(!['draft','queue'].includes(message.action))return normal(message);
      window.previewMessages.push(message);window[message.action==='draft'?'draftRequest':'queueRequest']=message;
    };
    for(const [id,value] of [['reflection-text','Final keystrokes'],['early-reason','Final reason']]){
      const input=document.getElementById(id);input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));
    }
  });
  await page.locator('#later').focus();await page.keyboard.press('Control+Enter');
  await page.waitForFunction(()=>window.draftRequest);
  await page.keyboard.down('Control');await page.keyboard.down('Enter');await page.keyboard.down('Enter');await page.keyboard.up('Enter');await page.keyboard.up('Control');
  await page.keyboard.press('Alt+Enter');await page.keyboard.press('Alt+s');await page.keyboard.press('Control+s');
  check((await messages(page)).filter(m=>m.action==='draft').length===1&&!(await messages(page)).some(m=>['queue','saveForLater'].includes(m.action))&&await page.locator('#reflection-text').evaluate(e=>e.readOnly),'Repeated Ctrl+Enter waits for one durable draft save and freezes edits');
  await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.draftRequest.requestId,error:'Draft save failed.'}));
  await page.waitForFunction(()=>document.querySelector('#error').textContent==='Draft save failed.');
  check(await page.locator('#reflection-text').isEditable()&&await page.locator('#reflection-text').inputValue()==='Final keystrokes'&&!(await messages(page)).some(m=>['queue','saveForLater'].includes(m.action)),'Failed draft save restores editing and retains the response without sending');
  await page.keyboard.press('Control+Enter');
  await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='draft').length===2);
  await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.draftRequest.requestId}));await submitted(page);
  await page.keyboard.press('Control+Enter');
  check((await messages(page)).filter(m=>m.action==='queue').length===1,'Repeated Ctrl+Enter cannot duplicate a pending save/send');
  await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.queueRequest.requestId,error:'Outbox save failed.'}));
  await page.waitForFunction(()=>document.querySelector('#error').textContent==='Outbox save failed.');
  check(await page.locator('#reflection-text').isEditable()&&await page.locator('#early-reason').inputValue()==='Final reason','Failed Outbox save restores editing and keeps both fields for retry');
  await page.keyboard.press('Control+Enter');
  await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='queue').length===2);
  const sent=await messages(page),retry=sent.filter(m=>m.action==='queue')[1];
  check(retry.data.text==='Final keystrokes'&&retry.data.reason==='Final reason'&&retry.data.endSession===true&&sent.filter(m=>m.action==='draft').length===2,'Retry rechecks the corresponding session using the latest durable fields with its session-bound early-ending request');
  await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.queueRequest.requestId}));
  await page.keyboard.press('Control+Enter');
  check((await messages(page)).filter(m=>m.action==='queue').length===2,'Successful save/send remains guarded until the native window closes or loads another reflection');
  await page.close();

  const clicked=await open('check-in');await clicked.locator('#reflection-form button[type=submit]').click();await submitted(clicked,'queue');
  check((await messages(clicked)).find(m=>m.action==='queue').data.endSession===false,'The Save & send button keeps its existing non-ending check-in behavior');
  await clicked.close();

  const blank=await open();
  await blank.evaluate(()=>{
    const normal=window.chrome.webview.postMessage;
    window.chrome.webview.postMessage=message=>{
      if(message.action!=='queue')return normal(message);
      window.previewMessages.push(message);
      queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:'Write a reflection between 1 and 5,000 characters.'}));
    };
  });
  await blank.locator('#reflection-text').fill('   ');await blank.locator('#skip-reflection').focus();await blank.keyboard.press('Control+Enter');
  await blank.waitForFunction(()=>document.querySelector('#reflection-text').getAttribute('aria-invalid')==='true');
  check(await blank.locator('#reflection-text').evaluate(e=>e===document.activeElement)&&await blank.locator('#reflection-text').isEditable()&&!(await messages(blank)).some(m=>['queue','skip'].includes(m.action)),'Whitespace response with a retained reason focuses the response on native validation failure instead of activating Skip');
  await blank.close();
  for(const reason of ['', 'Reason without response']){
    const active=await open('check-in');await active.locator('#reflection-text').fill('');await active.locator('#early-reason').fill(reason);
    await active.keyboard.press('Control+Enter');if(reason==='')await submitted(active,'skip');else await active.waitForFunction(()=>document.querySelector('#reflection-text').getAttribute('aria-invalid')==='true');
    const requests=await messages(active);
    check(reason===''?requests.some(m=>m.action==='skip')&&!requests.some(m=>m.action==='queue'):!requests.some(m=>['queue','skip','saveForLater'].includes(m.action))&&await active.locator('#early-reason').inputValue()===reason,'Ctrl+Enter skips empty fields and validates a reason-only draft without losing it');
    await active.close();
  }
  for(const reason of ['', 'Reason without response']){
    const saved=await open('check-in');await saved.locator('#reflection-text').fill('');await saved.locator('#early-reason').fill(reason);
    await saved.locator('#skip-reflection').focus();await saved.keyboard.press('Control+s');await submitted(saved,'saveForLater');
    const requests=await messages(saved);
    check(requests.some(m=>m.action==='saveForLater'&&m.data.text===''&&m.data.reason===reason)&&!requests.some(m=>['queue','skip'].includes(m.action)),'Ctrl+S saves an empty or reason-only draft from another button without skipping or sending');await saved.close();
  }
  const saved=await open();
  await saved.evaluate(()=>{
    const normal=window.chrome.webview.postMessage;window.chrome.webview.postMessage=message=>{
      if(message.action!=='saveForLater')return normal(message);
      window.previewMessages.push(message);window.saveRequest=message;
    };
  });
  await saved.keyboard.press('Control+s');await saved.waitForFunction(()=>window.saveRequest);
  for(const key of ['Control+s','Alt+s','Control+Enter','Escape'])await saved.keyboard.press(key);
  check((await messages(saved)).filter(m=>['saveForLater','skip','queue'].includes(m.action)).length===1,'Ctrl+S shares the save/close guard with every send and dismiss shortcut');
  await saved.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.saveRequest.requestId,error:'Local save failed.'}));
  await saved.waitForFunction(()=>document.querySelector('#error').textContent==='Local save failed.');
  check(await saved.locator('#reflection-text').isEditable()&&await saved.locator('#early-reason').inputValue()==='Saved reason','A failed Ctrl+S save retains both editable fields');
  await saved.keyboard.press('Control+s');await saved.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='saveForLater').length===2);
  await saved.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.saveRequest.requestId}));await saved.close();
  const guarded=await open();
  await guarded.locator('#reflection-form button[type=submit]').focus();
  for(const key of ['Control+Alt+Enter','Control+Shift+Enter','Alt+Shift+Enter','Control+Alt+s','Control+Shift+s','Alt+Shift+s'])await guarded.keyboard.press(key);
  await guarded.locator('#reflection-text').dispatchEvent('keydown',{key:'Enter',altKey:true,isComposing:true,bubbles:true});
  check(!(await messages(guarded)).some(m=>['queue','saveForLater'].includes(m.action)),'Other modifier combinations and composition cannot accidentally save or send a session');
  await guarded.evaluate(()=>{const dialog=document.createElement('dialog');document.body.append(dialog);dialog.showModal();});
  for(const key of ['Control+Enter','Alt+Enter','Alt+s','Control+s'])await guarded.keyboard.press(key);
  check(!(await messages(guarded)).some(m=>['queue','saveForLater'].includes(m.action)),'An open modal retains Ctrl+Enter instead of submitting the reflection behind it');
  await guarded.evaluate(()=>{document.querySelector('dialog[open]').close();window.previewDispatch({type:'flush',freeze:true});});
  await guarded.locator('#later').focus();for(const key of ['Control+Enter','Alt+Enter','Alt+s','Control+s'])await guarded.keyboard.press(key);
  check(!(await messages(guarded)).some(m=>['queue','saveForLater'].includes(m.action)),'Window-wide Ctrl+Enter respects automatic replacement and navigation freezes');
  await guarded.close();
  for(const [view,initialize] of [['main',true],['compact',true],['reflection',false]]){
    const other=await open('early',view,initialize);for(const key of ['Control+Enter','Alt+Enter','Alt+s','Control+s'])await other.keyboard.press(key);
    check(!(await messages(other)).some(m=>['queue','saveForLater'].includes(m.action)),`Ctrl+Enter does not save/send from ${initialize?view:'an uninitialized reflection'}`);
    await other.close();
  }
};
