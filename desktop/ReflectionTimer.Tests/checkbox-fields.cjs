// Exercise real focus and keyboard behavior with synthetic state only.
module.exports=async function checkboxFields(context,initial,settings,check){
  const page=await context.newPage();
  try{
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(({initial,settings})=>{
      window.previewDispatch({type:'init',state:initial});
      window.previewDispatch({type:'settings',settings});
    },{initial,settings});
    for(const [target,tab,field,enabled] of [
      ['timer','Timer','#threshold','#low-time'],
      ['schedule','Scheduler','#schedule-low-threshold','#schedule-low']
    ]){
      await page.getByRole('tab',{name:tab,exact:true}).click();
      const inherit=page.locator('#'+target+'-low-inherit'),input=page.locator(field);
      for(const theme of [0,1,2,3]){
        await page.evaluate(({settings,theme})=>window.previewDispatch({type:'settings',settings:{...settings,threshold:15,theme}}),{settings,theme});
        await inherit.focus();await page.keyboard.press('Tab');
        check(await input.evaluate(e=>document.activeElement===e&&!e.disabled&&e.readOnly)&&await input.inputValue()==='15',
          target+' inherited threshold remains in the Tab order and readable in theme '+theme);
        const before=await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='lowTime').length);
        await page.keyboard.press('ArrowUp');await page.keyboard.press('7');
        check(await input.inputValue()==='15'&&await inherit.isChecked()&&await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='lowTime').length)===before,
          target+' reading an inherited threshold cannot change or save it in theme '+theme);
      }
      check(await input.evaluate(e=>document.getElementById(e.getAttribute('aria-describedby')).textContent.includes('Uncheck Use default threshold')),
        target+' inherited field explains how to enable editing to a screen reader');
      await inherit.uncheck();await input.focus();await page.keyboard.press('ArrowUp');
      check(await input.inputValue()==='16'&&await input.isEditable(),target+' threshold supports arrow editing when inheritance is off');
      await input.fill('27');
      await page.evaluate(({initial,settings})=>{
        window.previewDispatch({type:'state',state:initial});
        window.previewDispatch({type:'settings',settings:{...settings,threshold:20}});
      },{initial,settings});
      check(await input.inputValue()==='27'&&await input.isEditable()&&!await inherit.isChecked(),
        target+' background updates preserve an unfinished custom threshold');
      await inherit.check();
      check(await input.inputValue()==='20'&&await input.evaluate(e=>e.readOnly&&!e.disabled),
        target+' rechecking inheritance immediately displays the default instead of the old override');
      await page.evaluate(settings=>window.previewDispatch({type:'settings',settings:{...settings,threshold:30}}),settings);
      await input.click();
      check(await input.evaluate(e=>document.activeElement===e)&&await input.inputValue()==='30',
        target+' inherited threshold stays mouse-focusable and follows a changed default');
      await page.locator(enabled).uncheck();
      check(await input.isHidden(),target+' disabling low-time audio appropriately hides its unused options');
      await page.locator(enabled).check();await inherit.focus();await page.keyboard.press('Tab');
      check(await input.evaluate(e=>document.activeElement===e&&e.readOnly&&!e.disabled),
        target+' re-enabling low-time audio restores the readable inherited value');
      const cutoff=page.locator(target==='timer'?'#cutoff':'#schedule-cutoff');
      const cutoffEnabled=page.locator(target==='timer'?'#cutoff-enabled':'#schedule-cutoff-enabled');
      await cutoffEnabled.check();await cutoff.focus();
      check(await cutoff.isEditable()&&await cutoff.evaluate(e=>document.activeElement===e),
        target+' auto-start cutoff is focusable and editable when enabled');
      await cutoffEnabled.uncheck();
      check(await cutoff.isDisabled(),target+' cutoff is appropriately disabled when the optional cutoff is off');
    }
    await page.getByRole('tab',{name:'Settings',exact:true}).click();
    for(const kind of [0,1,2,3]){
      const toggle=page.locator('#sound-fade-'+kind),duration=page.locator('#sound-fade-seconds-'+kind);
      await toggle.check();await duration.focus();
      check(await duration.isEditable()&&await duration.evaluate(e=>document.activeElement===e),'Audio '+kind+' fade duration is focusable when enabled');
      await toggle.uncheck();check(await duration.isDisabled(),'Audio '+kind+' fade duration is appropriately disabled when fading is off');
    }
    const fade=page.locator('#sound-message-fade-3'),seconds=page.locator('#sound-message-fade-seconds-3');
    await fade.check();await seconds.focus();
    check(await seconds.isEditable()&&await seconds.evaluate(e=>document.activeElement===e),'Send-fade duration is focusable when enabled');
    await fade.uncheck();check(await seconds.isDisabled(),'Send-fade duration is appropriately disabled when its feature is off');
  }finally{await page.close();}
};
