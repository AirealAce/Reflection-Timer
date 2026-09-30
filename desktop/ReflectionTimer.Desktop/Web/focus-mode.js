import {setOptions,setText} from './ui.js';

export function mountFocusMode({send,run,announce,view}) {
  const $=id=>document.getElementById(id),dialog=$('focus-target-dialog'),toggles=[$('focus-enabled'),$('focus-settings-enabled')];
  let settings={enabled:false,delaySeconds:5,target:null},enabled=false,dirty=false,revision=0,pending=false,saving=Promise.resolve();
  let opener,enableOnChoose=false,loading=false;
  const delay=$('focus-delay');
  const syncToggles=()=>toggles.forEach(control=>control.checked=enabled);
  function render(value){
    if(!value)return;settings=value;
    toggles.forEach(control=>control.disabled=false);
    if(!dirty){enabled=!!value.enabled;syncToggles();delay.value=value.delaySeconds??5;}
    const label=value.target?`${value.targetKind===1?'Tab':'Window'}: ${value.target}`:'No focus target selected.';
    setText($('focus-target-name'),label);setText($('focus-settings-target'),label);
  }
  function flush(){
    if(pending)return saving;
    if(!delay.checkValidity()){delay.reportValidity();return Promise.reject(new Error('Enter a focus delay of zero or more whole seconds.'));}
    pending=true;
    saving=(async()=>{
      while(dirty){const captured=revision;await send('focusMode',{enabled,delaySeconds:Number(delay.value),quiet:true});if(captured===revision)dirty=false;}
    })().finally(()=>pending=false);
    return saving;
  }
  async function refresh(){
    if(loading)return;loading=true;$('focus-target-kind').disabled=true;$('focus-target-list').disabled=true;$('focus-target-use').disabled=true;
    setText($('focus-picker-status'),'Loading open targets…');
    try {await send('focusTargets',{kind:Number($('focus-target-kind').value),quiet:true});}
    catch(error){setText($('focus-picker-status'),error.message);throw error;}
    finally{loading=false;$('focus-target-kind').disabled=false;$('focus-target-refresh').disabled=false;}
  }
  function open(button,enable=false){
    opener=button;enableOnChoose=enable;$('focus-target-kind').value=String(settings.targetKind??0);
    dialog.showModal();$('focus-target-kind').focus();run(refresh);
  }
  for(const toggle of toggles)toggle.addEventListener('change',()=>{
    if(toggle.checked&&!settings.target){syncToggles();open(toggle,true);return;}
    enabled=toggle.checked;syncToggles();dirty=true;revision++;run(flush);
  });
  delay.addEventListener('input',()=>{dirty=true;revision++;});
  delay.addEventListener('change',()=>run(flush));
  delay.addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.ctrlKey&&!event.altKey&&!event.isComposing){event.preventDefault();run(flush);}});
  for(const id of ['choose-focus-target','choose-focus-target-settings'])$(id).addEventListener('click',()=>open($(id)));
  $('focus-target-kind').addEventListener('change',()=>run(refresh));
  $('focus-target-refresh').addEventListener('click',()=>run(refresh));
  $('focus-target-use').addEventListener('click',()=>run(async()=>{
    const id=$('focus-target-list').value;if(!id)return;
    $('focus-target-use').disabled=true;
    try {await flush();await send('focusSelect',{id,enable:enableOnChoose,quiet:true});dialog.close();announce('Focus target saved.');}
    catch(error){$('focus-target-use').disabled=false;throw error;}
  }));
  $('focus-target-cancel').addEventListener('click',()=>dialog.close());
  dialog.addEventListener('close',()=>{syncToggles();opener?.focus();});
  return {render,flush,state(state){if(view==='main')render(state.focusMode);},message(message){
    if(view!=='main')return;
    if(message.type==='focusStatus')setText($('focus-status'),message.status);
    if(message.type==='settings')render(message.settings.focusMode);
    if(message.type==='focusTargets'&&dialog.open&&Number($('focus-target-kind').value)===message.kind){
      const choices=message.targets.map((target,index)=>({value:target.id,label:message.kind===1?`Tab ${target.tabPosition??index+1} · ${target.name} — ${target.app}${target.windowName?' · '+target.windowName:''}`:`${target.name} — ${target.app}`}));
      setOptions($('focus-target-list'),choices,choices[0]?.value??'');
      $('focus-target-list').disabled=choices.length===0;$('focus-target-use').disabled=choices.length===0;
      setText($('focus-picker-status'),choices.length?`${choices.length} open targets. Choose one, then use selected target.`:message.kind===1?'No readable browser tabs found. Try Window instead.':'No selectable windows found. Open the target and refresh.');
    }
  }};
}
