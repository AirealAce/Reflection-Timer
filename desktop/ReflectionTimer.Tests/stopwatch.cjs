// Real browser views with synthetic state; no production profile or network writes.
module.exports=async function stopwatch(context,initial,settings,check){
  for(const view of ['main','compact']){
    const page=await context.newPage();
    await page.goto('https://reflection-timer.invalid/'+(view==='compact'?'compact.html':'index.html?view=main'));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    const state={...initial,durationDraft:['invalid','',''],timer:{...initial.timer,mode:1},clock:{seconds:125,text:'2 minutes 5 seconds',status:'Paused',stopwatch:true}};
    await page.evaluate(state=>window.previewDispatch({type:'init',state}),state);
    check(await page.locator('#visual-clock').textContent()==='2:05'&&await page.locator('#hours').isHidden(),view+' shows stopwatch elapsed time, hiding countdown inputs');
    if(process.env.REFLECTION_PREVIEW_SCREENSHOTS){
      const fs=require('node:fs/promises'),path=require('node:path');await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});
      for(const theme of [0,1,2,3]){
        await page.evaluate(state=>window.previewDispatch({type:'state',state}),{...state,theme});
        await page.locator('body').screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,`Stopwatch-${view}-${theme}.png`)});
      }
    }
    check(await page.locator('#session-mode').textContent()===(view==='compact'?'T':'Stopwatch · switch to Timer'),view+' mode toggle offers Timer while in Stopwatch');
    if(view==='compact')check(await page.locator('#session-mode').evaluate(e=>e.nextElementSibling.id==='shrink'),'Compact places S/T immediately left of the minus button');
    await page.locator('#toggle').click();
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='toggle'));
    check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').at(-1).data.seconds===undefined),view+' resumes Stopwatch without validating a parked countdown draft');
    const running={...state,clock:{...state.clock,status:'Running'}};
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),running);
    await page.evaluate(()=>window.previewDispatch({type:'clock',clock:{seconds:126,text:'2 minutes 6 seconds',status:'Running',stopwatch:true}}));
    check(await page.locator('#visual-clock').textContent()==='2:06',view+' stopwatch ticks upward');
    await page.evaluate(()=>window.previewDispatch({type:'clock',clock:{seconds:17,text:'17 seconds',status:'Running'}}));
    check(await page.locator('#visual-clock').textContent()==='2:06',view+' ignores a stale countdown frame after switching modes');
    if(view==='compact'){
      check(await page.locator('body').getAttribute('data-tiny')==='true','Running Stopwatch fits the time-only layout');
      await page.locator('#read-time').hover();
      check(await page.locator('#session-mode').evaluate(e=>{const a=e.getBoundingClientRect(),b=document.querySelector('#shrink').getBoundingClientRect();return a.width<=16&&a.right<=b.left;}),'Time-only stopwatch mode control stays small and left of minus');
      const count=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length);
      await page.locator('#read-time').press('Control+Enter');
      await page.waitForFunction(n=>window.previewMessages.filter(m=>m.action==='toggle').length>n,count);
      check(true,'Compact Ctrl+Enter operates Stopwatch as well as Timer');
    }
    await page.locator('#session-mode').click();
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='switchMode'&&m.data.mode===0));
    check(true,view+' mode button sends an explicit Timer selection');
    if(view==='main'){
      await page.evaluate(settings=>window.previewDispatch({type:'settings',settings:{...settings,timeReachedEnabled:true,timeReachedSeconds:300}}),settings);
      check(await page.locator('#timer-time-reached-seconds').inputValue()==='300','Time-reached threshold defaults to 300 seconds');
      await page.locator('#timer-time-reached-seconds').fill('420');
      await page.locator('#timer-time-reached-seconds').press('Enter');
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='timeReached'&&m.data.seconds===420));
      check(await page.locator('#settings-time-reached-seconds').inputValue()==='420','Stopwatch threshold edits mirror into Settings and save on leaving the input');
    }
    await page.close();
  }
};
