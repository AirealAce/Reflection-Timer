module.exports=async function settingsSuccess(context,initial,settings,check){
  for(const shortcut of [null,'Control+Enter','Control+s']){
    const page=await context.newPage();
    try{
      await page.goto('https://reflection-timer.invalid/index.html?view=main');
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
      await page.evaluate(({initial,settings})=>{
        window.previewDispatch({type:'init',state:initial});window.previewDispatch({type:'settings',settings});
        const post=window.chrome.webview.postMessage;
        window.chrome.webview.postMessage=message=>{
          if(message.action==='connectionStore'){window.previewMessages.push(message);window.pendingConnection=message;}
          else if(window.failAppearance&&message.action==='saveAppearance'){
            window.previewMessages.push(message);queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:'Settings storage unavailable'}));
          }else post(message);
        };
      },{initial,settings});
      await page.getByRole('tab',{name:'Settings',exact:true}).click();
      await page.locator('#theme').selectOption('1');
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='displayOption'));
      check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),'Immediate setting changes do not duplicate the explicit-save success sound');
      await page.locator('#sheet-mode').selectOption('fixed');await page.locator('#sheet-name').fill('Locally edited name');
      const save=()=>shortcut?page.keyboard.press(shortcut):page.getByRole('button',{name:'Save settings',exact:true}).click();
      await save();await page.waitForFunction(()=>window.pendingConnection);
      check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),(shortcut||'Save settings')+' waits for the final connection write before success audio');
      await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.pendingConnection.requestId}));
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsSaveComplete'));
      check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='settingsSaveComplete').length===1),(shortcut||'Save settings')+' requests the configured success sound exactly once after completion');
      await page.evaluate(()=>{window.previewMessages=[];window.failAppearance=true;});
      await save();await page.waitForFunction(()=>document.querySelector('#error').textContent==='Settings storage unavailable');
      check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),(shortcut||'Save settings')+' does not play success if saving fails');
    }finally{await page.close();}
  }
};
