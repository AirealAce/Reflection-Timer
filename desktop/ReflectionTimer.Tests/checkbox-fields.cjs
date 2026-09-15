// Real browser keyboard events with a synthetic native bridge; no user data.
module.exports=async function checkboxFields(context,initial,settings,check){
  const page=await context.newPage();
  const lowRequests=()=>page.evaluate(()=>window.previewMessages.filter(m=>m.action==='lowTime'));
  try{
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(({initial,settings})=>{
      window.previewDispatch({type:'init',state:initial});window.previewDispatch({type:'settings',settings});
    },{initial,settings});
    check(await page.locator('[id$="-low-inherit"]').count()===0,'There is no separate default/inheritance checkbox');
    for(const [tab,checkbox,field] of [
      ['Timer','#low-time','#threshold'],['Scheduler','#schedule-low','#schedule-low-threshold'],['Settings','#settings-low-time','#default-threshold']
    ]){
      await page.getByRole('tab',{name:tab,exact:true}).click();
      const toggle=page.locator(checkbox),input=page.locator(field);
      check(await page.getByRole('checkbox',{name:'Use threshold',exact:true}).count()===1&&await toggle.isChecked()&&await input.inputValue()==='15',
        tab+' has one checked Use threshold option with 15 seconds initially');
      for(const theme of [0,1,2,3]){
        await page.evaluate(({settings,theme})=>window.previewDispatch({type:'settings',settings:{...settings,theme}}),{settings,theme});
        await toggle.focus();await page.keyboard.press('Tab');
        check(await input.evaluate(e=>document.activeElement===e&&!e.disabled&&!e.readOnly),
          tab+' checked threshold is focusable and editable in theme '+theme);
      }
    }
    check(await page.getByRole('group',{name:'Low on time audio',exact:true}).locator('#settings-low-time').count()===1,
      'The Settings threshold is inside its Low on time audio group');
    await page.locator('#default-threshold').press('ArrowUp');await page.locator('#settings-low-time').focus();
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='lowTime'&&m.data.threshold===16));
    check((await lowRequests()).at(-1).data.enabled&&(await lowRequests()).at(-1).data.inherit===false&&await page.locator('#threshold').inputValue()==='16',
      'Settings arrows save an explicit threshold and mirror Timer');
    check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='saveSound')),
      'Threshold edits do not save or preview a different audio setting');
    await page.getByRole('tab',{name:'Timer',exact:true}).click();
    await page.locator('#threshold').fill('27');
    await page.evaluate(initial=>window.previewDispatch({type:'state',state:initial}),initial);
    check(await page.locator('#threshold').inputValue()==='27'&&await page.locator('#default-threshold').inputValue()==='27',
      'Background updates preserve the latest uncommitted value in both Timer and Settings');
    await page.getByRole('tab',{name:'Settings',exact:true}).click();
    await page.locator('#settings-low-time').uncheck();
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='lowTime'&&!m.data.enabled&&m.data.threshold===27));
    check(!await page.locator('#low-time').isChecked()&&await page.locator('#threshold').isDisabled()&&await page.locator('#default-threshold').isDisabled(),
      'Unchecking Settings turns off the Timer warning and retains its number');
    check(await page.locator('#schedule-low').isChecked()&&await page.locator('#schedule-low-threshold').inputValue()==='15',
      'Turning the Timer warning off leaves Scheduler choices untouched');
    await page.locator('#settings-low-time').check();
    await page.locator('#default-threshold').fill('32');await page.keyboard.press('Control+Enter');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsSaveComplete'));
    check((await lowRequests()).at(-1).data.threshold===32&&(await lowRequests()).at(-1).data.enabled,
      'Ctrl+Enter saves the last typed threshold without requiring a blur');
    await page.getByRole('tab',{name:'Scheduler',exact:true}).click();
    await page.locator('#schedule-low-threshold').fill('45');await page.locator('#schedule-low').uncheck();
    await page.locator('#schedule-save').click();
    const scheduled=await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='schedule').data.lowOptions);
    check(!scheduled.enabled&&!scheduled.inherit&&scheduled.threshold===45&&await page.locator('#threshold').inputValue()==='32'&&await page.locator('#low-time').isChecked(),
      'Scheduler saves its own disabled warning and threshold without changing Timer');
    for(const [tab,toggleId,fieldId] of [['Timer','#cutoff-enabled','#cutoff'],['Scheduler','#schedule-cutoff-enabled','#schedule-cutoff']]){
      await page.getByRole('tab',{name:tab,exact:true}).click();
      const toggle=page.locator(toggleId),input=page.locator(fieldId);
      await toggle.check();await input.focus();
      check(await input.isEditable()&&await input.evaluate(e=>document.activeElement===e),tab+' cutoff is focusable when enabled');
      await toggle.uncheck();check(await input.isDisabled(),tab+' cutoff is appropriately disabled while off');
    }
    await page.getByRole('tab',{name:'Settings',exact:true}).click();
    for(const kind of [0,1,2,3]){
      const toggle=page.locator('#sound-fade-'+kind),input=page.locator('#sound-fade-seconds-'+kind);
      await toggle.check();await input.focus();check(await input.isEditable(),'Audio '+kind+' fade duration is editable while enabled');
      await toggle.uncheck();check(await input.isDisabled(),'Audio '+kind+' fade duration is disabled while off');
    }
    const fade=page.locator('#sound-message-fade-3'),seconds=page.locator('#sound-message-fade-seconds-3');
    await fade.check();await seconds.focus();check(await seconds.isEditable(),'Send-fade duration is editable while enabled');
    await fade.uncheck();check(await seconds.isDisabled(),'Send-fade duration is disabled while off');
    await page.locator('#default-threshold').fill('');
    await page.keyboard.press('Control+Enter');
    await page.waitForFunction(()=>document.querySelector('#error').textContent.includes('whole seconds'));
    check(await page.locator('#threshold').inputValue()==='', 'An invalid threshold is kept editable with an error');
    await page.locator('#settings-low-time').uncheck();
    await page.waitForFunction(()=>window.previewMessages.findLast(m=>m.action==='lowTime')?.data.enabled===false);
    check(await page.locator('#default-threshold').inputValue()==='32','An empty number cannot prevent switching the warning off');
    await page.evaluate(()=>{
      const post=window.chrome.webview.postMessage;window.failThreshold=true;
      window.chrome.webview.postMessage=message=>{
        if(message.action==='lowTime'&&window.failThreshold){
          window.failThreshold=false;window.previewMessages.push(message);
          queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:'Threshold save failed.'}));
        }else post(message);
      };
    });
    await page.locator('#settings-low-time').check();
    await page.waitForFunction(()=>document.querySelector('#error').textContent==='Threshold save failed.');
    await page.evaluate(initial=>window.previewDispatch({type:'state',state:{...initial,timer:{...initial.timer,low:{enabled:false,inherit:false,threshold:32,track:0,custom:false}}}}),initial);
    check(await page.locator('#settings-low-time').isChecked()&&await page.locator('#low-time').isChecked()&&await page.locator('#default-threshold').isEditable(),
      'A failed save keeps the shared choice editable across background updates');
    await page.keyboard.press('Control+Enter');
    await page.waitForFunction(()=>document.querySelector('#status').textContent==='Settings saved.');
    check((await lowRequests()).at(-1).data.enabled,'Explicit Settings save retries a failed threshold change');
    await page.evaluate(()=>{
      const post=window.chrome.webview.postMessage;window.heldThresholds=[];
      window.chrome.webview.postMessage=message=>{
        if(message.action==='lowTime'){window.previewMessages.push(message);window.heldThresholds.push(message);}
        else post(message);
      };
    });
    await page.locator('#default-threshold').fill('40');await page.locator('#settings-low-time').focus();
    await page.waitForFunction(()=>window.heldThresholds.length===1);
    await page.locator('#default-threshold').fill('41');await page.locator('#settings-low-time').focus();
    await page.evaluate(initial=>{
      window.previewDispatch({type:'state',state:{...initial,timer:{...initial.timer,low:{enabled:true,inherit:false,threshold:40,track:0,custom:false}}}});
      window.previewDispatch({type:'reply',requestId:window.heldThresholds[0].requestId});
    },initial);
    await page.waitForFunction(()=>window.heldThresholds.length===2);
    check(await page.locator('#default-threshold').inputValue()==='41'&&await page.locator('#threshold').inputValue()==='41'&&(await lowRequests()).at(-1).data.threshold===41,
      'A delayed earlier save cannot overwrite a newer mirrored threshold edit');
    await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.heldThresholds[1].requestId}));
  }finally{await page.close();}
};
