// Synthetic state only; no installed profile or real delivery is touched.
module.exports=async function reflectionHeader(context,initial,check,settings){
  const fs=require('node:fs/promises'),path=require('node:path');
  const sideEffects=page=>page.evaluate(()=>window.previewMessages.filter(m=>['queue','skip','saveForLater','saveOrSendReflection','resetAndReload','toggle'].includes(m.action)));
  async function open(mode=0,early=false,theme=0){
    const page=await context.newPage();await page.setViewportSize({width:544,height:early?486:401});
    await page.goto('https://reflection-timer.invalid/index.html?view=reflection');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(({initial,mode,early,theme})=>{
      window.headerState={...initial,theme,outbox:[],prompts:[{id:'header-prompt',mode,isCheckIn:mode===1,endedEarly:early,showEarlyEndReason:early,
        resumeOnSave:mode===1,draft:'Retain this reflection.',earlyEndReason:'',actual:'5 minutes',allotted:'15 minutes',completed:'10/7/2026 11:00 AM'}]};
      window.previewDispatch({type:'init',state:window.headerState,promptId:'header-prompt'});
    },{initial,mode,early,theme});
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='reflectionReady'));
    return page;
  }
  const geometry=page=>page.locator('#reflection-text,#early-reason,.reflection-actions').evaluateAll(nodes=>nodes.filter(n=>n.getClientRects().length).map(n=>{
    const r=n.getBoundingClientRect();return {id:n.id,x:r.x,y:r.y,width:r.width,height:r.height};
  }));
  for(const theme of [0,1,2,3])for(const [mode,early] of [[0,false],[0,true],[1,false]]){
    const page=await open(mode,early,theme),before=await geometry(page);
    const original=await page.evaluate(()=>{
      const row=document.querySelector('.page-title-row'),title=document.querySelector('#page-title');
      row.before(title);row.hidden=true;
      const fields=Array.from(document.querySelectorAll('#reflection-text,#early-reason,.reflection-actions')).filter(n=>n.getClientRects().length).map(n=>{
        const r=n.getBoundingClientRect();return {id:n.id,x:r.x,y:r.y,width:r.width,height:r.height};
      });
      row.prepend(title);row.hidden=false;return fields;
    });
    check(JSON.stringify(before)===JSON.stringify(original),'Header icons preserve the original field and action row geometry: '+theme+'/'+mode+'/'+early);
    const help=page.locator('#reflection-help-toggle'),panel=page.locator('#reflection-shortcuts');
    check(await help.isVisible()&&await page.locator('#reflection-failed').isHidden(),'Only the help icon appears without failed delivery: '+theme+'/'+mode+'/'+early);
    check(await help.evaluate(button=>{
      const title=document.querySelector('#page-title').getBoundingClientRect(),r=button.getBoundingClientRect();
      return r.left-title.right>=6&&r.left-title.right<=10&&r.right<=innerWidth-18;
    }),'Help icon sits immediately after the title within the popup: '+theme+'/'+mode+'/'+early);
    await help.focus();await page.keyboard.press('Space');
    await page.waitForFunction(()=>document.querySelector('#reflection-help-toggle').getAttribute('aria-expanded')==='true');
    check(await panel.isVisible()&&await panel.getAttribute('aria-labelledby')==='reflection-shortcuts-title','Keyboard opens a named readable help region');
    check(JSON.stringify(before)===JSON.stringify(await geometry(page)),'Opening help retains the writing fields and sticky action row geometry');
    check(await help.evaluate(button=>getComputedStyle(button).borderRadius==='50%'&&getComputedStyle(button).outlineStyle==='solid'),'Circled help icon retains theme styling and visible keyboard focus');
    check(await page.locator('#reflection-checkin-key-help').textContent().then(t=>mode===1?t.includes('finish this stopwatch'):t.includes('without ending the timer')),'Shortcut instructions match the reflection mode');
    check(await page.locator('#reflection-save-key-help').textContent().then(t=>t.includes('resume this stopwatch')===(mode===1)),'Save help describes paused stopwatch resumption');
    await page.keyboard.press('Tab');
    check(await panel.evaluate(node=>document.activeElement===node),'Tab moves directly into the readable shortcut region');
    if(process.env.REFLECTION_PREVIEW_SCREENSHOTS&&mode===0&&!early){
      await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});
      await page.screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,'Reflection-help-theme-'+theme+'.png')});
    }
    await page.keyboard.press('Escape');
    await page.waitForFunction(()=>document.querySelector('#reflection-help-toggle').getAttribute('aria-expanded')==='false');
    check(await panel.isHidden()&&await help.evaluate(e=>document.activeElement===e),'Escape closes help and returns focus to its button');
    check((await sideEffects(page)).length===0,'Opening and dismissing help cannot send, skip, reset or close a reflection');
    check(JSON.stringify(before)===JSON.stringify(await geometry(page)),'Closing help retains the original popup layout');
    const ax=await context.newCDPSession(page),tree=await ax.send('Accessibility.getFullAXTree');
    check(tree.nodes.some(n=>!n.ignored&&n.role?.value==='button'&&n.name?.value==='Keyboard shortcuts for the session-end window')
      &&tree.nodes.some(n=>!n.ignored&&n.role?.value==='heading'&&n.name?.value==='How did you spend your time?'),'Reader names describe the help button without changing the heading');
    await page.close();
  }
  const page=await open(),help=page.locator('#reflection-help-toggle'),failed=page.locator('#reflection-failed'),before=await geometry(page);
  await page.evaluate(()=>{window.headerState.outbox=[{id:'old-failed',deliveryFailed:true}];window.previewDispatch({type:'state',state:window.headerState});});
  check(await failed.isVisible()&&await failed.getAttribute('aria-label').then(t=>t.includes('earliest failed message')),'A delivery failure displays a named warning button');
  check(await failed.evaluate(button=>button.getBoundingClientRect().right<document.querySelector('#reflection-help-toggle').getBoundingClientRect().left),'Warning is immediately to the left of help');
  check(JSON.stringify(before)===JSON.stringify(await geometry(page)),'Showing the warning does not move the reflection fields or action row');
  if(process.env.REFLECTION_PREVIEW_SCREENSHOTS)await page.screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,'Reflection-warning.png')});
  await page.locator('#reflection-text').fill('Keep this draft while reviewing delivery.');
  await failed.click();await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='showFailedDelivery'));
  check(await page.evaluate(()=>{
    const sent=window.previewMessages,open=sent.findIndex(m=>m.action==='showFailedDelivery');
    return sent.slice(0,open).some(m=>m.action==='draft'&&m.data.text==='Keep this draft while reviewing delivery.');
  }),'Opening failed delivery saves the current draft first');
  check((await sideEffects(page)).length===0,'Opening Outbox cannot send, skip or retry the current reflection');
  await failed.focus();await page.evaluate(()=>{window.headerState.outbox=[];window.previewDispatch({type:'state',state:window.headerState});});
  check(await failed.isHidden()&&await help.evaluate(e=>document.activeElement===e),'Resolving the last issue removes its warning and keeps keyboard focus usable');
  await help.click();await page.locator('#page-title').click();
  check(await page.locator('#reflection-shortcuts').isHidden(),'Clicking outside help closes it without changing the draft');
  await help.focus();await page.keyboard.press('Space');await page.keyboard.press('Tab');await page.keyboard.press('Tab');
  check(await page.locator('#reflection-shortcuts').isHidden()&&await page.locator('#reflection-text').evaluate(e=>document.activeElement===e),'Tabbing back into the editor closes help and retains the intended input focus');
  await help.click();await page.keyboard.press('Control+Enter');
  await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='queue'));
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='queue').length===1&&window.previewMessages.find(m=>m.action==='queue').data.endSession),'Ctrl+Enter still sends from the help button when help is open');
  await page.close();
  const app=await context.newPage();await app.goto('https://reflection-timer.invalid/index.html?view=main');
  await app.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
  const outbox=[{id:'old-failed',saved:'10/7/2026 8:00 AM',destination:'Synthetic Sheets',status:'NeedsReview',deliveryFailed:true,error:'network',
    localOnly:false,wantsSheets:true,complete:false,csvStatus:'NotRequested',attempts:2,message:'Earlier failed reflection.',duration:'15 minutes'},
    {id:'new-failed',saved:'10/7/2026 9:00 AM',destination:'Synthetic CSV',status:'Sent',deliveryFailed:true,
    localOnly:false,wantsSheets:false,complete:false,csvStatus:'NeedsReview',csvError:'csv_locked',attempts:0,message:'Later failed reflection.',duration:'15 minutes'}];
  await app.evaluate(({initial,settings,outbox})=>{
    const state={...initial,outbox};window.previewDispatch({type:'init',state});window.previewDispatch({type:'settings',settings});
    window.previewDispatch({type:'showOutboxEntry',state,id:'old-failed'});
  },{initial,settings,outbox});
  check(await app.locator('#tab-outbox').getAttribute('aria-selected')==='true'
    &&await app.locator('#outbox-rows tr[data-id="old-failed"] input').isChecked(),'Host navigation opens Outbox and selects the requested failed entry');
  check(await app.locator('#outbox-detail').evaluate(node=>document.activeElement===node)&&await app.locator('#outbox-detail').textContent().then(t=>t.includes('Earlier failed reflection.')),'Navigation focuses the failed message in the readable details region');
  check(await app.locator('#reflection-title-actions').isHidden(),'Reflection header controls do not appear in App view');
  await app.close();
};
