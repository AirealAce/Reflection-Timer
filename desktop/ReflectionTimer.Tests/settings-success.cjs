module.exports=async function settingsSuccess(context,initial,settings,check){
  for(const shortcut of [null,'Control+Enter','Control+s','CSV form'])for(const nativeDelivered of [true,false]){
    const page=await context.newPage();
    try{
      await page.goto('https://reflection-timer.invalid/index.html?view=main');
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
      await page.evaluate(({initial,settings,nativeDelivered})=>{
        window.previewDispatch({type:'init',state:initial});window.previewDispatch({type:'settings',settings});
        window.settingsNativeFeedback=[];window.settingsLiveFeedback=[];
        const feedback=()=>{
          window.settingsNativeFeedback.push('Settings saved.');
          window.previewDispatch({type:'announcement',message:'Settings saved.',announce:!nativeDelivered});
        };
        new MutationObserver(records=>{for(const record of records){
          const node=record.target.nodeType===Node.ELEMENT_NODE?record.target:record.target.parentElement;
          const region=node?.closest('#status,[data-feedback="status"]');
          if(region?.textContent==='Settings saved.'&&region.getAttribute('aria-live')!=='off')window.settingsLiveFeedback.push(region.textContent);
        }}).observe(document.body,{subtree:true,childList:true,characterData:true});
        const post=window.chrome.webview.postMessage;
        window.chrome.webview.postMessage=message=>{
          if(message.action==='connectionStore'){
            window.previewMessages.push(message);window.pendingConnection=message;
            if(!message.data.quiet)queueMicrotask(feedback);
          }
          else if(message.action==='settingsSaveComplete'){
            window.previewMessages.push(message);queueMicrotask(()=>{feedback();window.previewDispatch({type:'reply',requestId:message.requestId});});
          }
          else if(window.failAppearance&&message.action==='saveAppearance'){
            window.previewMessages.push(message);queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:'Settings storage unavailable'}));
          }else post(message);
        };
      },{initial,settings,nativeDelivered});
      await page.getByRole('tab',{name:'Settings',exact:true}).click();
      await page.locator('#theme').selectOption('1');
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='displayOption'));
      check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),'Immediate setting changes do not duplicate the explicit-save success sound');
      await page.locator('#sheet-mode').selectOption('fixed');await page.locator('#sheet-name').fill('Locally edited name');
      const save=()=>shortcut==='CSV form'?page.locator('#csv-form').evaluate(form=>form.requestSubmit()):shortcut?page.keyboard.press(shortcut):page.getByRole('button',{name:'Save settings',exact:true}).click();
      await save();await page.waitForFunction(()=>window.pendingConnection);
      check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),(shortcut||'Save settings')+' waits for the final connection write before success audio');
      check(await page.evaluate(()=>window.pendingConnection.data.quiet===true&&window.settingsNativeFeedback.length===0),(shortcut||'Save settings')+' keeps the intermediate connection write silent until the complete Settings save succeeds');
      await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.pendingConnection.requestId}));
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsSaveComplete'));
      check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='settingsSaveComplete').length===1),(shortcut||'Save settings')+' requests the configured success sound exactly once after completion');
      await page.waitForFunction(()=>document.querySelector('#status').textContent==='Settings saved.');await page.waitForTimeout(100);
      check(await page.evaluate(()=>window.settingsNativeFeedback.length===1),(shortcut||'Save settings')+' receives one canonical completion message from the native save');
      check(await page.evaluate(nativeDelivered=>window.settingsLiveFeedback.length===(nativeDelivered?0:1),nativeDelivered),
        (shortcut||'Save settings')+(nativeDelivered?' keeps the native-spoken completion readable without repeated browser speech':' uses one browser announcement when native speech is unavailable'));
      await page.evaluate(()=>{window.previewMessages=[];window.settingsNativeFeedback=[];window.settingsLiveFeedback=[];window.failAppearance=true;});
      await save();await page.waitForFunction(()=>document.querySelector('#error').textContent==='Settings storage unavailable');
      check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),(shortcut||'Save settings')+' does not play success if saving fails');
      check(await page.evaluate(()=>window.settingsNativeFeedback.length===0&&window.settingsLiveFeedback.length===0),(shortcut||'Save settings')+' does not announce a successful save when a required write fails');
    }finally{await page.close();}
  }
};
