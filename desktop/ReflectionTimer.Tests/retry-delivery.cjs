// Synthetic delivery state only; no profile, CSV, or Google Sheet is used.
module.exports=async function retryDelivery(context,initial,check,settings){
  const failure={id:'failed',saved:'10/7/2026 11:00 AM',destination:'Synthetic CSV',status:'Sent',attempts:0,
    localOnly:false,wantsSheets:false,complete:false,csvStatus:'NeedsReview',csvError:'csv_locked',deliveryFailed:true,
    retryReviewRequired:false,message:'Saved failed message.',duration:'15 minutes'};
  async function open(view,outbox=[],prompt){
    const page=await context.newPage();
    if(view==='reflection')await page.setViewportSize({width:544,height:401});
    await page.goto('https://reflection-timer.invalid/index.html?view='+view);
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(({initial,settings,outbox,prompt})=>{
      window.retryState={...initial,outbox,prompts:prompt?[prompt]:[]};
      window.previewDispatch({type:'init',state:window.retryState,promptId:prompt?.id});
      window.previewDispatch({type:'settings',settings});
    },{initial,settings,outbox,prompt});
    return page;
  }
  const page=await open('main');await page.locator('#tab-outbox').click();
  const retry=page.locator('#retry-all');
  check(await retry.getAttribute('aria-disabled')==='true','Retry all is unavailable without failed deliveries');
  await page.evaluate(failure=>{
    window.retryState.outbox=[{...failure,id:'never-attempted',csvStatus:'Pending',deliveryFailed:false}];
    window.previewDispatch({type:'state',state:window.retryState});
  },failure);
  check(await retry.getAttribute('aria-disabled')==='true','Initial pending messages do not enable Retry all');
  await page.evaluate(failure=>{
    window.retryState.outbox=[failure];window.previewDispatch({type:'state',state:window.retryState});
    const original=window.chrome.webview.postMessage;
    window.chrome.webview.postMessage=message=>{
      if(message.action==='retryAll')window.previewDispatch({type:'retryAllState',busy:true});
      original(message);
    };
  },failure);
  await retry.click();await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='retryAll'));
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='retryAll').length===1&&window.previewMessages.find(m=>m.action==='retryAll').data.confirmed===false),'Ordinary failures retry directly using the native batch action');
  check(await page.locator('#delivery-dialog').isHidden(),'Ordinary retry does not show an unnecessary review dialog');
  check(await retry.getAttribute('aria-disabled')==='true'&&await retry.getAttribute('aria-busy')==='true','Native batch progress keeps Retry all unavailable after the request is accepted');
  await retry.evaluate(button=>button.click());check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='retryAll').length===1),'Repeated clicks cannot start another batch during delivery');
  await page.evaluate(()=>window.previewDispatch({type:'retryAllState',busy:false}));
  check(await retry.getAttribute('aria-disabled')==='false'&&await retry.getAttribute('aria-busy')==='false','Completing a batch restores Retry all when a failure remains');
  await page.evaluate(()=>{window.retryState.outbox=[];window.previewDispatch({type:'state',state:window.retryState});});
  check(await retry.getAttribute('aria-disabled')==='true','Resolving all failures disables Retry all');
  await page.evaluate(failure=>{
    window.retryState.outbox=[{...failure,wantsSheets:true,status:'NeedsReview',retryReviewRequired:true}];
    window.previewDispatch({type:'state',state:window.retryState});
  },failure);
  await retry.click();await page.locator('#delivery-dialog').waitFor({state:'visible'});
  check(await page.locator('#delivery-title').evaluate(e=>document.activeElement===e),'Batch review focuses its accessible dialog heading');
  check(await page.locator('#delivery-explanation').textContent().then(t=>t.includes('only failed destinations')&&t.includes('duplicate')),'Uncertain batch delivery explains its destination scope and duplicate risk');
  await page.keyboard.press('Escape');
  check(await page.locator('#delivery-dialog').isHidden()&&await retry.evaluate(e=>document.activeElement===e),'Escape cancels batch review and returns focus to Retry all');
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='retryAll').length===1),'Cancelled review sends no batch action');
  await retry.click();await page.locator('#delivery-confirm').click();
  await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='retryAll').length===2);
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='retryAll')[1].data.confirmed===true),'Approved batch review explicitly confirms the native retry');
  const ax=await context.newCDPSession(page),tree=await ax.send('Accessibility.getFullAXTree');
  check(tree.nodes.some(n=>!n.ignored&&n.role?.value==='button'&&n.name?.value==='Retry all'),'Retry all is exposed as a named screen-reader button');
  await page.close();

  const conflict=await open('main',[{...failure,csvError:'csv_conflict',retryReviewRequired:true}]);
  await conflict.locator('#tab-outbox').click();
  await conflict.locator('#outbox-rows tr').click();await conflict.locator('#retry-selected').click();
  check(await conflict.locator('#delivery-dialog').isVisible()&&await conflict.locator('#delivery-explanation').textContent().then(text=>text.includes('CSV')&&text.includes('new entry ID')),'A CSV conflict keeps review before retrying a single destination');
  check(await conflict.evaluate(()=>!window.previewMessages.some(message=>message.action==='retry')),'CSV conflict review cannot silently send a replacement');
  await conflict.locator('#delivery-confirm').click();await conflict.waitForFunction(()=>window.previewMessages.some(message=>message.action==='retry'));
  check(await conflict.evaluate(()=>window.previewMessages.find(message=>message.action==='retry').data.confirmed===true),'Confirmed CSV conflict retry explicitly authorizes its replacement identity');
  await conflict.close();

  const prompt={id:'retry-prompt',retryOutboxId:'failed',retryRequiresConfirmation:false,mode:0,isCheckIn:false,
    draft:'Saved failed message.',earlyEndReason:'',actual:'5 minutes',allotted:'15 minutes',completed:'10/7/2026 11:00 AM',
    pauses:[{id:'pause-a',paused:'10:55 AM 10/7/2026',duration:'0:05',reason:'Original pause.'}]};
  for(const shortcut of ['Control+Enter','Alt+s','Alt+Enter']){
    const editor=await open('reflection',[failure],prompt);
    await editor.locator('#reflection-text').fill('Updated unsent message.');
    await editor.locator('#pause-pause-a').fill('Updated pause reason.');
    await editor.keyboard.press(shortcut);
    await editor.waitForFunction(()=>window.previewMessages.some(m=>m.action==='queue'));
    check(await editor.evaluate(()=>{
      const review=window.previewMessages.find(m=>m.action==='reviewRetryReflection'),queue=window.previewMessages.find(m=>m.action==='queue');
      return review?.data.text==='Updated unsent message.'&&review.data.pauseReasons[0].reason==='Updated pause reason.'
        &&queue.data.text===review.data.text&&queue.data.retryConfirmed===false;
    }),shortcut+' checks the edited message with the host and sends the retry in the same editor');
    check(await editor.evaluate(()=>!window.previewMessages.some(m=>m.action==='reflectionSendStarted')),'Retrying a saved reflection does not fade audio for the active session');
    await editor.close();
  }
  const editor=await open('reflection',[failure],{...prompt,retryRequiresConfirmation:true});
  await editor.evaluate(()=>{
    const original=window.chrome.webview.postMessage;
    window.chrome.webview.postMessage=message=>{
      if(message.action==='reviewRetryReflection'){
        window.previewMessages.push(message);
        queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,needsConfirmation:true}));
      }else original(message);
    };
  });
  await editor.keyboard.press('Control+Enter');await editor.locator('#delivery-dialog').waitFor({state:'visible'});
  check(await editor.locator('#reflection-text').getAttribute('readonly')!==null,'Review freezes the retry editor to preserve the approved message');
  await editor.keyboard.press('Control+Enter');await editor.keyboard.press('Escape');
  check(await editor.locator('#reflection-text').evaluate(e=>document.activeElement===e)&&!await editor.locator('#reflection-text').getAttribute('readonly'),'Cancelling review restores editor focus and editing');
  check(await editor.evaluate(()=>!window.previewMessages.some(m=>['queue','skip','saveForLater'].includes(m.action))),'Review shortcuts and Escape cannot send or dismiss the reflection accidentally');
  await editor.keyboard.press('Alt+s');await editor.locator('#delivery-confirm').click();
  await editor.waitForFunction(()=>window.previewMessages.some(m=>m.action==='queue'));
  check(await editor.evaluate(()=>window.previewMessages.filter(m=>m.action==='queue').length===1&&window.previewMessages.find(m=>m.action==='queue').data.retryConfirmed===true),'Approved retry editor confirmation sends one authorized request');
  await editor.close();
};
