// Focus messages shared by period and apostrophe shortcuts; synthetic state only.
module.exports=async function timerFocus(context,initial,settings,check){
  for(const view of ['main','compact']){
    const page=await context.newPage();
    await page.goto('https://reflection-timer.invalid/'+(view==='main'?'index.html?view=main':'compact.html'));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(state=>window.previewDispatch({type:'init',state}),initial);
    const focus={type:view==='main'?'focusTimer':'expandCompact',selectTimer:true};
    const target=view==='main'?'playback-toggle':'toggle';
    for(const status of ['Ready','Running','Paused','Finished']){
      const state={...initial,timer:{...initial.timer,mode:1},clock:{seconds:12,text:'12 seconds',status,stopwatch:true}};
      await page.evaluate(({state,focus})=>{window.previewDispatch({type:'state',state});window.previewDispatch(focus);},{state,focus});
      check(await page.locator('#'+target).evaluate(e=>e===document.activeElement)&&await page.locator('#'+target).isVisible(),view+' focuses the visible Stopwatch play button when '+status);
      const before=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='toggle').length);
      await page.keyboard.press('Enter');
      await page.waitForFunction(n=>window.previewMessages.filter(m=>m.action==='toggle').length===n+1,before);
      check(true,view+' Enter operates the focused Stopwatch button when '+status);
    }
    for(const [values,field] of [[['1','20','3'],'hours'],[['0','20','3'],'minutes'],[['0','0','30'],'seconds'],[['0','0','0'],'hours']]){
      await page.evaluate(({state,values,focus})=>{window.previewDispatch({type:'state',state});window.previewDispatch({type:'durationDraft',parts:values});window.previewDispatch(focus);},{state:initial,values,focus});
      check(await page.locator('#'+field).evaluate(e=>e===document.activeElement),view+' Timer focus chooses '+field+' for '+values.join(':'));
      await page.keyboard.insertText('7');
      check(await page.locator('#'+field).inputValue()==='7',view+' focus selects the entire '+field+' value for replacement');
    }
    if(view==='main'){
      await page.getByRole('tab',{name:'Settings',exact:true}).click();
      await page.evaluate(settings=>window.previewDispatch({type:'settings',settings}),settings);
      await page.getByRole('button',{name:'Guided setup / another PC',exact:true}).click();
      const active=await page.evaluate(()=>document.activeElement.id);
      await page.evaluate(focus=>window.previewDispatch(focus),focus);
      check(await page.evaluate(id=>document.activeElement.id===id&&document.body.dataset.tab==='settings',active),'App focus shortcuts retain an open dialog and its selected tab');
    }
    await page.close();
  }
};
