module.exports=async function performance(context,initial,settings,check){
  const page=await context.newPage();
  try{
    await page.goto('https://reflection-timer.invalid/index.html?view=main');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='ready'));
    await page.evaluate(({initial,settings})=>{
      window.previewDispatch({type:'init',state:initial});window.previewDispatch({type:'settings',settings});
      const post=window.chrome.webview.postMessage;
      window.chrome.webview.postMessage=message=>{
        if(window.holdVolume&&message.action==='volume'){
          window.previewMessages.push(message);window.heldVolume=message;
        }else if(window.failVolume&&message.action==='volume'){
          window.previewMessages.push(message);queueMicrotask(()=>window.previewDispatch({type:'reply',requestId:message.requestId,error:'Synthetic volume save failed'}));
        }else post(message);
      };
    },{initial,settings});
    await page.getByRole('tab',{name:'Settings',exact:true}).click();
    const burst=async(selector,last=83)=>page.evaluate(({selector,last})=>{
      window.previewMessages=[];const input=document.querySelector(selector);
      for(let n=20;n<=last;n++){input.value=n;input.dispatchEvent(new Event('input',{bubbles:true}));}
    },{selector,last});
    await burst('#settings-volume');
    check(await page.locator('#app-volume').inputValue()==='83','A rapid slider drag updates both displayed master volumes immediately');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='volume'));
    check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='volume').length===1&&window.previewMessages.find(m=>m.action==='volume').data.volume===83),'A master-volume input burst persists only its final value');
    await burst('#sound-volume-1');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='saveSound'));
    check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='saveSound').length===1&&window.previewMessages.find(m=>m.action==='saveSound').data.volume===83),'A per-event volume burst coalesces to one durable save');
    await burst('#settings-volume',64);
    await page.evaluate(()=>document.querySelector('#settings-volume').dispatchEvent(new Event('change',{bubbles:true})));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='volume'&&m.data.volume===64));
    check(true,'Releasing the slider flushes its final value without waiting for debounce');
    await burst('#sound-volume-3',77);await page.keyboard.press('Control+Enter');
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='settingsSaveComplete'));
    check(await page.evaluate(()=>window.previewMessages.findIndex(m=>m.action==='saveSound'&&m.data.volume===77)<window.previewMessages.findIndex(m=>m.action==='settingsSaveComplete')),'Explicit Save waits for pending per-event volume before success feedback');
    await burst('#settings-volume',72);
    await page.evaluate(()=>window.previewDispatch({type:'flushSettings'}));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='flushed'));
    check(await page.evaluate(()=>window.previewMessages.findIndex(m=>m.action==='volume'&&m.data.volume===72)<window.previewMessages.findIndex(m=>m.action==='flushed')&&!window.previewMessages.some(m=>m.action==='settingsSaveComplete')),'Normal quit flushes pending volume silently before acknowledging the host');
    await page.evaluate(()=>window.failVolume=true);await burst('#settings-volume',69);
    await page.evaluate(()=>window.previewDispatch({type:'flushSettings'}));
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='flushFailed'));
    check(await page.evaluate(()=>!window.previewMessages.some(m=>m.action==='flushed')),'Failed settings flush cannot approve app shutdown');
    await page.evaluate(()=>{window.failVolume=false;window.previewMessages=[];window.previewDispatch({type:'flushSettings'});});
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='flushed'));
    check(await page.evaluate(()=>window.previewMessages.some(m=>m.action==='volume'&&m.data.volume===69)),'Failed slider save keeps its dirty value for retry');
    await page.evaluate(()=>{window.holdVolume=true;window.previewMessages=[];const input=document.querySelector('#settings-volume');input.value=25;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));});
    await page.waitForFunction(()=>window.heldVolume);
    await page.evaluate(()=>{const input=document.querySelector('#settings-volume');for(const value of [30,40,50,88]){input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));}});
    check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='volume').length===1),'A blocked volume save does not queue every intermediate slider value');
    await page.evaluate(()=>{window.holdVolume=false;window.previewDispatch({type:'reply',requestId:window.heldVolume.requestId});});
    await page.waitForFunction(()=>window.previewMessages.some(m=>m.action==='volume'&&m.data.volume===88));
    check(await page.evaluate(()=>window.previewMessages.filter(m=>m.action==='volume').length===2),'After a slow save, only the newest volume is persisted next');
    await page.evaluate(initial=>{
      window.historyRow=document.querySelector('#outbox-rows').firstElementChild;
      window.historyMutations=0;window.historyObserver=new MutationObserver(records=>window.historyMutations+=records.length);
      window.historyObserver.observe(document.querySelector('#outbox-rows'),{childList:true,subtree:true,attributes:true,characterData:true});
      window.previewDispatch({type:'state',state:{...initial,outbox:null,schedules:null,appVolume:35}});
    },initial);
    check(await page.evaluate(()=>window.historyMutations===0&&window.historyRow===document.querySelector('#outbox-rows').firstElementChild),'Volume-only incremental updates preserve history rows without DOM work');
    await page.evaluate(initial=>window.previewDispatch({type:'state',state:{...initial,outbox:[],schedules:[]}}),initial);
    check(await page.locator('#outbox-rows').locator('tr').count()===0,'An explicit empty history update clears rows; null does not');
  }finally{await page.close();}
};
