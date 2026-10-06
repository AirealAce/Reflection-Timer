import {setText} from './ui.js';

export function mountFocusMode({send,run,announce,view,flushAudio,validateAudio}) {
  const $=id=>document.getElementById(id),dialog=$('focus-target-dialog'),toggles=[$('focus-enabled'),$('focus-settings-enabled')];
  const targetButtons=[$('choose-focus-target'),$('choose-focus-target-settings')],list=$('focus-target-list'),use=$('focus-target-use');
  const multiple=$('focus-multiple-targets'),idle=$('focus-idle-enabled'),idleSeconds=$('focus-idle-seconds'),kindControl=$('focus-target-kind'),delay=$('focus-delay');
  const glowControls=[$('focus-screen-glow'),$('focus-picker-screen-glow')];
  const glowStyles=[$('focus-glow-style'),$('focus-picker-glow-style')];
  let glowEnabled=true,glowStyle=0,glowDirty=false,glowRevision=0,glowPending=null;
  function syncGlow(){glowControls.forEach(control=>control.checked=glowEnabled);glowStyles.forEach(control=>{control.value=String(glowStyle);control.disabled=!glowEnabled;});}
  function flushGlow(){
    if(glowPending)return glowPending;
    glowPending=(async()=>{while(glowDirty){const captured=glowRevision;await send('focusAnimation',{enabled:glowEnabled,style:glowStyle,quiet:true});if(captured===glowRevision)glowDirty=false;}})().finally(()=>glowPending=null);
    return glowPending;
  }
  for(const control of glowControls)control.addEventListener('change',()=>{glowEnabled=control.checked;glowDirty=true;glowRevision++;syncGlow();run(flushGlow);});
  for(const control of glowStyles)control.addEventListener('change',()=>{glowStyle=Number(control.value);glowDirty=true;glowRevision++;syncGlow();run(flushGlow);});
  for(const toggle of toggles){toggle.setAttribute('aria-keyshortcuts','Control+Alt+;');toggle.title='Ctrl+Alt+;: toggle Focus mode.';}
  let settings={enabled:false,delaySeconds:5,targets:[]},enabled=false,dirty=false,revision=0,pending=false,saving=Promise.resolve();
  let opener,enableOnChoose=false,loading=false,selecting=false,spaceRow=null,rows=[],activeKey=null,picked=new Map(),resetList=false,composing=false,targetRevision=0;
  const type=kind=>['Window','Tab','Tab Group'][kind]??'Window';
  const key=target=>target.key??`${target.kind}:${target.id}`;
  const selectedTargets=value=>value.targets?.length?value.targets:value.target?[{name:value.target,kind:value.targetKind??0,app:value.targetApp,windowName:value.targetWindowName,tabPosition:value.targetPosition}]:[];
  const shortName=target=>target.useFocused?target.name:target.kind===0?target.app||target.name:target.kind===1?`${target.tabPosition??'?'} - ${target.name}`:target.name;
  const fullName=target=>target.useFocused?target.name:`${type(target.kind)}: ${shortName(target)}`+(target.kind===0?` — ${target.displayName??target.name}`:target.windowName?` — ${target.app}, ${target.windowName}`:` — ${target.app}`);
  const syncToggles=()=>toggles.forEach(control=>{control.setAttribute('aria-pressed',String(enabled));control.classList.toggle('primary',enabled);});
  function render(value){
    if(!value)return;settings=value;toggles.forEach(control=>control.disabled=false);
    if(!glowDirty){glowEnabled=value.screenEdgeGlow!==false;glowStyle=value.screenEdgeGlowStyle??0;syncGlow();}
    if(!dirty){enabled=!!value.enabled;syncToggles();delay.value=value.delaySeconds??5;}
    const targets=selectedTargets(value),many=targets.length>1,heading=many?[0,1,2].filter(kind=>targets.some(t=>t.kind===kind)).map(type).join(', '):targets.length?type(targets[0].kind):'';
    const name=targets.length&&!many?shortName(targets[0]):'',label=targets.length?many?heading:targets[0].useFocused?name:`${heading}: ${name}`:'No focus target selected.';
    targetButtons.forEach(button=>{
      if(targets.length){
        if(!button.querySelector('.focus-target-type')){
          const heading=document.createElement('strong'),name=document.createElement('span');heading.className='focus-target-type';name.className='focus-target-label';button.replaceChildren(heading,document.createTextNode(' '),name);
        }
        setText(button.querySelector('.focus-target-type'),heading);setText(button.querySelector('.focus-target-label'),name);button.querySelector('.focus-target-label').hidden=many;
        button.setAttribute('aria-label',label);
      }else {setText(button,'Choose Window / Tab…');button.removeAttribute('aria-label');}
      button.classList.toggle('primary',targets.length>0);button.classList.toggle('has-target',targets.length>0);
      button.title=targets.length?targets.map(fullName).join('\n')+'\nChoose focus targets.':'Choose a window, browser tab, or tab group.';
    });
    setText($('focus-target-name'),targets.length?`${targets.length} selected. Choose focus targets.`:'Choose a window, browser tab, or tab group.');
    setText($('focus-settings-target'),label+(value.idleEnabled?` · Idle for ${value.idleSeconds??20} seconds`:''));
  }
  async function flush(){await flushGlow();await flushMode();}
  function flushMode(){
    if(pending)return saving;
    if(!delay.checkValidity()){if(!dialog.open)open(document.body.dataset.tab==='settings'?targetButtons[1]:targetButtons[0]);delay.reportValidity();return Promise.reject(new Error('Enter a focus delay of zero or more whole seconds.'));}
    pending=true;saving=(async()=>{
      while(dirty){const captured=revision;await send('focusMode',{enabled,delaySeconds:Number(delay.value),quiet:true});if(captured===revision)dirty=false;}
    })().finally(()=>pending=false);return saving;
  }
  function syncSelection(){
    const selected=[...picked.values()],backgroundTabs=selected.some(t=>t.kind===1&&t.useFocused&&t.captureScope===1);
    $('focus-picker-tab-scope').hidden=!backgroundTabs;
    $('focus-picker-target-overlap').hidden=!backgroundTabs||!selected.some(t=>t.kind===2||t.kind===0&&(t.useFocused||['chrome','msedge','firefox','brave','vivaldi','opera'].includes(t.app?.toLowerCase())));
    for(const row of list.querySelectorAll('tbody tr')){
      const checked=picked.has(row.dataset.key);row.classList.toggle('picked',checked);row.setAttribute('aria-current',checked&&!multiple.checked?'true':'false');
      const checkbox=row.querySelector('input');if(checkbox){checkbox.checked=checked;checkbox.disabled=loading||selecting;}
    }
    use.disabled=loading||selecting||((enableOnChoose||enabled)&&!picked.size&&!idle.checked);
    setText(use,multiple.checked?'Save selected targets':'Use selected target');
    setText($('focus-picker-keys'),(multiple.checked?'Arrow keys move between rows. Enter, Space, double-click, or check a box to toggle a target. Choices stay checked across categories.':'Arrow keys move between rows. Enter, Space, or double-click confirms the selected target.')+' Ctrl+Enter or Ctrl+S saves from anywhere in this window. Dynamic choices capture on start/resume. Background choices include other windows; focused tabs means the selected tab in each browser window. Switching modes keeps the capture.');
  }
  function status(){
    const available=rows.filter(t=>!t.unavailable&&!t.useFocused).length;
    setText($('focus-picker-status'),`${available} open targets. ${picked.size} selected across all categories.`+(rows.some(t=>t.unavailable)?' Previously selected closed targets can be unchecked.':''));
  }
  function focusRow(target){
    activeKey=key(target);
    for(const row of list.querySelectorAll('tbody tr')){
      const focused=row.dataset.key===activeKey;row.tabIndex=!multiple.checked&&focused?0:-1;
      const checkbox=row.querySelector('input');if(checkbox)checkbox.tabIndex=focused?0:-1;
      if(focused)(checkbox??row).focus();
    }
  }
  function choose(target){
    if(loading||selecting)return;
    const identity=key(target);
    if(multiple.checked){if(picked.has(identity))picked.delete(identity);else picked.set(identity,target);}
    else {picked.clear();picked.set(identity,target);}
    syncSelection();status();
  }
  function draw(){
    const kind=Number(kindControl.value),headers=kind===0?['App','Window Name']:kind===1?['Tab #','Tab Name','App']:['Grp #','Group Name','App'];
    const widths=kind===0?['30%','']:['64px','','24%'];
    if(multiple.checked){headers.unshift('');widths.unshift('32px');}
    const group=document.createElement('colgroup');for(const width of widths){const col=document.createElement('col');if(width)col.style.width=width;group.append(col);}
    list.querySelector('colgroup').replaceWith(group);
    const head=document.createElement('tr');for(const text of headers){const th=document.createElement('th');th.scope='col';th.textContent=text;if(text.endsWith(' #'))th.className='focus-number-cell';if(!text){th.className='focus-selection-cell';th.setAttribute('aria-label','Selection');}head.append(th);}list.querySelector('thead').replaceChildren(head);
    const body=list.querySelector('tbody');body.replaceChildren();
    for(const target of rows){
      const row=document.createElement('tr');row.dataset.key=key(target);row.dataset.id=target.id;row.tabIndex=-1;row.title=fullName(target);row.classList.toggle('unavailable',!!target.unavailable);
      const description=fullName(target)+(target.current?' (current tab)':'')+(target.unavailable?' (closed or unavailable)':'');
      row.setAttribute('aria-label',description);
      if(multiple.checked){
        const td=document.createElement('td'),checkbox=document.createElement('input');td.className='focus-selection-cell';checkbox.type='checkbox';checkbox.tabIndex=-1;checkbox.setAttribute('aria-label',description);checkbox.disabled=loading||selecting;
        checkbox.addEventListener('change',()=>{choose(target);});checkbox.addEventListener('click',event=>{if(event.detail>1)event.preventDefault();});td.append(checkbox);row.append(td);
      }
      const values=target.useFocused?(kind===0?[target.name,'']:['',target.name,'']):kind===0?[target.app,target.displayName??target.name]:[String(target.tabPosition??'?'),(target.current?'(current tab) ':'')+target.name,target.app];
      for(const [index,value] of values.entries()){const td=document.createElement('td'),text=document.createElement('span');if(kind!==0&&index===0)td.className='focus-number-cell';text.className='focus-cell';text.textContent=value;td.append(text);row.append(td);}
      row.addEventListener('focusin',()=>{
        activeKey=key(target);for(const other of body.children){other.tabIndex=!multiple.checked&&other===row?0:-1;const checkbox=other.querySelector('input');if(checkbox)checkbox.tabIndex=other===row?0:-1;}
        if(!multiple.checked&&!loading&&!selecting){picked.clear();picked.set(key(target),target);syncSelection();status();}
      });
      row.addEventListener('click',event=>{if(event.target.closest('input')||event.detail>1)return;choose(target);focusRow(target);});
      row.addEventListener('dblclick',event=>{if(event.ctrlKey||event.altKey||event.metaKey||loading||selecting)return;event.preventDefault();if(!multiple.checked)run(useSelection);});
      body.append(row);
    }
    const initial=rows.find(t=>key(t)===activeKey)??rows.find(t=>picked.has(key(t)))??rows[0];
    activeKey=initial?key(initial):null;
    const row=[...body.children].find(row=>row.dataset.key===activeKey);if(row){const checkbox=row.querySelector('input');if(checkbox)checkbox.tabIndex=0;else row.tabIndex=0;}
    syncSelection();status();
  }
  async function refresh(){
    if(loading||selecting)return;const active=document.activeElement;loading=true;kindControl.disabled=true;$('focus-target-refresh').disabled=true;use.disabled=true;list.inert=true;list.setAttribute('aria-busy','true');
    setText($('focus-picker-status'),'Loading open targets…');
    try {await send('focusTargets',{kind:Number(kindControl.value),reset:resetList,quiet:true});resetList=false;}
    catch(error){setText($('focus-picker-status'),error.message);throw error;}
    finally{loading=false;kindControl.disabled=false;$('focus-target-refresh').disabled=false;list.inert=false;list.setAttribute('aria-busy','false');syncSelection();
      if(dialog.open&&dialog.contains(active)&&!active.disabled&&(document.activeElement===document.body||document.activeElement===dialog))active.focus();
    }
  }
  function open(button,enable=false){
    if(selecting||loading||dialog.open)return;
    opener=button;enableOnChoose=enable;multiple.checked=!!settings.multipleTargets;idle.checked=!!settings.idleEnabled;idleSeconds.value=settings.idleSeconds??20;$('focus-picker-audio').open=false;$('focus-picker-animations').open=false;
    picked=new Map(selectedTargets(settings).filter(t=>t.id).map(t=>[key(t),t]));rows=[];activeKey=null;resetList=true;
    kindControl.value=String(selectedTargets(settings)[0]?.kind??settings.targetKind??0);draw();dialog.showModal();kindControl.focus();run(refresh);
  }
  for(const toggle of toggles)toggle.addEventListener('click',()=>{
    if(!enabled&&!selectedTargets(settings).length&&!settings.idleEnabled){open(toggle,true);return;}
    enabled=!enabled;syncToggles();dirty=true;revision++;run(flush);
  });
  delay.addEventListener('input',()=>{dirty=true;revision++;});delay.addEventListener('change',()=>run(flush));
  delay.addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.ctrlKey&&!event.altKey&&!event.isComposing){event.preventDefault();run(flush);}});
  for(const button of targetButtons)button.addEventListener('click',()=>open(button));
  for(const button of [...targetButtons,...toggles])button.addEventListener('keydown',event=>{if(event.repeat&&(event.key==='Enter'||event.key===' '))event.preventDefault();});
  kindControl.addEventListener('change',()=>{activeKey=null;run(refresh);});$('focus-target-refresh').addEventListener('click',()=>run(refresh));
  multiple.addEventListener('change',()=>{
    if(!multiple.checked&&picked.size>1){const keep=picked.get(activeKey)??picked.values().next().value;picked=new Map([[key(keep),keep]]);announce('Multiple Targets off. Only one selected target is kept.');}
    draw();
  });
  idle.addEventListener('change',syncSelection);
  async function useSelection(){
    if(selecting||loading||!dialog.open||use.disabled)return;
    if(!delay.checkValidity()){delay.reportValidity();return;}
    if(!idleSeconds.checkValidity()){idleSeconds.reportValidity();return;}
    if(!validateAudio())return;
    selecting=true;let failure;const active=document.activeElement,controls=[...dialog.querySelectorAll('button,input,select')].map(control=>({control,disabled:control.disabled}));
    controls.forEach(({control})=>control.disabled=true);list.inert=true;dialog.setAttribute('aria-busy','true');
    try{
      await flushAudio();
      await flush();
      let savedRevision;
      do {
        savedRevision=targetRevision;
        await send('focusSelect',{ids:[...picked.values()].map(t=>t.id),enable:enableOnChoose,multipleTargets:multiple.checked,idleEnabled:idle.checked,idleSeconds:Number(idleSeconds.value),quiet:true});
      }while(savedRevision!==targetRevision);
      dialog.close();announce('Focus settings saved.');
    }catch(error){failure=error.message;throw error;}
    finally{selecting=false;dialog.removeAttribute('aria-busy');list.inert=false;controls.forEach(({control,disabled})=>control.disabled=disabled);syncSelection();if(dialog.open)active?.focus();if(failure)setText($('focus-picker-status'),failure);}
  }
  use.addEventListener('click',()=>run(useSelection));
  const plainKey=event=>!event.ctrlKey&&!event.altKey&&!event.metaKey&&!event.shiftKey&&!event.isComposing;
  list.addEventListener('keydown',event=>{
    if(!plainKey(event)||loading||selecting)return;
    const row=event.target.closest('tr[data-key]');if(!row)return;
    const index=rows.findIndex(t=>key(t)===row.dataset.key);
    if(['ArrowDown','ArrowUp','Home','End'].includes(event.key)){
      event.preventDefault();event.stopPropagation();const next=event.key==='Home'?0:event.key==='End'?rows.length-1:Math.max(0,Math.min(rows.length-1,index+(event.key==='ArrowDown'?1:-1)));focusRow(rows[next]);return;
    }
    if(event.key!=='Enter'&&event.key!==' ')return;event.preventDefault();event.stopPropagation();if(event.repeat)return;
    if(event.key==='Enter'){if(multiple.checked)choose(rows[index]);else run(useSelection);}else spaceRow=rows[index];
  });
  list.addEventListener('keyup',event=>{
    if(event.key!==' ')return;event.preventDefault();event.stopPropagation();const target=spaceRow;spaceRow=null;
    if(target&&plainKey(event)&&!loading&&!selecting){if(multiple.checked)choose(target);else run(useSelection);}
  });
  list.addEventListener('focusout',()=>spaceRow=null);
  document.addEventListener('compositionstart',()=>composing=true);document.addEventListener('compositionend',()=>composing=false);
  document.addEventListener('keydown',event=>{
    if(view!=='main'||!dialog.open||!event.ctrlKey||event.altKey||event.metaKey||event.shiftKey||event.isComposing||composing||(event.key!=='Enter'&&event.key.toLowerCase()!=='s'))return;
    event.preventDefault();event.stopImmediatePropagation();if(!event.repeat)run(useSelection);
  },true);
  $('focus-target-cancel').addEventListener('click',()=>dialog.close());dialog.addEventListener('cancel',event=>{if(selecting)event.preventDefault();});
  dialog.addEventListener('close',()=>{spaceRow=null;syncToggles();opener?.focus();});
  return {render,flush,pickerShortcutActive:()=>view==='main'&&dialog.open&&!composing,state(state){if(view==='main')render(state.focusMode);},message(message){
    if(view!=='main')return false;
    if(message.type==='focusToggled'){
      enabled=message.enabled===true;settings={...settings,enabled};syncToggles();if(dirty)revision++;return true;
    }
    if(message.type==='focusChooseShortcut'){if(!document.querySelector('dialog[open]'))open(toggles[0],true);return true;}
    if(message.type==='focusTargetToggled'&&dialog.open){
      // A global checkbox toggle is a deliberate edit to this draft as well.
      // Update only its target; retain every unrelated unsaved choice/option.
      const target=message.target,affected=new Set(message.keys??[key(target)]);
      targetRevision++;
      enabled=message.enabled===true;settings={...settings,enabled};syncToggles();if(dirty)revision++;
      const focused=list.contains(document.activeElement)?document.activeElement.closest('tr')?.dataset.key:null;
      const oldMultiple=multiple.checked;let redraw=false;
      for(const identity of affected)picked.delete(identity);
      if(message.checked){picked.set(key(target),target);if(message.multipleTargets||picked.size>1)multiple.checked=true;}
      const remaining=rows.filter(t=>!t.unavailable||!affected.has(key(t))||message.checked&&key(t)===key(target));
      if(remaining.length!==rows.length){rows=remaining;redraw=true;}
      const existing=rows.findIndex(t=>key(t)===key(target));
      if(message.checked&&existing>=0&&rows[existing].unavailable){rows[existing]=target;redraw=true;}
      if(message.checked&&Number(kindControl.value)===target.kind&&!rows.some(t=>key(t)===key(target))){rows.push(target);redraw=true;}
      if(redraw||oldMultiple!==multiple.checked){
        draw();
        if(focused&&document.hasFocus()){
          const row=[...list.querySelectorAll('tbody tr')].find(row=>row.dataset.key===focused);
          (row?.querySelector('input')??row??kindControl).focus();
        }
      }else {syncSelection();status();}
      return true;
    }
    if(message.type==='settingsSaveShortcut'&&dialog.open){if(!composing)run(useSelection);return true;}
    if(message.type==='focusStatus')setText($('focus-status'),message.status);
    if(message.type==='settings')render(message.settings.focusMode);
    if(message.type==='focusTargets'&&dialog.open&&Number(kindControl.value)===message.kind){
      const keys=new Set();rows=message.targets.map(t=>({...t,kind:message.kind})).filter(t=>{const identity=key(t);if(keys.has(identity))return false;keys.add(identity);return true;});
      rows.sort((a,b)=>Number(!!b.useFocused)-Number(!!a.useFocused)||(message.kind===1?Number(!!b.current)-Number(!!a.current):0));
      for(const target of rows){
        const identity=key(target);let chosen=picked.has(identity);
        for(const previous of target.replacesKeys??[]){
          if(picked.delete(previous))chosen=true;
          if(activeKey===previous)activeKey=identity;
        }
        if(chosen)picked.set(identity,target);
      }
      if(!multiple.checked&&!picked.size){const initial=rows.find(t=>t.selected)??rows[0];if(initial)picked.set(key(initial),initial);}
      if(!multiple.checked&&picked.size&&![...picked.values()].some(t=>t.kind===message.kind)&&rows.length){picked.clear();const initial=rows.find(t=>t.selected)??rows[0];picked.set(key(initial),initial);}
      for(const target of picked.values())if(target.kind===message.kind&&!keys.has(key(target)))rows.push({...target,unavailable:true});
      draw();
    }
    return false;
  }};
}
