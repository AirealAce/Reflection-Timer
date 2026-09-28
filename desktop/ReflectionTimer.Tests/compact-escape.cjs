// Real browser keys with a synthetic bridge; no production timer or global keys.
module.exports=async function compactEscape(context,initial,check){
  async function open(status='Ready',initialize=true){
    const page=await context.newPage();
    await page.goto('https://reflection-timer.invalid/compact.html');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    if(initialize)await page.evaluate(state=>window.previewDispatch({type:'init',state,timeOnly:false}),{
      ...initial,clock:{seconds:status==='Finished'?0:900,text:'15 minutes',status},
      timer:{...initial.timer,endTime:status==='Running'?123456789:null},durationDraft:['0','13','0']
    });
    return page;
  }
  const actions=page=>page.evaluate(()=>window.previewMessages.filter(m=>!['ready','interfaceReady','compactSize','durationDraft'].includes(m.action)));
  async function trigger(page,click){if(click)await page.locator('#shrink').click();else await page.keyboard.press('Escape');}
  for(const status of ['Ready','Running','Paused','Finished'])for(const click of [false,true]){
    const page=await open(status),before=await page.locator('#visual-clock').textContent();
    await page.locator('#minutes').focus();await trigger(page,click);
    await page.waitForFunction(()=>document.body.dataset.tiny==='true'&&window.previewMessages.some(m=>m.action==='compactSize'&&m.data.tiny));
    check(await page.locator('body').evaluate(e=>e===document.activeElement)&&await page.locator('#minutes').isHidden(),`${status} ${click?'minus':'Escape'} switches to Time-only and releases input focus`);
    check(await page.locator('#visual-clock').textContent()===before&&await page.locator('#minutes').inputValue()==='13'&&(await actions(page)).length===0,`${status} shrink preserves the duration and timer without changing other windows`);
    await trigger(page,click);await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='close'));
    const sent=await actions(page);
    check(sent.length===1&&sent[0].action==='close',`${status} Time-only ${click?'minus':'Escape'} hides only the floating viewer`);
    await page.close();
  }
  for(const target of ['#hours','#seconds','#repeat','#app','#reset','#toggle','#end','#shrink','#expand','#close','#read-time','background']){
    const page=await open();
    if(target==='background')await page.evaluate(()=>document.activeElement.blur());else await page.locator(target).focus();
    await page.keyboard.press('Escape');
    check(await page.locator('body').getAttribute('data-tiny')==='true'&&await page.locator('body').evaluate(e=>e===document.activeElement)&&(await actions(page)).length===0,`Compact Escape from ${target} shrinks without activating the focused control`);
    await page.close();
  }
  const held=await open('Running');await held.locator('#minutes').focus();
  await held.keyboard.down('Escape');await held.keyboard.down('Escape');await held.keyboard.down('Escape');await held.keyboard.up('Escape');
  check(await held.locator('body').getAttribute('data-tiny')==='true'&&(await actions(held)).length===0,'Holding Escape stops at Time-only instead of immediately hiding it');
  await held.keyboard.press('Escape');await held.waitForFunction(()=>window.previewMessages.some(m=>m.action==='close'));
  check((await actions(held)).length===1,'A separate second Escape hides the Time-only viewer');
  await held.evaluate(()=>window.previewDispatch({type:'expandCompact'}));await held.keyboard.press('Escape');
  check(await held.locator('body').getAttribute('data-tiny')==='true'&&await held.locator('body').evaluate(e=>e===document.activeElement)&&(await actions(held)).length===1,'A reused Compact window still shrinks and releases focus after reopening');
  await held.close();
  const modal=await open();
  await modal.evaluate(()=>{const dialog=document.createElement('dialog');document.body.append(dialog);dialog.showModal();});await modal.keyboard.press('Escape');
  check(await modal.locator('body').getAttribute('data-tiny')==='false'&&(await actions(modal)).length===0,'Escape closes a modal without shrinking its underlying Compact viewer');
  await modal.close();
  const loading=await open('Ready',false);await loading.keyboard.press('Escape');
  check((await actions(loading)).length===0,'Escape waits for the Compact viewer to initialize');
  await loading.close();
};
