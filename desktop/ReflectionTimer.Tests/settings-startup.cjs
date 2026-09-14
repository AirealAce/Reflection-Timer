module.exports=async function settingsStartup(context,initial,settings,check){
  for(const fail of [false,true]){
    const page=await context.newPage();
    try{
      await page.goto('https://reflection-timer.invalid/index.html?view=main');
      await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
      await page.evaluate(initial=>{
        const post=window.chrome.webview.postMessage;
        window.chrome.webview.postMessage=message=>{
          if(message.action==='settingsLoad'){window.previewMessages.push(message);window.heldSettings=message;}
          else post(message);
        };
        window.previewDispatch({type:'init',state:{...initial,theme:3}});
      },initial);
      await page.waitForFunction(()=>window.heldSettings);
      check(await page.evaluate(()=>document.documentElement.dataset.theme==='3'&&!window.previewMessages.some(m=>m.action==='interfaceReady')),
        'Cold start applies the saved theme but does not reveal controls before settings have loaded');
      await page.getByRole('tab',{name:'Settings',exact:true}).click();
      await page.keyboard.press('Control+Enter');
      await page.waitForFunction(()=>document.querySelector('#error').textContent.includes('still loading'));
      check(await page.evaluate(()=>!window.previewMessages.some(m=>['saveAppearance','settingsSaveComplete','volume','connectionStore'].includes(m.action))),
        'An early Save shortcut cannot overwrite saved settings with uninitialized field defaults');
      await page.evaluate(({settings,fail})=>{
        if(!fail)window.previewDispatch({type:'settings',settings:{...settings,theme:3,threshold:23}});
        window.previewDispatch({type:'reply',requestId:window.heldSettings.requestId,...(fail?{error:'Synthetic settings-load failure'}:{})});
      },{settings,fail});
      if(fail){
        await page.waitForFunction(()=>document.querySelector('#error').textContent==='Synthetic settings-load failure');
        check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='interfaceReady')),
          'Failed settings load never announces readiness with default settings');
      }else{
        await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='interfaceReady'));
        check(await page.locator('#theme').inputValue()==='3'&&await page.locator('#default-threshold').inputValue()==='23',
          'The startup readiness acknowledgement follows restoration of saved settings controls');
      }
    }finally{await page.close();}
  }
};
