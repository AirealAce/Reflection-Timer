module.exports=async function settingsShortcuts(context,initial,settings,check){
  const page=await context.newPage();
  try{
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(({initial,settings})=>{
      window.previewDispatch({type:'init',state:initial});
      window.previewDispatch({type:'settings',settings});
    },{initial,settings});
    await page.locator('#tab-settings').click();
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsShortcutScope'&&m.data.enabled));
    const count=()=>page.evaluate(()=>window.previewMessages.filter(m=>m.action==='settingsSaveComplete').length);
    for(const shortcut of ['Control+Enter','Control+s']){
      for(const selector of ['#theme','#show-compact','#settings-volume','#sound-track-0','#sound-behavior-0','#preview-sound-0','#default-threshold','#settings-time-reached-seconds','#sheet-url','#sheet-name','#save-settings','#tab-settings','#page-title']){
        await page.locator(selector).evaluate(e=>{if(e.disabled)e.disabled=false;if(e.tagName==='H1')e.tabIndex=-1;e.focus();});
        const before=await count();await page.keyboard.press(shortcut);
        await page.waitForFunction(before=>window.previewMessages.filter(m=>m.action==='settingsSaveComplete').length===before+1,before);
        check(await page.locator(selector).evaluate(e=>document.activeElement===e),shortcut+' saves once without moving focus from '+selector);
      }
      // Leave each edit focused: no blur or change event should be needed to persist it.
      for(const [selector,value,action,field] of [['#default-threshold','24','lowTime','threshold'],['#settings-time-reached-seconds','420','timeReached','seconds'],['#sound-fade-seconds-0','37','saveSound','fadeSeconds'],['#sheet-url','https://docs.google.com/spreadsheets/d/synthetic-settings-test/edit','connectionStore','sheetUrl']]){
        if(selector==='#sound-fade-seconds-0')await page.locator('#sound-fade-0').check();
        await page.locator(selector).fill(value);
        await page.evaluate(()=>window.previewMessages=[]);
        await page.keyboard.press(shortcut);
        await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsSaveComplete'));
        check(await page.evaluate(({action,field,value})=>window.previewMessages.some(m=>m.action===action&&String(m.data[field])===value),{action,field,value}),shortcut+' persists the unblurred edit in '+selector);
      }
    }
    await page.locator('#default-threshold').evaluate(e=>e.addEventListener('keydown',event=>event.stopImmediatePropagation()));
    await page.locator('#default-threshold').fill('31');
    let before=await count();await page.keyboard.press('Control+Enter');
    await page.waitForFunction(before=>window.previewMessages.filter(m=>m.action==='settingsSaveComplete').length===before+1,before);
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='lowTime').data.threshold===31),'Settings capture handler saves before a focused input can consume the key');

    await page.evaluate(()=>{
      window.previewMessages=[];window.holdSettings=true;
      const post=window.chrome.webview.postMessage;
      window.chrome.webview.postMessage=message=>{
        if(window.holdSettings&&message.action==='saveAppearance'){window.previewMessages.push(message);window.heldSettingsSave=message;}
        else post(message);
      };
    });
    await page.locator('#save-settings').focus();await page.keyboard.press('Control+Enter');
    await page.waitForFunction(()=>window.heldSettingsSave);
    await page.keyboard.press('Control+s');
    await page.evaluate(()=>{document.querySelector('#save-settings').click();window.previewDispatch({type:'settingsSaveShortcut'});document.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,repeat:true,bubbles:true,cancelable:true}));});
    check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='saveAppearance').length===1&&!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),'Button, native shortcut and repeated keys share one in-flight Settings save');
    await page.evaluate(()=>{window.holdSettings=false;window.previewDispatch({type:'reply',requestId:window.heldSettingsSave.requestId});});
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsSaveComplete'));
    check(await count()===1,'Competing Settings saves produce one success sound');

    await page.locator('#default-threshold').fill('0');await page.keyboard.press('Control+s');
    await page.waitForFunction(()=>document.querySelector('#error').textContent.length>0);
    check(await count()===1&&await page.locator('#default-threshold').inputValue()==='0','Invalid settings stay editable without success feedback');
    await page.locator('#default-threshold').fill('19');await page.keyboard.press('Control+s');
    await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='settingsSaveComplete').length===2);
    check(await page.evaluate(()=>window.previewMessages.findLast(m=>m.action==='lowTime').data.threshold===19),'Settings save can be retried after correcting validation');

    for(const mode of ['dialog','composition','other-tab']){
      if(mode==='dialog')await page.locator('#guided-setup').click();
      else if(mode==='composition')await page.locator('#sheet-url').evaluate(e=>e.dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true})));
      else await page.locator('#tab-timer').click();
      await page.waitForFunction(()=>window.previewMessages.findLast(m=>m.action==='settingsShortcutScope')?.data.enabled===false);
      before=await count();
      await page.evaluate(()=>{for(const key of ['Enter','s'])document.dispatchEvent(new KeyboardEvent('keydown',{key,ctrlKey:true,bubbles:true,cancelable:true}));window.previewDispatch({type:'settingsSaveShortcut'});});
      check(await count()===before,'Settings shortcuts leave '+mode+' alone, including delayed native messages');
      if(mode==='dialog')await page.locator('#guided-close').click();
      else if(mode==='composition')await page.locator('#sheet-url').evaluate(e=>e.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true})));
      else await page.locator('#tab-settings').click();
      await page.waitForFunction(()=>window.previewMessages.findLast(m=>m.action==='settingsShortcutScope')?.data.enabled===true);
    }
    before=await count();
    await page.evaluate(()=>{for(const data of [{repeat:true},{isComposing:true},{altKey:true},{shiftKey:true},{metaKey:true}])document.dispatchEvent(new KeyboardEvent('keydown',{key:'s',ctrlKey:true,bubbles:true,cancelable:true,...data}));});
    check(await count()===before,'Held, composing, and extra-modifier keys cannot save Settings');
  }finally{await page.close();}
};
