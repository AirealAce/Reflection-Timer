// Synthetic bridge only; never dismisses a real prompt or writes to Sheets.
module.exports=async function reflectionDismiss(context,initial,check){
  async function open({kind='early',text='',reason='',view='reflection',initialize=true}={}){
    const page=await context.newPage();
    await page.goto('https://reflection-timer.invalid/index.html?view='+view);
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    if(initialize)await page.evaluate(state=>window.previewDispatch({type:'init',state,promptId:'dismiss-test'}),{
      ...initial,prompts:['older','dismiss-test','newer'].map(id=>({id,isCheckIn:kind==='check-in',endedEarly:kind==='early',showEarlyEndReason:kind!=='natural',
        draft:text,earlyEndReason:reason,actual:'2 minutes',allotted:'15 minutes',completed:'Today'}))
    });
    return page;
  }
  const actions=page=>page.evaluate(()=>window.previewMessages.filter(m=>['skip','queue','saveForLater','saveOrSendReflection','navigateReflection','toggle','end'].includes(m.action)));
  async function focus(page,target){
    if(target==='background')await page.evaluate(()=>document.activeElement.blur());
    else await page.locator(target).focus();
  }
  const targets=['#reflection-text','#early-reason','#reflection-heading','#later','#skip-reflection','#reflection-prev','#reflection-next','#reflection-form button[type=submit]','background'];
  for(const kind of ['early','natural','check-in'])for(const key of ['Alt+Enter','Escape']){
    for(const target of kind==='early'?targets:['background']){
      const page=await open({kind});await focus(page,target);await page.keyboard.press(key);
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='skip'));
      const sent=await actions(page);
      check(sent.length===1&&sent[0].action==='skip'&&sent[0].data.id==='dismiss-test',`${kind} empty ${key} skips once from ${target} without sending or changing the timer`);
      await page.close();
    }
  }
  for(const target of targets){
    const page=await open();
    // Do not wait for the input debounce: Escape must save the final keystrokes.
    await page.evaluate(()=>{
      for(const [id,value] of [['reflection-text','Newest response'],['early-reason','Newest reason']]){
        const input=document.getElementById(id);input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));
      }
    });
    await focus(page,target);await page.keyboard.press('Escape');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveForLater'));
    const all=await page.evaluate(()=>window.previewMessages),sent=await actions(page);
    const saved=all.findIndex(m=>m.action==='draft'&&m.data.text==='Newest response'&&m.data.reason==='Newest reason');
    check(saved>=0&&saved<all.findIndex(m=>m.action==='saveForLater')&&sent.length===1&&sent[0].action==='saveForLater'&&sent[0].data.text==='Newest response'&&sent[0].data.reason==='Newest reason',`Escape from ${target} saves both latest fields before closing without sending`);
    await page.close();
  }
  for(const options of [{text:'Response only'},{reason:'Reason only'},{text:' \n ',reason:'\t'},{kind:'natural',reason:'Retained provisional reason'},{kind:'check-in',text:'Check-in draft'}]){
    const page=await open(options);await page.locator('#skip-reflection').focus();await page.keyboard.press('Escape');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveForLater'));
    const sent=await actions(page);
    check(sent.length===1&&sent[0].action==='saveForLater'&&sent[0].data.text===(options.text||'')&&sent[0].data.reason===(options.reason||''),'Escape preserves any field content, including whitespace or a hidden retained reason');
    await page.close();
  }
  const reasonOnly=await open({reason:'Keep this reason'});await reasonOnly.keyboard.press('Alt+Enter');
  await reasonOnly.waitForFunction(()=>document.querySelector('#reflection-text').getAttribute('aria-invalid')==='true');
  check((await actions(reasonOnly)).length===0&&await reasonOnly.locator('#early-reason').inputValue()==='Keep this reason','Reason-only Alt+Enter retains the existing required-response validation and never skips the reason');
  await reasonOnly.close();

  for(const key of ['Alt+Enter','Escape']){
    const page=await open();
    await page.evaluate(()=>{
      const normal=window.chrome.webview.postMessage;
      window.chrome.webview.postMessage=message=>{
        if(message.action!=='skip')return normal(message);
        window.previewMessages.push(message);window.pendingSkip=message;
      };
    });
    await page.keyboard.press(key);await page.waitForFunction(()=>window.pendingSkip);
    await page.keyboard.press('Control+Enter');await page.keyboard.press('Escape');
    await page.keyboard.down('Escape');await page.keyboard.down('Escape');await page.keyboard.up('Escape');
    check((await actions(page)).length===1&&await page.locator('#reflection-text').evaluate(e=>e.readOnly),`${key} skip blocks repeated or competing keys while persistence is pending`);
    await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.pendingSkip.requestId,error:'Skip storage failed.'}));
    await page.waitForFunction(()=>document.querySelector('#error').textContent==='Skip storage failed.');
    check(await page.locator('#reflection-text').isEditable()&&await page.locator('#later').getAttribute('aria-disabled')==='false','Failed skip restores the prompt controls for retry');
    await page.keyboard.press(key);await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='skip').length===2);
    await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.pendingSkip.requestId}));
    await page.keyboard.press('Escape');await page.keyboard.press('Control+Enter');
    check((await actions(page)).length===2,'Successful skip stays guarded until the native window closes');
    await page.close();
  }
  const saving=await open({text:'Keep this response'});
  await saving.evaluate(()=>{
    const normal=window.chrome.webview.postMessage;
    window.chrome.webview.postMessage=message=>{
      if(message.action!=='saveForLater')return normal(message);
      window.previewMessages.push(message);window.pendingSave=message;
    };
  });
  await saving.keyboard.press('Escape');await saving.waitForFunction(()=>window.pendingSave);
  await saving.keyboard.press('Escape');await saving.keyboard.press('Control+Enter');
  check((await actions(saving)).length===1,'Pending Escape save cannot become a send or a second save');
  await saving.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.pendingSave.requestId,error:'Save storage failed.'}));
  await saving.waitForFunction(()=>document.querySelector('#error').textContent==='Save storage failed.');
  check(await saving.locator('#reflection-text').isEditable()&&await saving.locator('#reflection-text').inputValue()==='Keep this response','Failed Escape save leaves the response editable and intact');
  await saving.close();
  const guarded=await open();
  await guarded.evaluate(()=>{const modal=document.createElement('dialog');document.body.append(modal);modal.showModal();});
  await guarded.keyboard.press('Control+Enter');await guarded.keyboard.press('Escape');
  check((await actions(guarded)).length===0,'Escape dismisses an open modal without skipping the reflection behind it');
  await guarded.evaluate(()=>window.previewDispatch({type:'flush',freeze:true}));
  await guarded.keyboard.press('Escape');await guarded.keyboard.press('Control+Enter');
  check((await actions(guarded)).length===0,'Empty shortcuts cannot interfere with a frozen reflection handoff');
  await guarded.close();
  for(const options of [{initialize:false},{view:'main'},{view:'compact'}]){
    const page=await open(options);await focus(page,'background');await page.keyboard.press('Escape');await page.keyboard.press('Control+Enter');
    check((await actions(page)).length===0,`Reflection dismissal keys ignore ${options.view||'an uninitialized prompt'}`);
    await page.close();
  }
};
