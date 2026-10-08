// Reuses the Focus browser suite's synthetic bridge; no installed profile is read.
module.exports=async(page,check)=>{
  const dialog=page.locator('#focus-target-dialog'),kind=page.locator('#focus-target-kind');
  const open=async()=>{await page.locator('#choose-focus-target-settings').click();await page.waitForFunction(()=>document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false'&&document.querySelector('#focus-target-list tbody tr'));};
  const close=async()=>{await page.locator('#focus-target-cancel').click();await dialog.waitFor({state:'hidden'});};
  const select=async value=>{await kind.selectOption(String(value));await page.waitForFunction(()=>document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');};
  await page.evaluate(()=>{
    delete window.windowListing;delete window.windowReplacements;
    window.settings.focusMode={enabled:false,delaySeconds:5,multipleTargets:true,idleEnabled:false,idleSeconds:20,targets:[]};
    window.dispatchBridge({type:'settings',settings:window.settings});
  });
  await open();check(await kind.inputValue()==='0','A new profile retains the existing Window chooser default');await close();
  await page.evaluate(()=>{
    window.settings.focusMode.targets=[{id:'1-a',key:'1-a',kind:1,name:'Same tab title',app:'chrome',tabPosition:1}];
    window.dispatchBridge({type:'settings',settings:window.settings});
  });
  await open();check(await kind.inputValue()==='1','A legacy profile without a saved picker category still opens its saved target category');await close();
  await page.evaluate(()=>{
    window.settings.focusMode.targets.unshift({id:'0-a',key:'0-a',kind:0,name:'Work window',app:'EXCEL'});
    window.dispatchBridge({type:'settings',settings:window.settings});
  });
  for(const [value,save] of [[2,'button'],[1,'Control+Enter'],[0,'Control+s']]){
    await open();await select(value);
    if(save==='button')await page.locator('#focus-target-use').click();else await kind.press(save);
    await dialog.waitFor({state:'hidden'});
    check(await page.evaluate(value=>window.settings.focusMode.pickerKind===value&&window.settings.focusMode.targets[0].kind===0&&window.settings.focusMode.targets.length===2,value),save+' saves the browsed category independently of selected targets');
    await open();check(await kind.inputValue()===String(value),save+' reopens on the last successfully saved category');await close();
  }
  await open();await select(2);await close();await open();
  check(await kind.inputValue()==='0','Cancel discards a browsed category without changing the saved preference');
  await select(2);await page.evaluate(()=>window.selectionError='Synthetic selection save failure.');await kind.press('Control+s');
  await page.waitForFunction(()=>document.querySelector('#focus-picker-status').textContent.includes('Synthetic selection save failure'));
  check(await dialog.isVisible()&&await page.evaluate(()=>window.settings.focusMode.pickerKind===0),'A failed target save does not replace the saved category');
  await close();await page.evaluate(()=>window.selectionError=null);await open();
  check(await kind.inputValue()==='0','Reopening after a failed save restores the last successful category');
  await select(1);await kind.press('Control+s');await dialog.waitFor({state:'hidden'});
  await page.reload();await page.waitForFunction(()=>window.messages.some(m=>m.action==='ready'));
  await page.evaluate(()=>window.dispatchBridge({type:'settings',settings:window.settings}));
  await page.locator('#tab-settings').click();await open();
  check(await kind.inputValue()==='1'&&await page.evaluate(()=>window.settings.focusMode.targets[0].kind===0),'A new interface instance restores the saved Browser Tab category despite Window being first in its targets');
  await close();
  await open();await select(2);await page.locator('#focus-multiple-targets').uncheck();await kind.press('Control+s');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>!window.settings.focusMode.multipleTargets&&window.settings.focusMode.pickerKind===2&&window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].kind===0),'Returning to a single target can save a different browsed category without changing the kept Window');
  await open();
  check(await kind.inputValue()==='2'&&await page.locator('#focus-target-list tbody tr.picked').count()===0,'Opening the remembered category does not silently select its first row instead of the saved single Window');
  await page.locator('#focus-target-refresh').click();await page.waitForFunction(()=>document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');
  check(await kind.inputValue()==='2'&&await page.locator('#focus-target-list tbody tr.picked').count()===0,
    'Refreshing the remembered category retains the saved single Window from another category');
  await kind.press('Control+s');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.pickerKind===2&&window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].kind===0),'Saving the reopened chooser without choosing a target preserves its existing single Window');
  await open();await select(1);
  check(await page.locator('#focus-target-list tbody tr.picked').count()===1,'An explicit category change still offers an initial single target for keyboard selection');await close();
};
