// Real document keys with a synthetic native bridge. These tests never register
// global shortcuts, play audio, or change the installed timer/profile.
module.exports=async function statusShortcut(context,initial,settings,check){
  async function open(view,status='Ready',stopwatch=false,initialize=true){
    const page=await context.newPage();
    await page.goto(view==='compact'||view==='time-only'?'https://reflection-timer.invalid/compact.html':`https://reflection-timer.invalid/index.html?view=${view}`);
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    if(initialize)await page.evaluate(({initial,settings,view,status,stopwatch})=>{
      const state={...initial,clock:{seconds:status==='Finished'?0:143,text:'2 minutes 23 seconds',status,stopwatch},
        timer:{...initial.timer,mode:stopwatch?1:0},
        prompts:view==='reflection'?[{id:'status-prompt',isCheckIn:true,draft:'Keep this response',earlyEndReason:'Keep this reason',showEarlyEndReason:true,actual:'2 minutes',allotted:'15 minutes',completed:'Today'}]:[]};
      window.previewDispatch({type:'init',state,promptId:'status-prompt',timeOnly:view==='time-only'});
      window.previewDispatch({type:'settings',settings});
    },{initial,settings,view,status,stopwatch});
    return page;
  }
  const calls=page=>page.evaluate(()=>window.previewMessages.filter(m=>m.action==='readStatus').length);
  const snapshot=page=>page.evaluate(()=>({focus:document.activeElement?.id,tiny:document.body.dataset.tiny,tab:document.body.dataset.tab,
    clock:document.querySelector('#visual-clock')?.textContent,fields:[...document.querySelectorAll('input,textarea')].map(e=>[e.id,e.value])}));
  const unrelated=page=>page.evaluate(()=>window.previewMessages.filter(m=>!['ready','interfaceReady','compactSize','readStatus'].includes(m.action)));
  for(const view of ['main','compact','time-only','reflection']){
    const target=view==='reflection'?'#reflection-text':view==='time-only'?'#read-time':'#minutes';
    for(const stopwatch of [false,true])for(const status of ['Ready','Running','Paused','Finished']){
      const page=await open(view,status,stopwatch);
      try{
        const modeTarget=stopwatch&&view==='main'?'#playback-toggle':stopwatch&&view==='compact'?'#toggle':target;
        await page.locator(modeTarget).focus();
        const before=await snapshot(page),otherBefore=await unrelated(page);
        await page.keyboard.press('Control+Alt+Space');
        await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='readStatus'));
        check(await calls(page)===1,`${view} ${stopwatch?'Stopwatch':'Timer'} ${status} routes Ctrl+Alt+Space only to status reading`);
        check(await page.locator(modeTarget).evaluate(e=>e===document.activeElement)&&JSON.stringify(await snapshot(page))===JSON.stringify(before)&&JSON.stringify(await unrelated(page))===JSON.stringify(otherBefore),
          `${view} ${stopwatch?'Stopwatch':'Timer'} ${status} status reading preserves focus, fields, clock, view and drafts`);
      }finally{await page.close();}
    }
    const page=await open(view);
    try{
      await page.locator(target).focus();
      for(const extra of [{ctrlKey:false},{altKey:false},{shiftKey:true},{metaKey:true},{repeat:true},{isComposing:true}]){
        await page.evaluate(extra=>document.dispatchEvent(new KeyboardEvent('keydown',{key:' ',ctrlKey:true,altKey:true,bubbles:true,cancelable:true,...extra})),extra);
      }
      check(await calls(page)===0,view+' ignores held, composing and differently modified status chords');
      await page.evaluate(()=>{
        const original=window.chrome.webview.postMessage;
        window.chrome.webview.postMessage=message=>{
          if(message.action==='readStatus'){
            window.previewMessages.push(message);
            window.pendingStatusReply=message.requestId;
          }else original(message);
        };
      });
      await page.keyboard.press('Control+Alt+Space');await page.waitForFunction(()=>window.pendingStatusReply);
      await page.keyboard.press('Control+Alt+Space');
      check(await calls(page)===1,view+' blocks competing status requests while the native reply is pending');
      await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.pendingStatusReply,error:'Status is unavailable.'}));
      await page.waitForFunction(()=>document.querySelector('#error').textContent==='Status is unavailable.');
      await page.keyboard.press('Control+Alt+Space');
      check(await calls(page)===2&&await page.locator(target).evaluate(e=>e===document.activeElement),view+' retries a failed status request without moving focus');
      await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.pendingStatusReply}));
      await page.evaluate(()=>{const dialog=document.createElement('dialog');dialog.id='status-test-dialog';dialog.innerHTML='<textarea id="status-modal-text">Keep modal text</textarea>';document.body.append(dialog);dialog.showModal();});
      const before=await snapshot(page),otherBefore=await unrelated(page);
      await page.keyboard.press('Control+Alt+Space');
      check(await calls(page)===3&&JSON.stringify(await snapshot(page))===JSON.stringify(before)&&JSON.stringify(await unrelated(page))===JSON.stringify(otherBefore),
        view+' reads status inside a modal without closing it or altering its field');
      await page.evaluate(()=>window.previewDispatch({type:'reply',requestId:window.pendingStatusReply}));
    }finally{await page.close();}
    const loading=await open(view,'Ready',false,false);
    try{await loading.keyboard.press('Control+Alt+Space');check(await calls(loading)===0,view+' waits for initialization before reading status');}
    finally{await loading.close();}
  }
  const main=await open('main');
  try{
    for(const tab of ['Settings','Scheduler','Outbox','Diagnostics','Help']){
      await main.getByRole('tab',{name:tab,exact:true}).click();
      if(tab==='Settings')await main.locator('#settings-search').focus();
      const before=await snapshot(main),count=await calls(main);
      await main.keyboard.press('Control+Alt+Space');
      check(await calls(main)===count+1&&JSON.stringify(await snapshot(main))===JSON.stringify(before),'App '+tab+' reads status without switching tabs or saving settings');
    }
    await main.evaluate(()=>window.previewDispatch({type:'shortcuts',shortcuts:Array.from({length:12},()=>({available:true}))}));
    const row=main.locator('#shortcut-notices p').nth(6);
    check((await row.textContent()).includes('announce Timer or Stopwatch')&&!(await row.textContent()).includes('same as Ctrl+Space'),
      'Settings describes Ctrl+Alt+Space as read-only status feedback');
  }finally{await main.close();}
};
