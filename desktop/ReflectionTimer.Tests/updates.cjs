// Synthetic release status and bridge only. No GitHub requests or installation.
module.exports=async function updates(context,initial,settings,check){
  const page=await context.newPage();
  try{
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(({initial,settings})=>{
      window.previewDispatch({type:'init',state:initial});
      window.previewDispatch({type:'settings',settings});
      const post=window.chrome.webview.postMessage;
      window.updateReplyMode='reply';
      window.chrome.webview.postMessage=message=>{
        if(!['checkForUpdates','installUpdate'].includes(message.action)){post(message);return;}
        window.previewMessages.push(message);
        if(window.updateReplyMode==='hold'){window.heldUpdateRequest=message;return;}
        queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,
          ...(window.updateReplyMode==='error'?{error:'Synthetic update request failed.'}:{})}));
      };
      window.previewDispatch({type:'updates',update:{status:'idle',currentVersion:'2.0.0'}});
    },{initial,settings});
    await page.locator('#tab-settings').click();
    const checkButton=page.getByRole('button',{name:'Check for updates',exact:true});
    const installButton=page.getByRole('button',{name:'Update now',exact:true});
    const status=page.locator('#updates-status'),progress=page.locator('#updates-progress');
    const sendState=update=>page.evaluate(update=>window.previewDispatch({type:'updates',update}),update);
    const calls=action=>page.evaluate(action=>window.previewMessages.filter(m=>m.action===action).length,action);
    check(await page.locator('#panel-settings>section').first().getAttribute('id')==='updates','Updates is the first Settings section below search');
    check(await page.locator('#updates-version').textContent()==='Current version: 2.0.0'&&await checkButton.isVisible()&&await installButton.isHidden(),'Updates starts with the installed version and a manual check button');
    check(await status.getAttribute('role')==='status'&&await status.getAttribute('aria-live')==='polite'&&await status.getAttribute('aria-atomic')==='true','Update status is one readable polite live region');
    check(await page.locator('#updates-help').isHidden()&&await page.locator('#help-toggle-updates').getAttribute('aria-expanded')==='false','Update instructions use the same collapsed circled help as other Settings sections');
    await page.locator('#help-toggle-updates').focus();await page.keyboard.press('Enter');
    check(await page.locator('#updates-help').isVisible()&&await page.locator('#help-toggle-updates').evaluate(e=>document.activeElement===e),'Keyboard users can open update help without losing focus');
    await page.keyboard.press('Space');
    await page.locator('#settings-search-input').fill('updates');await page.waitForTimeout(220);
    check(await checkButton.isVisible()&&!await page.locator('#theme').isVisible(),'Settings search finds Updates and filters unrelated controls');
    await page.locator('#settings-search-clear').click();

    await page.locator('#sheet-url').fill('https://synthetic.invalid/unsaved');
    const saves=await calls('saveAppearance'),connectionSaves=await calls('connectionStore');
    await page.evaluate(()=>window.updateReplyMode='hold');
    await checkButton.focus();await page.keyboard.press('Enter');
    await page.waitForFunction(()=>window.heldUpdateRequest?.action==='checkForUpdates');
    check(await checkButton.getAttribute('aria-disabled')==='true'&&await status.textContent()==='Checking for updates…','Checking exposes busy state immediately and announces progress');
    await page.evaluate(()=>{document.querySelector('#check-for-updates').click();document.querySelector('#check-for-updates').click();});
    check(await calls('checkForUpdates')===1,'Repeated mouse or keyboard activations cannot queue duplicate update checks');
    await sendState({status:'available',currentVersion:'2.0.0',availableVersion:'2.1.0'});
    check(await installButton.isVisible()&&await installButton.getAttribute('aria-disabled')==='true','A delayed check reply cannot allow a second action before its request finishes');
    await page.evaluate(()=>{window.updateReplyMode='reply';window.previewDispatch({type:'reply',requestId:window.heldUpdateRequest.requestId});});
    await page.waitForFunction(()=>document.querySelector('#install-update').getAttribute('aria-disabled')==='false');
    check(await status.textContent()==='Version 2.1.0 is available.'&&await checkButton.evaluate(e=>document.activeElement===e),'A newer version appears without replacing controls or moving check-button focus');
    check(await calls('saveAppearance')===saves&&await calls('connectionStore')===connectionSaves&&await page.locator('#sheet-url').inputValue()==='https://synthetic.invalid/unsaved','Checking for updates leaves unrelated unsaved Settings edits intact');

    await installButton.focus();await page.keyboard.press('Space');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='installUpdate'));
    check(await installButton.getAttribute('aria-disabled')==='true'&&await installButton.evaluate(e=>document.activeElement===e)&&await progress.isVisible(),'Updating starts an indeterminate progress indicator and retains keyboard focus');
    check(await progress.getAttribute('value')===null&&await progress.getAttribute('aria-label')==='Update download progress','An unknown download length has an accessible indeterminate progress bar');
    await page.evaluate(()=>{document.querySelector('#install-update').click();document.querySelector('#check-for-updates').click();});
    check(await calls('installUpdate')===1&&await calls('checkForUpdates')===1,'Download blocks additional update and check actions even after the host immediately replies');
    for(const value of [0,42.4,100,140,-5]){
      await sendState({status:'downloading',progress:value,message:'Downloading the update…'});
      const expected=Math.min(100,Math.max(0,value));
      check(await progress.evaluate(e=>e.value)===expected&&await page.locator('#updates-progress-caption').textContent()===Math.round(expected)+'%','Download progress handles '+value+' percent without changing focus');
    }
    await sendState({status:'downloading',progress:null,message:'Downloading the update…'});
    check(await progress.getAttribute('value')===null&&await page.locator('#updates-progress-caption').textContent()==='','Download progress returns to indeterminate when its size is unavailable');
    await sendState({status:'ready',availableVersion:'2.1.0',message:'Pause the timer, then choose Update now.'});
    check(await progress.isHidden()&&await installButton.getAttribute('aria-disabled')==='false'&&await installButton.evaluate(e=>document.activeElement===e),'A verified download can be retried after pausing a session without moving focus');
    await page.keyboard.press('Enter');await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='installUpdate').length===2);
    await sendState({status:'installing',message:'Installing and restarting…'});
    check(await installButton.isVisible()&&await installButton.getAttribute('aria-disabled')==='true'&&await installButton.evaluate(e=>document.activeElement===e)&&await status.textContent()==='Installing and restarting…','Restarting retains the focused control and exposes the installing state');
    await sendState({status:'error',message:'The update could not be installed. Try checking again.'});
    check(await checkButton.getAttribute('aria-disabled')==='false'&&await checkButton.evaluate(e=>document.activeElement===e)&&await installButton.isHidden(),'An installation failure restores a usable focused Check button');
    await page.evaluate(()=>window.updateReplyMode='error');await page.keyboard.press('Space');
    await page.waitForFunction(()=>document.querySelector('#updates-status').textContent==='Synthetic update request failed.');
    check(await checkButton.getAttribute('aria-disabled')==='false','A rejected host request reports its error and permits retry');
    await page.evaluate(()=>window.updateReplyMode='reply');
    await sendState({status:'available',availableVersion:'2.1.0'});await installButton.focus();
    await sendState({status:'downloading',message:'Downloading the update…'});
    // Headless browser documents may all report focus; model an inactive native
    // WebView explicitly to exercise the background-focus guard.
    await page.evaluate(()=>{window.updateFocusEvents=[];document.addEventListener('focusin',event=>window.updateFocusEvents.push(event.target.id));Object.defineProperty(document,'hasFocus',{configurable:true,value:()=>false});});
    try{
      await sendState({status:'error',message:'The download was interrupted.'});
      check(await page.evaluate(()=>!window.updateFocusEvents.includes('check-for-updates')),'An inactive App document does not focus Check after a background update error');
    }finally{await page.evaluate(()=>delete document.hasFocus);}
    await checkButton.focus();
    await sendState({status:'current',currentVersion:'2.0.0',message:'Your installed version is newer than the latest release.'});
    check(await installButton.isHidden()&&await status.textContent()==='Your installed version is newer than the latest release.','Current/newer installed builds have no downgrade action');
    await sendState({status:'available',availableVersion:'<img src=x onerror=alert(1)>',message:'<b>New update available</b>'});
    check(await status.textContent()==='<b>New update available</b>'&&await status.locator('*').count()===0,'Release status is displayed as text rather than executable markup');
    await sendState({status:'current',currentVersion:'2.0.0',message:'You have the latest version.'});
    await page.locator('#sheet-url').fill(settings.sheetUrl||'');
    await checkButton.focus();
    for(const chord of ['Control+s','Control+Enter']){
      const before=await calls('settingsSaveComplete');await page.keyboard.press(chord);
      await page.waitForFunction(before=>window.previewMessages.filter(m=>m.action==='settingsSaveComplete').length===before+1,before);
      check(await checkButton.evaluate(e=>document.activeElement===e),chord+' still saves Settings while the update control is focused');
    }
    for(const theme of [0,1,2,3]){
      await page.evaluate(({settings,theme})=>window.previewDispatch({type:'settings',settings:{...settings,theme}}),{settings,theme});
      for(const width of [739,420,336]){
        await page.setViewportSize({width,height:642});
        await checkButton.scrollIntoViewIfNeeded();
        check(await page.locator('#updates').evaluate(section=>section.scrollWidth<=section.clientWidth)&&await page.locator('#help-toggle-updates').evaluate(button=>{
          const label=document.getElementById('help-label-updates').getBoundingClientRect(),icon=button.getBoundingClientRect();
          return icon.left-label.right>=6&&icon.left-label.right<=10&&Math.abs((icon.top+icon.bottom-label.top-label.bottom)/2)<3;
        }),'Updates fits theme '+theme+' at '+width+' pixels with its help icon aligned');
      }
    }
  }finally{await page.close();}
};
