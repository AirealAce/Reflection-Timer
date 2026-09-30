import {setOptions,setText} from './ui.js';

export function mountFocusMode({send,run,announce,view}) {
  const $=id=>document.getElementById(id),dialog=$('focus-target-dialog'),toggles=[$('focus-enabled'),$('focus-settings-enabled')];
  const targetButtons=[$('choose-focus-target'),$('choose-focus-target-settings')],list=$('focus-target-list'),use=$('focus-target-use');
  let settings={enabled:false,delaySeconds:5,target:null},enabled=false,dirty=false,revision=0,pending=false,saving=Promise.resolve();
  let opener,enableOnChoose=false,loading=false,selecting=false,spacePressed=false;
  const delay=$('focus-delay');
  const targetName=(target,kind,position=target.tabPosition)=>{
    const window=[target.app,kind===0?target.name:target.windowName].filter(Boolean).join(' · ');
    return kind===0?window:[window,target.name].filter(Boolean).join(' — ')+(position>0?` · ${kind===2?'Group':'Tab'} ${position}`:'');
  };
  const syncToggles=()=>toggles.forEach(control=>{
    control.setAttribute('aria-pressed',String(enabled));
    control.classList.toggle('primary',enabled);
  });
  function render(value){
    if(!value)return;settings=value;
    toggles.forEach(control=>control.disabled=false);
    if(!dirty){enabled=!!value.enabled;syncToggles();delay.value=value.delaySeconds??5;}
    const kind=value.targetKind??0,type=kind===2?'Tab Group':kind===1?'Tab':'Window';
    const name=value.target?targetName({name:value.target,app:value.targetApp,windowName:value.targetWindowName,tabPosition:value.targetPosition},kind):'';
    const label=value.target?`${type}: ${name}`:'No focus target selected.';
    targetButtons.forEach(button=>{
      if(value.target){
        if(!button.querySelector('.focus-target-type')){
          const heading=document.createElement('strong'),name=document.createElement('span');
          heading.className='focus-target-type';name.className='focus-target-label';button.replaceChildren(heading,document.createTextNode(' '),name);
        }
        setText(button.querySelector('.focus-target-type'),type);setText(button.querySelector('.focus-target-label'),name);
        button.setAttribute('aria-label',label);
      }else {setText(button,'Choose Window / Tab…');button.removeAttribute('aria-label');}
      button.classList.toggle('primary',!!value.target);
      button.classList.toggle('has-target',!!value.target);
      button.title=value.target?`${label}. Choose another focus target.`:'Choose a window, browser tab, or tab group.';
    });
    setText($('focus-target-name'),value.target?'Choose another window, browser tab, or tab group.':'Choose a window, browser tab, or tab group.');
    setText($('focus-settings-target'),label);
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
    if(loading||selecting)return;loading=true;$('focus-target-kind').disabled=true;$('focus-target-refresh').disabled=true;list.disabled=true;use.disabled=true;
    setText($('focus-picker-status'),'Loading open targets…');
    try {await send('focusTargets',{kind:Number($('focus-target-kind').value),quiet:true});}
    catch(error){setText($('focus-picker-status'),error.message);throw error;}
    finally{loading=false;$('focus-target-kind').disabled=false;$('focus-target-refresh').disabled=false;}
  }
  function open(button,enable=false){
    if(selecting||dialog.open)return;
    opener=button;enableOnChoose=enable;$('focus-target-kind').value=String(settings.targetKind??0);
    dialog.showModal();$('focus-target-kind').focus();run(refresh);
  }
  for(const toggle of toggles)toggle.addEventListener('click',()=>{
    if(!enabled&&!settings.target){open(toggle,true);return;}
    enabled=!enabled;syncToggles();dirty=true;revision++;run(flush);
  });
  delay.addEventListener('input',()=>{dirty=true;revision++;});
  delay.addEventListener('change',()=>run(flush));
  delay.addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.ctrlKey&&!event.altKey&&!event.isComposing){event.preventDefault();run(flush);}});
  for(const button of targetButtons){
    button.addEventListener('click',()=>open(button));
  }
  for(const button of [...targetButtons,...toggles])button.addEventListener('keydown',event=>{if(event.repeat&&(event.key==='Enter'||event.key===' '))event.preventDefault();});
  $('focus-target-kind').addEventListener('change',()=>run(refresh));
  $('focus-target-refresh').addEventListener('click',()=>run(refresh));
  async function useSelection(){
    const id=list.value;if(selecting||loading||!dialog.open||list.disabled||use.disabled||!id)return;
    selecting=true;const active=document.activeElement;
    const controls=[$('focus-target-kind'),$('focus-target-refresh'),list,use];
    controls.forEach(control=>control.disabled=true);dialog.setAttribute('aria-busy','true');
    try {await flush();await send('focusSelect',{id,enable:enableOnChoose,quiet:true});dialog.close();announce('Focus target saved.');}
    catch(error){
      controls.forEach(control=>control.disabled=false);setText($('focus-picker-status'),error.message);
      if(dialog.open)active?.focus();throw error;
    }
    finally {selecting=false;dialog.removeAttribute('aria-busy');controls.forEach(control=>control.disabled=false);use.disabled=!list.value;}
  }
  use.addEventListener('click',()=>run(useSelection));
  const plainKey=event=>!event.ctrlKey&&!event.altKey&&!event.metaKey&&!event.shiftKey&&!event.isComposing;
  list.addEventListener('keydown',event=>{
    if(!plainKey(event)||(event.key!=='Enter'&&event.key!==' '))return;
    event.preventDefault();event.stopPropagation();
    if(event.repeat)return;
    if(event.key==='Enter')run(useSelection);else spacePressed=true;
  });
  // Space activates on release, as with a native button. This prevents the same
  // keyup from reopening the picker after focus returns to its opener.
  list.addEventListener('keyup',event=>{
    if(event.key!==' ')return;
    const activate=spacePressed&&plainKey(event);spacePressed=false;
    event.preventDefault();event.stopPropagation();if(activate)run(useSelection);
  });
  list.addEventListener('blur',()=>spacePressed=false);
  list.addEventListener('dblclick',event=>{if(plainKey(event)){event.preventDefault();run(useSelection);}});
  $('focus-target-cancel').addEventListener('click',()=>dialog.close());
  dialog.addEventListener('close',()=>{syncToggles();opener?.focus();});
  return {render,flush,state(state){if(view==='main')render(state.focusMode);},message(message){
    if(view!=='main')return;
    if(message.type==='focusStatus')setText($('focus-status'),message.status);
    if(message.type==='settings')render(message.settings.focusMode);
    if(message.type==='focusTargets'&&dialog.open&&Number($('focus-target-kind').value)===message.kind){
      const choices=message.targets.map((target,index)=>({value:target.id,label:targetName(target,message.kind,target.tabPosition??index+1)}));
      setOptions($('focus-target-list'),choices,message.targets.find(target=>target.selected)?.id??choices[0]?.value??'');
      $('focus-target-list').disabled=choices.length===0;$('focus-target-use').disabled=choices.length===0;
      setText($('focus-picker-status'),choices.length?`${choices.length} open targets. Choose one, then use selected target.`:message.kind===2?'No readable browser tab groups found. Open a group in Chrome or Edge and refresh, or choose Browser Tab or Window instead.':message.kind===1?'No readable browser tabs found. Try Window instead.':'No selectable windows found. Open the target and refresh.');
    }
  }};
}
