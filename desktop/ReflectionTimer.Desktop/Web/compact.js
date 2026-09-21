import {setText,formatClock,displayClock,durationSeconds,normalizeEmptyDuration,bindTimerEditor,announceSelectChanges,bindResetAndReload,focusTimerControl} from './ui.js';
announceSelectChanges();
const $=id=>document.getElementById(id),bridge=window.chrome?.webview,requests=new Map();
const requestPrefix=crypto.randomUUID();
let sequence=0,state,dirty=false,tiny=false,revealed=false,lastRunning=false,lastDeadline,repeatPending=false;
function send(action,data={}){return new Promise((resolve,reject)=>{const requestId=`${requestPrefix}:${++sequence}`;if(!bridge)return reject(new Error('Open the compact timer through Reflection Timer.'));const timeout=['reset','resetAndReload'].includes(action)?undefined:setTimeout(()=>{requests.delete(requestId);reject(new Error('The app did not respond.'));},35000);requests.set(requestId,{resolve,reject,timeout});bridge.postMessage({requestId,action,data});});}
function run(action){setText($('error'),'');Promise.resolve().then(action).catch(e=>setText($('error'),e.message));}
function bind(id,action){$(id).addEventListener('click',()=>{if($(id).getAttribute('aria-disabled')!=='true')run(action);});}
function announce(text){setText($('status'),'');setTimeout(()=>setText($('status'),text),50);}
function duration(){return durationSeconds(['hours','minutes','seconds'].map(id=>$(id).value.trim()));}
function repeatEnabled(){return $('repeat').getAttribute('aria-pressed')==='true';}
function setRepeat(enabled){const value=String(Boolean(enabled));if($('repeat').getAttribute('aria-pressed')!==value)$('repeat').setAttribute('aria-pressed',value);}
function setAppVisibility(visible){
  const shown=Boolean(visible),value=String(shown);
  if($('app').dataset.appVisible!==value)$('app').dataset.appVisible=value;
  setText($('app-view-status'),shown?'App view is visible.':'App view is hidden or minimized.');
  $('app').title=shown?'Bring App view to front':'Show App view';
}
function fill(seconds){$('hours').value=Math.floor(seconds/3600);$('minutes').value=Math.floor(seconds/60)%60;$('seconds').value=seconds%60;}
function sharedDuration(parts){dirty=Array.isArray(parts);if(parts)['hours','minutes','seconds'].forEach((id,i)=>{if($(id).value!==parts[i])$(id).value=parts[i];});else if(state)fill(state.timer.durationSeconds);if(state?.clock.status!=='Running')renderDuration();}
function renderDuration(clock=state?.clock){
  if(!clock)return;
  try{setText($('visual-clock'),formatClock(displayClock(clock,['hours','minutes','seconds'].map(id=>$(id).value),dirty).seconds));}
  catch{ /* Keep the last valid time while an invalid value is being edited. */ }
}
function resize(){if(state)send('compactSize',{width:Math.ceil(document.body.getBoundingClientRect().width),height:Math.ceil(document.body.getBoundingClientRect().height),tiny}).catch(e=>setText($('error'),e.message));}
function mode(value){tiny=value;document.body.dataset.tiny=String(value);$('shrink').setAttribute('aria-label',value?'Hide compact timer':'Shrink to time-only view');$('expand').setAttribute('aria-label',value?'Expand compact view':'Open main timer page');['shrink','expand'].forEach(id=>$(id).title=$(id).getAttribute('aria-label'));}
function expand(){revealed=true;mode(false);focusTimerControl(state?.timer.mode===1);}
function shrink(){revealed=false;mode(true);$('read-time').focus();}
function snapshot(clock,speak=false){try{clock=displayClock(clock,['hours','minutes','seconds'].map(id=>$(id).value),dirty);}catch{}const text=`${clock.text} ${clock.stopwatch?'elapsed':clock.status==='Finished'?'set':'remaining'}. ${clock.status}.`;setText($('time-snapshot'),`Time checked: ${text}`);if(speak)announce(text);}
function render(next,keepTimeOnly=false){const previous=state;state=next;document.documentElement.dataset.theme=String(state.theme??0);const running=state.clock.status==='Running';
  const stopwatch=state.timer.mode===1;
  document.body.dataset.stopwatch=String(stopwatch);
  setText($('caption').querySelector('h1'),stopwatch?'Stopwatch':'Reflection Timer');
  setText($('session-mode'),stopwatch?'T':'S');
  $('session-mode').title=stopwatch?'Switch to Timer (pauses stopwatch)':'Switch to Stopwatch (pauses timer)';
  $('session-mode').setAttribute('aria-label',$('session-mode').title);
  $('read-time').setAttribute('aria-label',stopwatch?'Read elapsed time':'Read remaining time');
  $('read-time').title=stopwatch?'Read elapsed time':'Read remaining time';
  if(!previous&&state.durationDraft)sharedDuration(state.durationDraft);
  else if(!previous||(!dirty&&state.timer.durationSeconds!==previous.timer.durationSeconds))fill(state.timer.durationSeconds);
  if(!previous||previous.clock.status!==state.clock.status)snapshot(state.clock);
  if(running!==lastRunning||lastDeadline!==state.timer.endTime){revealed=false;if(running||!tiny||!keepTimeOnly)mode(running);}lastRunning=running;lastDeadline=state.timer.endTime;
  if(!repeatPending)setRepeat(state.timer.autoRestart);['hours','minutes','seconds'].forEach(id=>$(id).readOnly=running);
  const action=running?'Pause':state.clock.status==='Paused'&&(!dirty||stopwatch)?'Resume':'Start';$('toggle').setAttribute('aria-label',`${action} ${stopwatch?'stopwatch':'timer'}`);$('toggle').title=$('toggle').getAttribute('aria-label');setText($('toggle').firstElementChild,running?'Ⅱ':'▶');
  $('end').setAttribute('aria-disabled',String(!(running||stopwatch&&state.clock.status==='Paused')));$('reset').setAttribute('aria-disabled',String((stopwatch||!dirty)&&state.clock.status==='Ready'));
  $('end').title=stopwatch?'Pause and reflect':'End timer early';$('end').setAttribute('aria-label',$('end').title);
  renderDuration();
  document.body.style.setProperty('--tiny-width',`${Math.max(96,formatClock(stopwatch?state.clock.seconds:state.timer.durationSeconds).length*15+16)}px`);
  if(tiny&&['hours','minutes','seconds','repeat','app','reset','end'].includes(document.activeElement.id))$('read-time').focus();
}
bridge?.addEventListener('message',event=>{const m=event.data;if(m.type==='reply'){const p=requests.get(m.requestId);if(!p)return;clearTimeout(p.timeout);requests.delete(m.requestId);m.error?p.reject(new Error(m.error)):p.resolve(m);}
  else if(m.type==='init'){render(m.state);if(typeof m.timeOnly==='boolean'){mode(m.timeOnly);revealed=!m.timeOnly;}setAppVisibility(m.appViewVisible);if(m.state.durationDraft)sharedDuration(m.state.durationDraft);resize();restoreReloadView();send('interfaceReady').catch(e=>setText($('error'),e.message));}
  else if(m.type==='appViewVisibility')setAppVisibility(m.visible);
  else if(m.type==='state')render(m.state,m.keepTimeOnly===true);
  else if(m.type==='clock'){if(state?.clock.status===m.clock.status&&Boolean(m.clock.stopwatch)===(state?.timer.mode===1)){renderDuration(m.clock);if(m.clock.stopwatch)document.body.style.setProperty('--tiny-width',`${Math.max(96,formatClock(m.clock.seconds).length*15+16)}px`);}}
  else if(m.type==='timeRead')snapshot(m.clock,true);
  else if(m.type==='announcement')announce(m.message);
  else if(m.type==='durationDraft')sharedDuration(m.parts);
  else if(m.type==='expandCompact')expand();
  else if(m.type==='shrinkCompact')shrink();
  else if(m.type==='measureCompact')resize();
});
const restoreReloadView=bindResetAndReload({bridge,send,run,canReset:()=>Boolean(state)});
['hours','minutes','seconds'].forEach(id=>{
  const changed=()=>{const parts=['hours','minutes','seconds'].map(id=>$(id).value);sharedDuration(parts);run(()=>send('durationDraft',{parts}));$('reset').setAttribute('aria-disabled','false');};
  $(id).addEventListener('input',changed);normalizeEmptyDuration($(id),changed);
});
bindTimerEditor($('timer-editor'),['hours','minutes','seconds'].map($),run,async submitter=>{
  const keepTimeOnly=tiny&&submitter===$('toggle');
  if(state?.timer.mode===1){await send('toggle',{keepTimeOnly});return;}
  if(state?.clock.status==='Running'){await send('toggle',{keepTimeOnly});return;}
  await send('toggle',{seconds:duration(),repeat:repeatEnabled(),lowTime:state.timer.enabled,threshold:state.timer.threshold,keepTimeOnly});dirty=false;fill(state.timer.durationSeconds);
});
bind('repeat',async()=>{
  if(repeatPending)return;
  const enabled=!repeatEnabled();repeatPending=true;setRepeat(enabled);$('repeat').setAttribute('aria-disabled','true');
  try{await send('repeat',{enabled});}
  catch(error){setRepeat(state?.timer.autoRestart);throw error;}
  finally{repeatPending=false;$('repeat').removeAttribute('aria-disabled');}
});
bind('read-time',()=>send('readTime'));bind('app',()=>send('main'));bind('end',()=>send('end'));
bind('reset',async()=>{const reply=await send('reset',state?.timer.mode===1?{}:{seconds:duration()});if(reply?.cancelled)return;if(state?.timer.mode!==1){dirty=false;fill(state.timer.durationSeconds);}});
bind('session-mode',()=>send('switchMode',{mode:state?.timer.mode===1?0:1}));
bind('close',()=>send('close'));bind('shrink',()=>tiny?send('close'):shrink());bind('expand',()=>tiny?expand():send('main'));
document.addEventListener('keydown',event=>{
  const timerKey=(event.key==='Enter'&&event.ctrlKey)||(event.key===' '&&!event.ctrlKey);
  if(!timerKey||event.altKey||event.metaKey||event.shiftKey||event.isComposing||document.querySelector('dialog[open]'))return;
  // Capture before duration fields or focused controls handle Enter or Space.
  // Reuse the form's validation and in-flight guard in both floating layouts.
  event.preventDefault();event.stopPropagation();
  if(state&&!event.repeat)$('timer-editor').requestSubmit(tiny&&event.target.closest?.('#toggle')?$('toggle'):undefined);
},true);
document.addEventListener('keydown',event=>{
  if(event.key!=='Escape'||!state||document.querySelector('dialog[open]'))return;
  event.preventDefault();
  // One press shrinks and focuses the clock; a second press hides the viewer.
  if(!event.repeat)$('shrink').click();
});
window.addEventListener('blur',()=>{if(revealed&&state?.clock.status==='Running'){revealed=false;mode(true);}});
new ResizeObserver(resize).observe(document.body);
$('caption').addEventListener('pointerdown',event=>{if(event.button===0&&!event.target.closest('button'))run(()=>send('dragCompact'));});
$('read-time').addEventListener('pointerdown',event=>{if(event.button===0&&tiny)run(()=>send('dragCompact'));});
run(()=>send('ready'));
