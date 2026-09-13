// UI-only native bridge: no production settings or Sheets are accessed.
module.exports=async function messageSentFade(context,initial,settings,check){
  const page=await context.newPage();
  try{
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(({initial,settings})=>{
      window.previewDispatch({type:'init',state:initial});window.previewDispatch({type:'settings',settings});
      const post=window.chrome.webview.postMessage;
      window.chrome.webview.postMessage=message=>{
        if(message.action==='saveSound'&&message.data.kind===3&&(window.failFade||(message.data.fadeAfterMessageSent&&message.data.messageSentFadeSeconds<1))){
          window.previewMessages.push(message);queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:'Message-sent fade could not be saved.'}));
        }else post(message);
      };
    },{initial,settings});
    await page.getByRole('tab',{name:'Settings',exact:true}).click();
    const checkbox=page.getByRole('checkbox',{name:'Fade out after message sent',exact:true}),seconds=page.getByRole('spinbutton',{name:'Low-time message-sent fade duration in seconds',exact:true});
    check(await checkbox.count()===1&&await checkbox.evaluate(e=>e.closest('form').id==='sound-form-3'),'Only Low on time audio has the message-sent fade option');
    check(!await checkbox.isChecked()&&await seconds.isDisabled()&&await seconds.inputValue()==='3','Legacy settings default to disabled message-sent fading with three seconds');
    check((await page.locator('#sound-message-fade-help-3').textContent()).includes('Disruptive'),'The option explains confirmed delivery and the Disruptive audio exception');
    await checkbox.check();
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveSound'&&m.data.kind===3&&m.data.fadeAfterMessageSent&&m.data.messageSentFadeSeconds===3&&m.data.quiet));
    check(await seconds.isEnabled(),'Enabling message-sent fading silently saves it and enables its duration');
    await seconds.fill('7');await page.evaluate(settings=>window.previewDispatch({type:'settings',settings}),settings);
    check(await seconds.inputValue()==='7'&&await checkbox.isChecked(),'A background settings update cannot overwrite an unfinished fade edit');
    await seconds.press('Enter');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveSound'&&m.data.messageSentFadeSeconds===7));
    check(await checkbox.evaluate(e=>e===document.activeElement),'Enter leaves the fade duration and saves without submitting Settings');
    check(await page.evaluate(()=>!window.previewMessages.some(m=>['settingsSaveComplete','previewSound'].includes(m.action))),'Changing message-sent fading neither plays a preview nor requests a success sound');
    await seconds.fill('9');await seconds.press('Control+Enter');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsSaveComplete'));
    check(await page.evaluate(()=>window.previewMessages.some(m=>m.action==='saveSound'&&m.data.kind===3&&m.data.messageSentFadeSeconds===9)),'Ctrl+Enter saves an unblurred message-sent fade duration through Save settings');
    await page.evaluate(()=>{window.previewMessages=[];window.failFade=true;});
    await seconds.fill('12');await seconds.press('Control+Enter');
    await page.waitForFunction(()=>document.querySelector('#error').textContent==='Message-sent fade could not be saved.');
    check(await seconds.inputValue()==='12'&&await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),'A failed fade save preserves the input and cannot report successful Settings save');
    await page.evaluate(()=>window.failFade=false);await seconds.press('Control+Enter');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsSaveComplete'));
    check(true,'The fade setting can be retried after a save failure');
    await seconds.fill('0');await seconds.press('Enter');await page.waitForFunction(()=>document.querySelector('#error').textContent==='Message-sent fade could not be saved.');
    await checkbox.uncheck();await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveSound'&&m.data.kind===3&&!m.data.fadeAfterMessageSent));
    check(await seconds.isDisabled(),'Message-sent fading can be disabled even after an invalid duration edit');
    const saved={...settings,sounds:settings.sounds.map(s=>s.kind===3?{...s,fadeOutAfterMessageSent:true,messageSentFadeSeconds:12}:s)};
    await page.evaluate(settings=>window.previewDispatch({type:'settings',settings}),saved);
    check(await checkbox.isChecked()&&await seconds.inputValue()==='12'&&await seconds.isEnabled(),'Saved message-sent fade values are restored by the native settings snapshot');
    for(const width of [940,739,420])for(const theme of [0,1,2,3]){
      await page.setViewportSize({width,height:780});
      await page.evaluate(({state,settings})=>{window.previewDispatch({type:'state',state});window.previewDispatch({type:'settings',settings});},{state:{...initial,theme},settings:{...saved,theme}});
      await seconds.scrollIntoViewIfNeeded();
      const layout=await page.evaluate(()=>{
        const input=document.querySelector('#sound-message-fade-seconds-3'),checkbox=document.querySelector('#sound-message-fade-3');
        const box=input.getBoundingClientRect(),units=input.nextElementSibling.getBoundingClientRect(),check=checkbox.getBoundingClientRect();
        const text=document.createRange();text.selectNodeContents(checkbox.parentElement);text.setStartAfter(checkbox);const label=text.getBoundingClientRect();
        const main=document.querySelector('main');
        const overflow=[...main.querySelectorAll('*')].filter(e=>e.getClientRects().length&&e.getBoundingClientRect().right>main.getBoundingClientRect().right).slice(0,8).map(e=>e.id||e.className||e.tagName);
        return {inputDelta:Math.abs(box.y+box.height/2-units.y-units.height/2),labelDelta:Math.abs(check.y+check.height/2-label.y-label.height/2),mainOverflow:main.scrollWidth-main.clientWidth,pageOverflow:document.documentElement.scrollWidth-innerWidth,overflow};
      });
      const fits=layout.inputDelta<2&&layout.labelDelta<3&&layout.mainOverflow<=0&&layout.pageOverflow<=0;
      check(fits,`Message-sent fade controls stay vertically centered without horizontal overflow at ${width}px, theme ${theme}${fits?'':': '+JSON.stringify(layout)}`);
      if(width===739&&theme===3&&process.env.REFLECTION_PREVIEW_SCREENSHOTS){
        const fs=require('node:fs/promises'),path=require('node:path');await fs.mkdir(process.env.REFLECTION_PREVIEW_SCREENSHOTS,{recursive:true});
        await page.screenshot({path:path.join(process.env.REFLECTION_PREVIEW_SCREENSHOTS,'low-time-message-sent-fade.png')});
      }
    }
  }finally{await page.close();}
};
