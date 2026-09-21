// Browser keys cover native accelerator fallback, focus independence, repeat
// suppression and retry. NativeResetReloadSmoke covers real document reloads.
module.exports=async function resetReload(context,initial,settings,check){
  for(const view of ['main','compact','time-only','reflection']){
    const page=await context.newPage();
    try{
      await page.goto(view==='compact'||view==='time-only'?'https://reflection-timer.invalid/compact.html':`https://reflection-timer.invalid/index.html?view=${view}`);
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
      await page.evaluate(({initial,settings,view})=>{
        const state={...initial,prompts:view==='reflection'?[{id:'reset-prompt',isCheckIn:true,draft:'Keep this response',earlyEndReason:'Keep this reason',showEarlyEndReason:true,actual:'2 minutes',allotted:'15 minutes',completed:'Today'}]:[]};
        window.previewDispatch({type:'init',state,promptId:'reset-prompt',timeOnly:view==='time-only'});
        window.previewDispatch({type:'settings',settings});
        const original=window.chrome.webview.postMessage;
        window.chrome.webview.postMessage=message=>{
          if(['reset','resetAndReload'].includes(message.action)&&window.cancelReset){
            window.previewMessages.push(message);
            queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,cancelled:true}));
          }else if(message.action==='resetAndReload'&&window.rejectReset){
            window.previewMessages.push(message);
            queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:'Cannot save yet.'}));
          }else original(message);
        };
      },{initial,settings,view});
      const target=view==='main'?'#minutes':view==='compact'?'#app':view==='time-only'?'#read-time':'#later';
      await page.locator(target).focus();
      const requests=()=>page.evaluate(()=>window.previewMessages.filter(m=>m.action==='resetAndReload').length);
      // Modified/repeated DOM events should never turn into this shortcut.
      for(const extra of [{altKey:true},{shiftKey:true},{metaKey:true},{repeat:true},{isComposing:true}]){
        await page.evaluate(extra=>document.dispatchEvent(new KeyboardEvent('keydown',{key:'r',ctrlKey:true,bubbles:true,cancelable:true,...extra})),extra);
      }
      check(await requests()===0,view+' ignores Ctrl+R with other modifiers, composition, or key repeat');
      await page.evaluate(()=>{const dialog=document.createElement('dialog');dialog.id='reset-test-dialog';dialog.innerHTML='<button>Keep dialog</button>';document.body.append(dialog);dialog.showModal();});
      await page.keyboard.press('Control+r');
      check(await requests()===0,view+' does not reset or discard an open modal dialog');
      await page.evaluate(()=>document.querySelector('#reset-test-dialog').remove());await page.locator(target).focus();
      await page.evaluate(()=>window.rejectReset=true);
      await page.keyboard.press('Control+r');
      await page.waitForFunction(()=>document.querySelector('#error').textContent==='Cannot save yet.');
      check(await requests()===1,view+' routes real Ctrl+R from the focused control to the native reset-and-reload command');
      check(await page.evaluate(()=>sessionStorage.getItem('timer-reset-reload-view')===null),view+' failed reset clears stale reload metadata');
      await page.evaluate(()=>{window.rejectReset=false;window.cancelReset=true;window.previewDispatch({type:'resetAndReloadShortcut'});});
      await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='resetAndReload').length===2&&sessionStorage.getItem('timer-reset-reload-view')===null);
      check(await page.locator('#error').textContent()==='',view+' cancelled reset clears reload state without reporting an error');
      if(view==='main'||view==='compact'){
        await page.locator('#minutes').fill('2');await page.locator('#seconds').fill('3');
        await page.locator('#reset').click();await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='reset'));
        check(await page.locator('#minutes').inputValue()==='2'&&await page.locator('#seconds').inputValue()==='3',view+' cancelled Reset button preserves duration edits');
      }
      await page.evaluate(()=>window.cancelReset=false);
      await page.evaluate(()=>{window.rejectReset=false;window.previewDispatch({type:'resetAndReloadShortcut'});});
      await page.waitForFunction(()=>window.previewMessages.filter(m=>m.action==='resetAndReload').length===3);
      await page.keyboard.press('Control+r');
      await page.evaluate(()=>window.previewDispatch({type:'resetAndReloadShortcut'}));
      check(await requests()===3,view+' native accelerator can retry after Cancel and duplicate keys stay blocked until reload');
      if(view==='reflection')check(await page.locator('#reflection-text').inputValue()==='Keep this response'&&await page.locator('#early-reason').inputValue()==='Keep this reason',
        'Reset shortcut never submits or clears either reflection field in the browser');
      check(await page.evaluate(()=>!window.previewMessages.some(m=>['queue','skip','saveForLater','saveOrSendReflection'].includes(m.action))),view+' reset shortcut never sends or skips a reflection');
    }finally{await page.close();}
  }
};
