// Native reader delivery is synthetic; browser assertions verify one DOM route,
// modal accessibility, no focus/layout change, and quiet native-delivered text.
module.exports=async function feedbackAnnouncements(context,initial,settings,check){
  for(const view of ['main','compact','time-only','reflection']){
    const page=await context.newPage();
    try{
      const compact=view==='compact'||view==='time-only';
      await page.goto(`https://reflection-timer.invalid/${compact?'compact':'index'}.html?view=${compact?'compact':view}`);
      await page.waitForFunction(()=>window.previewMessages.some(message=>message.action==='ready'));
      await page.evaluate(({initial,settings,view})=>{
        const state={...initial,prompts:view==='reflection'?[{id:'feedback-prompt',isCheckIn:true,draft:'Keep this reflection',earlyEndReason:'',actual:'2 minutes',allotted:'15 minutes',completed:'Today'}]:initial.prompts};
        window.previewDispatch({type:'init',state,promptId:'feedback-prompt',timeOnly:view==='time-only'});
        window.previewDispatch({type:'settings',settings});
        const original=window.chrome.webview.postMessage;
        window.chrome.webview.postMessage=message=>{
          if(message.action==='readTime'&&window.feedbackError){window.previewMessages.push(message);queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:window.feedbackError}));return;}
          original(message);
        };
        window.feedbackEvents=[];
        new MutationObserver(records=>{
          for(const record of records){
            const node=record.target.nodeType===Node.ELEMENT_NODE?record.target:record.target.parentElement;
            const region=node?.closest('#status,#error,[data-feedback]');
            if(region?.textContent&&region.getAttribute('aria-live')!=='off')window.feedbackEvents.push({text:region.textContent,scope:region.closest('dialog')?.id??'body'});
          }
        }).observe(document.body,{subtree:true,childList:true,characterData:true});
      },{initial,settings,view});
      const snapshot=()=>page.evaluate(()=>{const rect=document.querySelector('main').getBoundingClientRect();return {focus:document.activeElement?.id,tiny:document.body.dataset.tiny,tab:document.body.dataset.tab,clock:document.querySelector('#visual-clock').textContent,reflection:document.querySelector('#reflection-text')?.value,rect:[rect.x,rect.y,rect.width],modalRect:document.querySelector('#feedback-modal[open]')?.getBoundingClientRect().toJSON()};});
      const before=await snapshot();
      await page.evaluate(()=>window.previewDispatch({type:'announcement',message:'Timer paused with 2 minutes remaining.',announce:false}));
      await page.waitForTimeout(100);
      check(await page.locator('#status').textContent()==='Timer paused with 2 minutes remaining.'&&await page.locator('#status').getAttribute('aria-live')==='off'&&await page.evaluate(()=>window.feedbackEvents.length===0),view+' preserves native-delivered feedback as readable text without a duplicate live announcement');
      await page.evaluate(()=>window.previewDispatch({type:'announcement',message:'Timer resumed with 2 minutes remaining.'}));
      await page.waitForFunction(()=>document.querySelector('#status').textContent==='Timer resumed with 2 minutes remaining.');
      check(await page.locator('#status').getAttribute('aria-live')==='polite'&&await page.evaluate(()=>window.feedbackEvents.filter(event=>event.text==='Timer resumed with 2 minutes remaining.').length===1),view+' legacy or native-failed feedback uses one polite live region');
      await page.evaluate(()=>window.previewDispatch({type:'announcement',message:'Timer resumed with 2 minutes remaining.'}));
      await page.waitForTimeout(100);
      check(await page.evaluate(()=>window.feedbackEvents.filter(event=>event.text==='Timer resumed with 2 minutes remaining.').length===2),view+' repeats the same requested status through a fresh live update');
      await page.evaluate(()=>{window.feedbackEvents=[];window.previewDispatch({type:'announcement',message:'Obsolete pending action'});window.previewDispatch({type:'announcement',message:'Latest action'});});
      await page.waitForTimeout(100);
      check(await page.evaluate(()=>window.feedbackEvents.length===1&&window.feedbackEvents[0].text==='Latest action'),view+' coalesces rapid feedback to the latest action');
      await page.evaluate(()=>{window.feedbackEvents=[];window.previewDispatch({type:'announcement',message:'Obsolete DOM fallback'});window.previewDispatch({type:'announcement',message:'Native-delivered replacement',announce:false});});await page.waitForTimeout(100);
      check(await page.locator('#status').textContent()==='Native-delivered replacement'&&await page.locator('#status').getAttribute('aria-live')==='off'&&await page.evaluate(()=>window.feedbackEvents.length===0),view+' native-delivered replacement cancels a pending DOM fallback before it can speak');
      check(JSON.stringify(await snapshot())===JSON.stringify(before),view+' feedback leaves focus, clock, fields, layout and viewer mode unchanged');

      await page.evaluate(()=>{
        const dialog=document.createElement('dialog');dialog.id='feedback-modal';dialog.innerHTML='<label>Modal input<input id="feedback-modal-input" value="Keep modal input"></label><button id="feedback-error">Retry</button>';
        dialog.querySelector('button').addEventListener('click',()=>document.querySelector('#read-time').click());document.body.append(dialog);dialog.showModal();document.querySelector('#feedback-modal-input').focus();
      });
      const modal=page.locator('#feedback-modal'),modalBefore=await snapshot();
      await page.evaluate(()=>{window.feedbackEvents=[];window.previewDispatch({type:'announcement',message:'Focus settings could not be saved.'});});
      await page.waitForFunction(()=>document.querySelector('#feedback-modal [data-feedback="status"]')?.textContent==='Focus settings could not be saved.');
      check(await modal.locator('[data-feedback="status"]').getAttribute('role')==='status'&&await modal.locator('[data-feedback="status"]').getAttribute('aria-live')==='polite'&&await page.locator('#status').getAttribute('aria-live')==='off',view+' routes feedback inside the active modal and leaves its body mirror quiet');
      check(await page.evaluate(()=>window.feedbackEvents.filter(event=>event.text==='Focus settings could not be saved.').length===1&&window.feedbackEvents[0].scope==='feedback-modal'),view+' modal status produces only one live update');
      const cdp=await context.newCDPSession(page),tree=await cdp.send('Accessibility.getFullAXTree');
      check(tree.nodes.filter(node=>!node.ignored&&node.role?.value==='StaticText'&&node.name?.value==='Focus settings could not be saved.').length===1,view+' modal feedback remains in the browser accessibility tree while the body is inert');await cdp.detach();
      await page.evaluate(()=>{window.feedbackError='A modal operation failed. Try again.';});await modal.locator('#feedback-error').click();
      await page.waitForFunction(()=>document.querySelector('#feedback-modal [data-feedback="error"]')?.textContent==='A modal operation failed. Try again.');
      check(await modal.locator('[data-feedback="error"]').getAttribute('role')==='alert'&&await modal.locator('[data-feedback="error"]').getAttribute('aria-live')==='assertive'&&await page.locator('#error').getAttribute('aria-live')==='off',view+' modal failures use one assertive scoped alert without a live parent duplicate');
      await modal.locator('#feedback-modal-input').focus();
      check(JSON.stringify(await snapshot())===JSON.stringify(modalBefore)&&await modal.locator('input').inputValue()==='Keep modal input',view+' scoped status and errors preserve modal focus, field text and the underlying viewer');
      await page.evaluate(()=>{window.feedbackEvents=[];window.previewDispatch({type:'announcement',message:'Native spoke inside modal.',announce:false});});await page.waitForTimeout(100);
      check(await page.locator('#status').textContent()==='Native spoke inside modal.'&&await modal.locator('[data-feedback="status"]').textContent()===''&&await page.evaluate(()=>window.feedbackEvents.length===0),view+' native-delivered modal feedback stays readable without DOM speech');

      await page.evaluate(()=>{window.feedbackEvents=[];window.previewDispatch({type:'announcement',message:'Stale modal message'});document.querySelector('#feedback-modal').close();});await page.waitForTimeout(100);
      check(await page.evaluate(()=>!window.feedbackEvents.some(event=>event.text==='Stale modal message')&&document.querySelector('#feedback-modal [data-feedback="status"]').textContent===''),view+' closing a modal cancels its pending announcement');
      await page.evaluate(()=>{window.feedbackEvents=[];window.previewDispatch({type:'announcement',message:'Body feedback restored'});});
      await page.waitForFunction(()=>document.querySelector('#status').textContent==='Body feedback restored');
      check(await page.locator('#status').getAttribute('aria-live')==='polite'&&await page.evaluate(()=>window.feedbackEvents.filter(event=>event.text==='Body feedback restored').length===1),view+' later feedback restores the body live region after the modal closes');
      await page.evaluate(()=>{document.querySelector('#feedback-modal').showModal();});await page.waitForTimeout(100);
      check(await modal.locator('[data-feedback="status"]').textContent()===''&&await modal.locator('[data-feedback="error"]').textContent()==='',view+' reopening a modal cannot replay old status or error text');
      await page.evaluate(()=>document.querySelector('#feedback-modal').close());
      await page.evaluate(()=>{window.feedbackError='A body operation failed.';document.querySelector('#read-time').click();});
      await page.waitForFunction(()=>document.querySelector('#error').textContent==='A body operation failed.');
      check(await page.locator('#error').getAttribute('role')==='alert'&&await page.locator('#error').getAttribute('aria-live')==='assertive',view+' later local errors restore the ordinary body alert');
    }finally{await page.close();}
  }
};
