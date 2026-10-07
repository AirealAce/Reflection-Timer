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
    const footerGeometry=()=>page.locator('main,#reflection-text,.reflection-actions').evaluateAll(nodes=>nodes.map(node=>{
      const r=node.getBoundingClientRect();return {id:node.id,x:r.x,y:r.y,width:r.width,height:r.height};
    }));
    const writingArea=await footerGeometry();
    await page.evaluate(()=>{
      window.previewDispatch({type:'sessionStatus',message:'Time-only view.'});
      window.previewDispatch({type:'announcement',message:'Time reached. 5 minutes elapsed.'});
    });
    await page.waitForFunction(()=>document.querySelector('#status').textContent==='Time reached. 5 minutes elapsed.');
    check(JSON.stringify(writingArea)===JSON.stringify(await footerGeometry())
      &&await page.locator('.messages').evaluate(node=>node.getBoundingClientRect().height===0),
      'Timer/view announcements leave all reclaimed height available to the original response and sticky actions: '+theme+'/'+mode+'/'+early);
    const announced=await ax.send('Accessibility.getFullAXTree'),nodes=new Map(announced.nodes.map(node=>[node.nodeId,node]));
    const includesText=(node,text)=>node.name?.value===text||(node.childIds??[]).some(id=>nodes.has(id)&&includesText(nodes.get(id),text));
    check(announced.nodes.some(node=>!node.ignored&&node.role?.value==='status'&&includesText(node,'Time reached. 5 minutes elapsed.'))
      &&!announced.nodes.some(node=>!node.ignored&&node.name?.value==='Time-only view.'),
      'The hidden announcement remains a readable status while duplicate passive view text is omitted from accessibility: '+theme+'/'+mode+'/'+early);
    check(await page.evaluate(()=>{
      const main=document.querySelector('main'),root=document.documentElement,actions=document.querySelector('.reflection-actions').getBoundingClientRect();
      return main.scrollHeight<=main.clientHeight+1&&root.scrollHeight<=innerHeight+1&&actions.bottom<=innerHeight;
    }),'Hidden reflection announcements create no outer or writing-page overflow: '+theme+'/'+mode+'/'+early);
    await page.evaluate(()=>window.previewDispatch({type:'reflectionCloseFailed',message:'Could not save this reflection. Try again.'}));
    check(await page.locator('#error').isVisible()&&await page.locator('#error').getAttribute('role')==='alert'
      &&await page.locator('#error').textContent()==='Could not save this reflection. Try again.',
      'A genuine reflection save failure remains visible and announced as an alert: '+theme+'/'+mode+'/'+early);
    check(await page.evaluate(()=>{
      const main=document.querySelector('main'),root=document.documentElement,error=document.querySelector('#error').getBoundingClientRect();
      const actions=document.querySelector('.reflection-actions').getBoundingClientRect();
      return error.top>=0&&error.bottom<=innerHeight&&actions.bottom<=error.top&&main.scrollHeight<=main.clientHeight+1&&root.scrollHeight<=innerHeight+1;
    }),'Visible reflection errors fit below the controls without overflowing the popup: '+theme+'/'+mode+'/'+early);
    await page.close();
  }
  const page=await open(),help=page.locator('#reflection-help-toggle'),failed=page.locator('#reflection-failed'),before=await geometry(page);
  await page.evaluate(()=>{window.headerState.outbox=[{id:'old-failed',deliveryFailed:true}];window.previewDispatch({type:'state',state:window.headerState});});
  check(await failed.isVisible()&&await failed.getAttribute('aria-label').then(t=>t.includes('most recent unsent message')),'A delivery failure displays a named warning button');
  check(await failed.evaluate(button=>button.getBoundingClientRect().right<document.querySelector('#reflection-help-toggle').getBoundingClientRect().left),'Warning is immediately to the left of help');
  check(JSON.stringify(before)===JSON.stringify(await geometry(page)),'Showing the warning does not move the reflection fields or action row');
  if(process.env.REFLECTION_PREVIEW_SCREENSHOTS)await page.screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,'Reflection-warning.png')});
  await page.locator('#reflection-text').fill('Keep this draft while reviewing delivery.');
  await failed.click();await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='showFailedDelivery'));
  check(await page.evaluate(()=>{
    const sent=window.previewMessages,open=sent.findIndex(m=>m.action==='showFailedDelivery');
    return sent.slice(0,open).some(m=>m.action==='draft'&&m.data.text==='Keep this draft while reviewing delivery.');
  }),'Opening failed delivery saves the current draft first');
  check((await sideEffects(page)).length===0,'Opening failed delivery cannot send, skip or retry the current reflection');
  await page.evaluate(()=>{
    const retry={...window.headerState.prompts[0],id:'retry-latest',retryOutboxId:'new-failed',draft:'Latest failed reflection.',resumeOnSave:false};
    window.headerState={...window.headerState,prompts:[...window.headerState.prompts,retry]};
    window.previewDispatch({type:'showReflection',state:window.headerState,promptId:retry.id});
  });
  check(await page.locator('#reflection-text').inputValue()==='Latest failed reflection.'&&await page.locator('#reflection-text').evaluate(e=>document.activeElement===e),'Failed delivery opens in the same reflection editor with its text box focused');
  check(await page.locator('#reflection-heading').textContent()==='Unsent reflection'&&await page.locator('#reflection-context').textContent().then(t=>t.includes('undelivered destinations')),'Retry editor describes saved delivery without implying the active session ends');
  check(await page.locator('#reflection-form').getAttribute('aria-description').then(t=>t.includes('undelivered destinations')&&!t.includes('ends this session')),'Retry editor announces destination-only sending instead of active-session commands');
  check(await page.locator('#reflection-form button[type=submit]').getAttribute('title').then(t=>t.includes('undelivered destinations')&&!t.includes('end this session')),'Retry send tooltip describes the saved message action');
  check(await page.locator('#reflection-send-key-help').textContent().then(t=>t.includes('undelivered destinations')),'Retry help explains that successful destinations are kept');
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
  },{initial,settings,outbox});
  check(await app.locator('#reflection-title-actions').isHidden(),'Reflection header controls do not appear in App view');
  await app.close();
};
