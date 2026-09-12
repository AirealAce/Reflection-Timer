import {setText, formatClock, displayClock, durationSeconds, normalizeEmptyDuration, bindTimerEditor, reconcileRows, announceSelectChanges} from './ui.js';
import {settingsUI, localDateTime} from './settings.js';
import {arrangeApp} from './layout.js';

const $ = id => document.getElementById(id);
announceSelectChanges();
let view = new URLSearchParams(location.search).get('view') || 'main';
if (!['main', 'compact', 'reflection'].includes(view)) view = 'main';
document.body.dataset.view = view;
setText($('page-title'), view === 'main' ? 'Reflection Timer' : view === 'compact' ? 'Compact timer' : 'How did you spend your time?');
const layout=arrangeApp(view);
const bridge = window.chrome?.webview;
const requests = new Map();
// A delayed reply from a crashed document must not resolve a new request.
const requestPrefix=crypto.randomUUID();
let requestSequence = 0, state, promptId, initial = true, durationDirty = false, loadedPrompt;
let reflectionBusy=false, savingAndClosing=false;
function setReflectionBusy(busy){
  reflectionBusy=busy;const blocked=busy||savingAndClosing;
  $('reflection-form').setAttribute('aria-busy',String(blocked));
  ['reflection-text','early-reason'].forEach(id=>$(id).readOnly=blocked);
  document.querySelectorAll('#reflection-form button').forEach(button=>available(button,!blocked));
  renderReflectionNavigation();
}
let saveDelay, saving = Promise.resolve(), queued = false, lastSavedDraft = '', scheduleEdit, deliveryDecision, selectedSchedule, selectedOutbox;

function send(action, data = {}) {
  return new Promise((resolve, reject) => {
    if (!bridge) { reject(new Error('Open this interface through the Reflection Timer app.')); return; }
    const requestId = `${requestPrefix}:${++requestSequence}`;
    const nativeDialog=['browseSound','browseLowSound','setupScript','exportDiagnostics','importSchedules'].includes(action);
    const timeout = nativeDialog ? undefined : setTimeout(() => { requests.delete(requestId); reject(new Error('The app did not respond. Check its status before trying again.')); }, 35000);
    requests.set(requestId, {resolve, reject, timeout});
    bridge.postMessage({requestId, action, data});
  });
}
function error(message) { setText($('error'), message); }
function announce(message) { setText($('status'), ''); setTimeout(() => setText($('status'), message), 60); }
function run(action) { error(''); Promise.resolve().then(action).catch(e => error(e.message)); }
function available(button, yes) { button.setAttribute('aria-disabled', String(!yes)); }
function bind(id, action) { $(id).addEventListener('click', () => { if ($(id).getAttribute('aria-disabled') !== 'true') run(action); }); }
function snapshot(clock, speak = false) {
  try{clock=displayClock(clock,['hours','minutes','seconds'].map(id=>$(id).value),durationDirty);}catch{}
  const text = `${clock.text} ${clock.status==='Finished'?'set':'remaining'}. ${clock.status}.`;
  setText($('time-snapshot'), `Time checked: ${text}`);
  if (speak) announce(text);
}
function readDuration() { return durationSeconds(['hours','minutes','seconds'].map(id => $(id).value.trim())); }
function applyDuration(seconds) {
  $('hours').value = Math.floor(seconds / 3600); $('minutes').value = Math.floor(seconds / 60) % 60; $('seconds').value = seconds % 60;
}
function sharedDuration(parts){
  durationDirty=Array.isArray(parts);
  if(parts)['hours','minutes','seconds'].forEach((id,i)=>{if($(id).value!==parts[i])$(id).value=parts[i];});else if(state)applyDuration(state.timer.durationSeconds);
  if(state?.clock.status!=='Running'){setText($('toggle'),durationDirty?'Start':state?.clock.status==='Paused'?'Resume':'Start');renderDuration();}
}
function renderDuration(clock=state?.clock){
  if(!clock)return;
  try{setText($('visual-clock'),formatClock(displayClock(clock,['hours','minutes','seconds'].map(id=>$(id).value),durationDirty).seconds));}
  catch{ /* Keep the last valid time while an invalid value is being edited. */ }
}
function draft() { return {id: promptId, text: $('reflection-text').value, reason: $('early-reason').value}; }
function saveDraft() {
  clearTimeout(saveDelay);
  if (!loadedPrompt || queued) return saving;
  const data = draft(), signature = JSON.stringify(data);
  if (signature === lastSavedDraft) return saving;
  saving = saving.catch(() => {}).then(async () => {
    await send('draft', data); lastSavedDraft = signature; setText($('draft-status'), 'Draft saved locally.');
  });
  return saving;
}
function renderReflection() {
  renderReflectionNavigation();
  const prompt = state.prompts.find(p => p.id === promptId);
  if (!prompt) return;
  setText($('reflection-heading'), prompt.isCheckIn ? 'Session check-in' : 'Session reflection');
  setText($('reflection-context'), `${prompt.endedEarly ? 'Session ended early. ' : ''}${prompt.actual} spent; ${prompt.allotted} allotted.`);
  setText($('reflection-timestamp'), prompt.completed);
  $('reason-group').hidden = !(prompt.showEarlyEndReason??prompt.endedEarly);
  if($('reason-group').hidden&&document.activeElement===$('early-reason')&&document.hasFocus())$('reflection-text').focus();
  if(loadedPrompt===prompt.id)return;
  loadedPrompt = prompt.id;
  $('reflection-text').value = prompt.draft; $('early-reason').value = prompt.earlyEndReason;
  lastSavedDraft = JSON.stringify(draft());
}
function renderReflectionNavigation(){
  const index=state?.prompts.findIndex(p=>p.id===promptId)??-1,count=state?.prompts.length??0;
  const ready=index>=0&&!queued&&!reflectionBusy&&!savingAndClosing;
  available($('reflection-prev'),ready&&index>0);
  available($('reflection-next'),ready&&index<count-1);
  setText($('reflection-position'),index>=0?`Reflection ${index+1} of ${count}. Navigation saves your draft without sending it.`:'No pending reflection.');
}
function createTableRow(columns) {
  const row = document.createElement('tr');
  Array.from({length:typeof columns==='number'?columns:columns.length}).forEach((_, index) => { const cell = document.createElement(index === 0 ? 'th' : 'td'); if (index === 0) cell.scope = 'row'; row.append(cell); });
  return row;
}
function reviewDelivery(id, action) {
  const uncertain=['write_uncertain','id_conflict'].includes(state.outbox.find(o=>o.id===id)?.error);
  deliveryDecision={id,action};
  setText($('delivery-explanation'),action==='retry'
    ? uncertain?'This receiver reported an uncertain write. Check your Google sheet first. Retrying creates a new request and could duplicate an entry already saved there.':'Check the Google Sheet first. Retrying an entry that already arrived can create a duplicate. Send it again?'
    : 'Check that this exact reflection is already in your Google sheet. Confirming removes it from pending delivery without sending it.');
  setText($('delivery-confirm'),action==='retry'?(uncertain?'I checked the sheet — retry as a new request':'Retry selected entry'):'I found this entry in my sheet — mark already sent');
  $('delivery-dialog').showModal();$('delivery-title').focus();
}
function clearScheduleEdit() {
  scheduleEdit=undefined;setText($('schedule-legend'),'Add a scheduled session');setText($('schedule-save'),'Add session');
  $('schedule-start').value=localDateTime(Date.now()+3600000);$('schedule-hours').value='0';$('schedule-minutes').value='15';$('schedule-seconds').value='0';$('schedule-repeat').checked=false;$('schedule-cutoff-enabled').checked=false;$('schedule-cutoff').value='';$('schedule-cutoff').disabled=true;$('schedule-volume').value='50';setText($('schedule-volume-caption'),'App sound (50%)');
  settings.resetScheduleLow();run(()=>send('editScheduleDraft',{id:null}));
}
function selectedRow(kind) {
  return (kind==='schedule'?state.schedules:state.outbox).find(r=>r.id===(kind==='schedule'?selectedSchedule:selectedOutbox));
}
function selectRow(kind,id) {
  if(kind==='schedule')selectedSchedule=id;else selectedOutbox=id;
  renderSelections();
}
function selectableRow(columns,kind) {
  const row=createTableRow(columns),radio=document.createElement('input'),text=document.createElement('span');
  radio.type='radio';radio.name=kind+'-selection';radio.className='row-selector';text.className='cell-text';row.cells[0].append(radio,text);
  radio.addEventListener('change',()=>{if(radio.checked)selectRow(kind,row.dataset.id);});
  row.addEventListener('click',()=>{radio.checked=true;selectRow(kind,row.dataset.id);});
  return row;
}
function renderSelections() {
  for(const [kind,id] of [['schedule',selectedSchedule],['outbox',selectedOutbox]]) {
    for(const row of $(kind+'-rows').children){const selected=row.dataset.id===id;row.dataset.selected=String(selected);row.querySelector('input').checked=selected;}
  }
  const schedule=selectedRow('schedule'),entry=selectedRow('outbox');
  available($('edit-schedule'),!!schedule);available($('remove-schedule'),!!schedule);
  $('schedule-decision').hidden=schedule?.status!=='Needs choice';
  available($('retry-selected'),!!entry&&!['Sent','Sending','Simulated success'].includes(entry.status)&&entry.localOnly===false);
  available($('mark-selected'),entry?.status==='NeedsReview'&&entry?.localOnly===false);
  $('local-preview-actions').hidden=!entry||entry.localOnly===false;
  let text='';
  if(entry){
    const actual=entry.actualDurationSeconds;
    text=entry.message+'\n\n'+(actual!=null?formatClock(actual)+' spent / ':'Actual time unavailable / ')+(entry.durationSeconds!=null?formatClock(entry.durationSeconds):entry.duration)+' allotted';
    if(entry.isCheckIn)text+=' · Check-in';
    else if(entry.endedEarly)text+=' · ended early\nReason: '+(entry.earlyEndReason||'Not supplied');
    if(entry.autoSent)text+='\nauto-sent';
    if(entry.status==='Pending'&&entry.nextAttemptAt)text+='\nNext retry: '+new Date(entry.nextAttemptAt).toLocaleTimeString();
    if(entry.status==='NeedsReview')text+='\nNeeds review: '+entry.error+'. Check the Sheet before retrying.';
    if(entry.localOnly!==false)text+='\nLocal preview only.';
  }
  setText($('outbox-detail'),text);
}
async function editSelectedSchedule() {
  const record=selectedRow('schedule');if(!record)return;scheduleEdit=record.id;
  $('schedule-start').value=record.editStart;$('schedule-hours').value=Math.floor(record.durationSeconds/3600);$('schedule-minutes').value=Math.floor(record.durationSeconds/60)%60;$('schedule-seconds').value=record.durationSeconds%60;
  $('schedule-repeat').checked=record.repeat==='On';$('schedule-volume').value=record.volume;
  setText($('schedule-volume-caption'),'App sound ('+record.volume+'%)');
  $('schedule-cutoff').value=localDateTime(record.autoRestartUntil);$('schedule-cutoff-enabled').checked=!!record.autoRestartUntil;$('schedule-cutoff').disabled=!record.autoRestartUntil;
  settings.resetScheduleLow();await send('editScheduleDraft',{id:record.id});
  setText($('schedule-legend'),'Edit scheduled session');setText($('schedule-save'),'Save changes');$('schedule-start').focus();
}
function tables() {
  if(!state.schedules.some(s=>s.id===selectedSchedule))selectedSchedule=state.schedules[0]?.id;
  const entries=[...state.outbox].reverse();
  if(!entries.some(s=>s.id===selectedOutbox))selectedOutbox=entries[0]?.id;
  reconcileRows($('schedule-rows'),state.schedules,()=>selectableRow(7,'schedule'),(row,record)=>{
    const values=[record.start,formatClock(record.durationSeconds),record.repeat,record.autoRestartUntil?new Date(record.autoRestartUntil).toLocaleString():'—',record.volume+'%',record.lowTime,record.status];
    setText(row.cells[0].querySelector('.cell-text'),values[0]);
    values.slice(1).forEach((value,i)=>setText(row.cells[i+1],value));
    row.querySelector('input').setAttribute('aria-label','Select session starting '+record.start);
  });
  $('schedule-empty').hidden=state.schedules.length!==0;
  reconcileRows($('outbox-rows'),entries,()=>selectableRow(4,'outbox'),(row,record)=>{
    setText(row.cells[0].querySelector('.cell-text'),record.saved);
    [record.destination,record.status+(record.autoSent?' · auto-sent':''),record.attempts].forEach((value,i)=>setText(row.cells[i+1],value));
    row.querySelector('input').setAttribute('aria-label','Select entry saved '+record.saved);
  });
  $('outbox-empty').hidden=state.outbox.length!==0;renderSelections();
}

function render(next) {
  const previous = state; state = next;
  settings.state(state);
  if (view === 'reflection') { renderReflection(); return; }
  setText($('timer-state'),state.clock.status==='Running'&&state.timer.endTime?'Running · ends '+new Date(state.timer.endTime).toLocaleTimeString([], {hour:'numeric',minute:'2-digit'}):state.clock.status);
  const running = state.clock.status === 'Running';
  ['hours','minutes','seconds'].forEach(id => $(id).readOnly = running);
  // Readonly duration controls remain focusable. No tick changes their values.
  if(initial&&state.durationDraft)sharedDuration(state.durationDraft);
  else if (initial || (!durationDirty && state.timer.durationSeconds !== previous?.timer.durationSeconds)) applyDuration(state.timer.durationSeconds);
  if (!previous || previous.clock.status !== state.clock.status) snapshot(state.clock);
  $('repeat').checked = state.timer.autoRestart;
  setText($('toggle'),running ? 'Pause' : state.clock.status === 'Paused' && !durationDirty ? 'Resume' : 'Start');
  available($('end'),running); available($('check-in'),running || state.clock.status === 'Paused');
  if (view === 'main') {
    setText($('pending-count'),`${state.prompts.length} pending reflection(s) · ${state.outbox.filter(o=>!['Sent','Simulated success'].includes(o.status)).length} unsent entry/entries`);
    reconcileRows($('pending-list'),state.prompts,record => {
      const li=document.createElement('li'), button=document.createElement('button'); button.type='button'; li.append(button);
      button.addEventListener('click',()=>run(()=>send('openReflection',{id:record.id}))); return li;
    },(li,record)=>setText(li.firstChild,`${record.isCheckIn ? 'Check-in' : 'Reflection'} from ${record.completed}`));
    tables();
  }
  initial = false;
  renderDuration();
}
bridge?.addEventListener('message', event => {
  const message = event.data;
  if (message.type === 'reply') {
    const pending=requests.get(message.requestId); if (!pending) return;
    clearTimeout(pending.timeout); requests.delete(message.requestId);
    if (message.error) pending.reject(new Error(message.error)); else pending.resolve();
  } else if (message.type === 'init') {
    promptId=message.promptId; render(message.state);
    if(view!=='reflection'&&message.state.durationDraft)sharedDuration(message.state.durationDraft);
    // Initial focus is deliberate; subsequent updates never repeat this.
    if(view==='reflection'){$('reflection-text').focus();$('reflection-text').selectionStart=$('reflection-text').value.length;}
    else {const id=['hours','minutes','seconds'].find(id=>Number($(id).value)>0)||'hours';$(id).focus();$(id).select();}
    settings.load();
    if(view==='reflection')run(()=>send('reflectionReady',{id:promptId}));
    else run(()=>send('interfaceReady'));
  } else if(message.type==='showReflection'&&view==='reflection') {
    // The host flushed and froze both fields. Rebind the same document and
    // controls to a saved reflection, keeping WebView2 alive across Prev/Next.
    if(!message.state?.prompts?.some(p=>p.id===message.promptId)) {
      run(()=>send('reflectionLoadFailed',{id:message.promptId}));return;
    }
    clearTimeout(saveDelay);promptId=message.promptId;loadedPrompt=undefined;
    queued=false;savingAndClosing=false;setReflectionBusy(false);
    $('reflection-text').removeAttribute('aria-invalid');error('');
    render(message.state);setText($('draft-status'),'Draft saved locally.');
    $('reflection-text').focus();$('reflection-text').selectionStart=$('reflection-text').value.length;
    run(()=>send('reflectionReady',{id:promptId}));
  } else if (message.type === 'state') render(message.state);
  else if (message.type === 'clock') {if(state?.clock.status===message.clock.status)renderDuration(message.clock);}
  else if (message.type === 'timeRead') snapshot(message.clock,true);
  else if (message.type === 'announcement') announce(message.message);
  else if (message.type === 'durationDraft'&&view!=='reflection')sharedDuration(message.parts);
  else if (message.type === 'focusReflection') {if(view==='reflection'&&!document.querySelector('dialog[open]'))$('reflection-text').focus();}
  else if (message.type === 'reflectionShortcut') {
    if(view!=='reflection'||!loadedPrompt||queued||reflectionBusy||savingAndClosing||document.querySelector('dialog[open]'))return;
    if(['reflection-text','early-reason'].some(id=>$(id)===document.activeElement))$('later').click();
    else $('reflection-text').focus();
  }
  else if (message.type === 'reflectionCloseFailed') {savingAndClosing=false;setReflectionBusy(reflectionBusy);error(message.message);}
  else if (message.type === 'focusTimer') {if(document.querySelector('dialog[open]'))return;if(message.selectTimer)layout.select('timer');if(document.body.dataset.tab!=='timer')return;const id=['hours','minutes','seconds'].find(id=>Number($(id).value)>0)||'hours';$(id).focus();$(id).select();}
  else if (message.type === 'cycleAppTab') layout.cycle(message.backward);
  else if (message.type === 'flush') {
    if(message.freeze)setReflectionBusy(true);
    saveDraft().then(()=>send('flushed')).catch(e=>{ error(e.message); send('flushFailed').catch(()=>{}); });
  } else if(message.type==='resumeReflection') {setReflectionBusy(false);
  } else settings.message(message);
});
const settings=settingsUI({send,run,bind,view,announce});
bind('delivery-confirm',async()=>{
  const decision=deliveryDecision;await send(decision.action,{id:decision.id,confirmed:true});$('delivery-dialog').close();
  $('retry-selected').focus();
});
['hours','minutes','seconds'].forEach(id => {
  const changed=()=>{const parts=['hours','minutes','seconds'].map(id=>$(id).value);sharedDuration(parts);run(()=>send('durationDraft',{parts}));};
  $(id).addEventListener('input',changed);normalizeEmptyDuration($(id),changed);
});
['schedule-hours','schedule-minutes','schedule-seconds'].forEach(id=>normalizeEmptyDuration($(id),()=>{}));
bindTimerEditor($('timer-editor'),['hours','minutes','seconds'].map($),run,async()=>{
  if(state?.clock.status==='Running'){await send('toggle');return;}
  const seconds = readDuration(), threshold = Number($('threshold').value);
  await send('toggle',{seconds,threshold,repeat:$('repeat').checked,lowTime:$('low-time').checked}); durationDirty=false; applyDuration(state.timer.durationSeconds); render(state);
});
bind('read-time',()=>send('readTime'));
bind('reset',async()=>{ await send('reset',{seconds:readDuration()}); durationDirty=false; applyDuration(state.timer.durationSeconds); render(state); });
bind('end',()=>send('end')); bind('check-in',()=>send('checkIn')); bind('practice',()=>send('testReflection'));
$('repeat').addEventListener('change',()=>run(()=>send('repeat',{enabled:$('repeat').checked})));
bind('open-compact',()=>send('toggleCompact')); bind('open-main',()=>send('main')); bind('close-compact',()=>send('close'));
if(view==='main'){
  bind('show-pending',async()=>{if(!state.prompts.length)return announce('No pending reflections.');await send('openReflection',{id:state.prompts.at(-1).id});});
  bind('edit-schedule',editSelectedSchedule);
  bind('remove-schedule',async()=>{if(!selectedSchedule)return;const id=selectedSchedule;await send('removeSchedule',{id});if(scheduleEdit===id)clearScheduleEdit();($('schedule-rows').querySelector('input:checked')||$('schedule-start')).focus();});
  for(const [id,decision] of [['schedule-start-now',0],['schedule-wait',1],['schedule-skip',2]])bind(id,()=>selectedSchedule&&send('resolveSchedule',{id:selectedSchedule,decision}));
  bind('retry-selected',()=>selectedOutbox&&reviewDelivery(selectedOutbox,'retry'));
  bind('mark-selected',()=>selectedOutbox&&send('markSent',{id:selectedOutbox,confirmed:true}));
  bind('simulate-selected',()=>selectedOutbox&&send('simulate',{id:selectedOutbox}));
  $('schedule-volume').addEventListener('input',()=>setText($('schedule-volume-caption'),'App sound ('+$('schedule-volume').value+'%)'));
  $('schedule-repeat').addEventListener('change',()=>{if(!$('schedule-repeat').checked){$('schedule-cutoff-enabled').checked=false;$('schedule-cutoff').disabled=true;}});
  bind('timer-mark-issue',()=>send('markIssue'));bind('quit',()=>send('quit'));
  $('quick-schedule-form').addEventListener('submit',event=>{event.preventDefault();run(()=>send('schedule',{start:$('quick-start').value,seconds:readDuration(),repeat:$('repeat').checked,lowOptions:settings.timerLow(),fromTimer:true,volume:state.appVolume??50,cutoff:$('cutoff-enabled').checked?$('cutoff').value:''}));});
}
bind('compact-mode',()=>{
  const tiny=document.body.dataset.tiny !== 'true'; document.body.dataset.tiny=String(tiny);
  $('compact-mode').setAttribute('aria-expanded',String(!tiny)); setText($('compact-mode'),tiny?'Show timer controls':'Time-only view');
});
$('schedule-form').addEventListener('submit',event=>{event.preventDefault();run(async()=>{
  const seconds=durationSeconds(['schedule-hours','schedule-minutes','schedule-seconds'].map(id=>$(id).value.trim()));
  await send('schedule',{id:scheduleEdit,start:$('schedule-start').value,seconds,repeat:$('schedule-repeat').checked,lowTime:$('schedule-low').checked,
    lowOptions:settings.scheduleLow(),volume:Number($('schedule-volume').value),cutoff:$('schedule-cutoff-enabled').checked?$('schedule-cutoff').value:''});clearScheduleEdit();
});});
$('schedule-cutoff-enabled').addEventListener('change',()=>{$('schedule-cutoff').disabled=!$('schedule-cutoff-enabled').checked;if($('schedule-cutoff-enabled').checked)$('schedule-repeat').checked=true;if($('schedule-cutoff-enabled').checked&&!$('schedule-cutoff').value)$('schedule-cutoff').value=localDateTime(new Date($('schedule-start').value||Date.now()).getTime()+3600000);});
bind('schedule-cancel',()=>{clearScheduleEdit();$('schedule-start').focus();});
['reflection-text','early-reason'].forEach(id=>$(id).addEventListener('input',()=>{
  setText($('draft-status'),'Saving draft…'); clearTimeout(saveDelay); saveDelay=setTimeout(()=>saveDraft().catch(e=>error(e.message)),300);
}));
$('reflection-form').addEventListener('submit',event=>{event.preventDefault();run(async()=>{
  if(view!=='reflection'||!loadedPrompt||queued||reflectionBusy||savingAndClosing)return;
  if(!$('reflection-text').value.trim()) { $('reflection-text').setAttribute('aria-invalid','true'); $('reflection-text').focus(); throw new Error('Write a reflection before saving.'); }
  $('reflection-text').removeAttribute('aria-invalid');
  // Lock before the draft flush so another shortcut cannot submit it twice.
  savingAndClosing=true;setReflectionBusy(reflectionBusy);
  try { await saveDraft(); queued=true; await send('queue',draft()); }
  catch(e) { queued=false;savingAndClosing=false;setReflectionBusy(reflectionBusy);throw e; }
});});
bind('later',async()=>{
  if(!loadedPrompt||queued||reflectionBusy||savingAndClosing)return;
  savingAndClosing=true;setReflectionBusy(reflectionBusy);
  try {await saveDraft();await send('saveForLater',draft());}
  catch(e){savingAndClosing=false;setReflectionBusy(reflectionBusy);throw e;}
});
for(const [id,direction] of [['reflection-prev',-1],['reflection-next',1]])bind(id,async()=>{
  if(!loadedPrompt||queued||reflectionBusy||savingAndClosing)return;
  setReflectionBusy(true);
  try {await saveDraft();await send('navigateReflection',{direction});}
  finally {setReflectionBusy(false);}
});
bind('skip-reflection',async()=>{
  if(view!=='reflection'||!loadedPrompt||queued||reflectionBusy||savingAndClosing)return;
  savingAndClosing=true;setReflectionBusy(reflectionBusy);clearTimeout(saveDelay);
  try {await saving.catch(()=>{});queued=true;await send('skip',{id:promptId});}
  catch(e){queued=false;savingAndClosing=false;setReflectionBusy(reflectionBusy);throw e;}
});
document.addEventListener('keydown',event=>{
  const submit=event.ctrlKey&&event.key==='Enter',dismiss=event.key==='Escape';
  if(document.querySelector('dialog[open]'))return;
  if(view==='main'&&dismiss){
    event.preventDefault();
    // Native App close hides and retains this document, just like Ctrl+Alt+T.
    if(!event.repeat&&state)run(()=>send('close'));
    return;
  }
  if(view!=='reflection'||(!submit&&!dismiss))return;
  // Cancel Enter's focused-button action as well (Save, Skip, Prev, or Next).
  event.preventDefault();
  if(event.repeat||!loadedPrompt||queued||reflectionBusy||savingAndClosing)return;
  // Inspect both fields, including a reason retained after natural completion.
  const empty=['reflection-text','early-reason'].every(id=>$(id).value.length===0);
  if(empty)$('skip-reflection').click();
  else if(dismiss)$('later').click();
  else $('reflection-form').requestSubmit();
});
if(view==='main')$('schedule-start').value=localDateTime(Date.now()+3600000);
run(()=>send('ready'));
