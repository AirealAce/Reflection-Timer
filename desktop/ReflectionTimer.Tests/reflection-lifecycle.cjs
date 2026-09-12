module.exports=async function reflectionLifecycle(context,initial,check){
  const page=await context.newPage();
  const prompt={id:'lifecycle',isCheckIn:true,endedEarly:false,showEarlyEndReason:true,draft:'Saved before closing',earlyEndReason:'Possible interruption',actual:'10 seconds',allotted:'1 minute',completed:'Today'};
  async function reopen(state){
    await page.goto('https://reflection-timer.invalid/index.html?view=reflection');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(()=>{
      const post=window.chrome.webview.postMessage;
      window.chrome.webview.postMessage=message=>{
        if(message.action==='reflectionReady')window.editorAtReady={text:document.querySelector('#reflection-text').value,theme:document.documentElement.dataset.theme,reason:!document.querySelector('#reason-group').hidden};
        post(message);
      };
    });
    await page.evaluate(state=>window.previewDispatch({type:'init',state,promptId:'lifecycle'}),state);
    await page.waitForFunction(()=>window.editorAtReady);
  }
  for(const theme of [0,1,2,3]){
    const state={...initial,theme,prompts:[prompt]};
    await reopen(state);
    const ready=await page.evaluate(()=>window.editorAtReady);
    check(ready.text==='Saved before closing'&&ready.theme===String(theme)&&ready.reason,'Native readiness is acknowledged only after the saved draft, reason, and theme are populated: theme '+theme);
    await page.locator('#reflection-text').fill('Final unsaved keystroke');
    await page.locator('#early-reason').fill('A reason typed before zero');
    const completed={...prompt,isCheckIn:false,showEarlyEndReason:false,draft:'Older saved snapshot',actual:'1 minute',completed:'9/12/2026 3:45 PM'};
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),{...state,prompts:[completed]});
    check(await page.locator('#reflection-text').inputValue()==='Final unsaved keystroke'&&await page.locator('#reason-group').isHidden(),'Natural completion hides the reason without overwriting in-flight reflection text: theme '+theme);
    check(await page.locator('#reflection-heading').textContent()==='Session reflection'&&(await page.locator('#reflection-context').textContent()).includes('1 minute spent'),'Promoted prompt updates its heading and actual time without recreating the editor: theme '+theme);
    check(await page.locator('#reflection-timestamp').textContent()===completed.completed,'Promoted prompt updates its timestamp under the same reflection ID: theme '+theme);
    check(await page.locator('#reflection-text').evaluate(e=>e===document.activeElement),'Hiding the focused reason moves focus safely to the reflection box: theme '+theme);
    await page.evaluate(()=>window.previewDispatch({type:'reflectionShortcut'}));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveForLater'));
    check(await page.evaluate(()=>window.previewMessages.some(m=>m.action==='draft'&&m.data.text==='Final unsaved keystroke')),'Save after completion retains the latest text under the original prompt ID: theme '+theme);
    await reopen({...state,prompts:[{...completed,draft:'Final unsaved keystroke'}]});
    check(await page.locator('#reflection-text').inputValue()==='Final unsaved keystroke'&&await page.locator('#reason-group').isHidden(),'Reopened completed prompt restores its saved response and has no early-end field: theme '+theme);
    await page.evaluate(state=>window.previewDispatch({type:'state',state}),{...state,prompts:[{...completed,endedEarly:true,showEarlyEndReason:true}]});
    check(await page.locator('#reason-group').isVisible(),'A genuine early completion retains the reason field: theme '+theme);
  }
  await page.close();
};
