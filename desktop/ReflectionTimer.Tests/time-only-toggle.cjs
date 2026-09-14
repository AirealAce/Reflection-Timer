// Real controls with synthetic replies; never operates the installed timer.
module.exports=async function timeOnlyToggle(context,initial,check){
  const page=await context.newPage();
  await page.goto('https://reflection-timer.invalid/compact.html');
  await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
  await page.evaluate(initial=>{
    window.tinyState=structuredClone(initial);
    const normal=window.chrome.webview.postMessage;
    window.chrome.webview.postMessage=message=>{
      if(message.action!=='toggle')return normal(message);
      window.previewMessages.push(message);
      window.finishTinyToggle=()=>{
        if(window.failTinyToggle){window.previewDispatch({type:'reply',requestId:message.requestId,error:'Test save failed.'});return;}
        const state=window.tinyState,running=state.clock.status==='Running';
        state.clock.status=running?'Paused':'Running';state.timer.endTime=running?null:123456789;
        window.previewDispatch({type:'state',state:structuredClone(state),keepTimeOnly:message.data.keepTimeOnly});
        window.previewDispatch({type:'reply',requestId:message.requestId});
      };
      if(!window.holdTinyToggle)queueMicrotask(window.finishTinyToggle);
    };
    window.previewDispatch({type:'init',state:structuredClone(initial)});
  },initial);
  const toggle=page.locator('#toggle');
  for(const theme of [0,1,2,3]){
    await page.evaluate(theme=>{window.tinyState.theme=theme;window.previewDispatch({type:'state',state:structuredClone(window.tinyState)});window.previewDispatch({type:'expandCompact'});},theme);
    const colors=await toggle.evaluate(e=>{const s=getComputedStyle(e);return [s.backgroundColor,s.color,s.borderColor];});
    await page.locator('#shrink').click();await page.locator('#read-time').focus();await page.mouse.move(400,300);
    check(await page.locator('#timer-editor').evaluate(e=>getComputedStyle(e).opacity==='0')&&await page.locator('body').evaluate(e=>e.offsetWidth===96&&e.offsetHeight===40),'Time-only transport hides without hover/focus and keeps the 96 by 40 footprint: theme '+theme);
    await page.locator('#read-time').hover();
    check(await page.locator('#timer-editor').evaluate(e=>getComputedStyle(e).opacity==='1')&&await toggle.evaluate((e,colors)=>{const s=getComputedStyle(e),r=e.getBoundingClientRect(),b=document.body.getBoundingClientRect(),top=document.querySelector('#close').getBoundingClientRect();return JSON.stringify([s.backgroundColor,s.color,s.borderColor])===JSON.stringify(colors)&&r.width===top.width&&r.height===top.height&&r.left===b.left+2&&r.bottom===b.bottom-2;},colors),'Hover shows a corner-sized bottom-left button in the regular transport colors: theme '+theme);
    check(await page.locator('.footer button:visible').count()===1&&await page.getByRole('spinbutton').count()===0,'Time-only reveals only its transport button from the compact controls');
    if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){const fs=require('node:fs/promises'),path=require('node:path');await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});await page.locator('body').screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,'Time-only-hover-'+theme+'.png')});}
    await page.mouse.move(400,300);await page.locator('#read-time').focus();await page.keyboard.press('Tab');
    check(await toggle.evaluate(e=>e===document.activeElement)&&await page.locator('#timer-editor').evaluate(e=>getComputedStyle(e).opacity==='1'),'Tab reveals and focuses the time-only transport without hovering');
  }
  await page.emulateMedia({forcedColors:'active'});await page.locator('#read-time').hover();
  check(await toggle.evaluate(e=>{const s=getComputedStyle(e);return s.backgroundColor!==s.color&&s.forcedColorAdjust==='none';}),'Time-only transport retains contrasting system colors in Windows contrast mode');
  await page.emulateMedia({forcedColors:'none'});
  for(const [action,label] of [['click','Pause timer'],['Space','Resume timer'],['Enter','Pause timer'],['Control+Enter','Resume timer']]){
    const count=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length);
    if(action==='click'){await page.locator('#read-time').hover();await toggle.click();}else await toggle.press(action);
    await page.waitForFunction(label=>document.querySelector('#toggle').getAttribute('aria-label')===label,label);
    check(await page.locator('body').getAttribute('data-tiny')==='true'&&await toggle.evaluate(e=>e===document.activeElement)&&await page.evaluate(count=>window.previewMessages.filter(m=>m.action==='toggle').length===count+1&&window.previewMessages.filter(m=>m.action==='toggle').at(-1).data.keepTimeOnly===true,count),'Time-only '+action+' toggles once, retains focus and keeps the small view');
  }
  await page.evaluate(()=>{window.holdTinyToggle=true;window.failTinyToggle=true;});
  await toggle.click();await toggle.press('Space');await toggle.press('Enter');
  const beforeRetry=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length);
  await page.evaluate(()=>window.finishTinyToggle());await page.waitForFunction(()=>document.querySelector('#error').textContent==='Test save failed.');
  check(beforeRetry===5&&await page.locator('body').getAttribute('data-tiny')==='true','Pending time-only button clicks and competing keys share the existing save guard; failure keeps time-only');
  await page.evaluate(()=>{window.holdTinyToggle=false;window.failTinyToggle=false;});await toggle.press('Enter');
  await page.waitForFunction(()=>document.querySelector('#toggle').getAttribute('aria-label')==='Pause timer');
  check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length===6&&!window.previewMessages.some(m=>['end','reset','dragCompact','queue'].includes(m.action))),'Time-only transport retries after failure without dragging, ending, resetting or sending');
  await page.close();
};
