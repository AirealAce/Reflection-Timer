import {setText,setValue} from './ui.js';
import {mountTheme} from './themes.js';
import {mountSetup} from './setup.js';
import {mountAudio} from './audio.js';
import {mountLowTime} from './low-time.js';
import {mountTimeReached} from './time-reached.js';

export function settingsUI({send, run, bind, view, announce}) {
  const $ = id => document.getElementById(id);
  let settings, volumeRevision=0, volumeSaving=Promise.resolve();
  const updateTheme=mountTheme(view);
  const setup=mountSetup({send,run});
  const audio=mountAudio({send,run});
  const lowTime=mountLowTime({send,run});
  const timeReached=mountTimeReached({send,run});
  const dirty = new Set();
  const dirtyFields=new Map();
  const displayRevisions=new Map();
  let displaySaving=Promise.resolve();
  const trackedForms=new Set(['appearance-form','volume-form','settings-volume-form','connection-form','cutoff-form']);
  for(const name of ['input','change'])document.addEventListener(name,event=>{const id=event.target.form?.id;if(!trackedForms.has(id))return;dirty.add(id);if(!dirtyFields.has(id))dirtyFields.set(id,new Set());dirtyFields.get(id).add(event.target.id);});
  function cleanField(form,field){dirtyFields.get(form)?.delete(field);if(!dirtyFields.get(form)?.size)dirty.delete(form);}
  function connection() {
    return {sheetUrl:$('sheet-url').value,webAppUrl:$('receiver-url').value,token:$('connection-token').value,
      sheetMode:$('sheet-mode').value,sheetName:$('sheet-name').value,enabled:$('extension-off').checked};
  }
  function populateConnection(c) {
    $('sheet-url').value=c.sheetUrl; $('receiver-url').value=c.webAppUrl;
    setValue($('sheet-mode'),c.sheetMode); $('sheet-name').value=c.sheetName;$('sheet-name').disabled=c.sheetMode!=='fixed';
  }
  function populate(form) {
    if(!settings || dirty.has(form)) return;
    if(form==='appearance-form') {
      ['theme','placement','popup','overlap'].forEach(id=>setValue($(id),settings[id]));
      setValue($('reflectionSeparator'),settings.reflectionSeparator??3);
      $('show-compact').checked=settings.showFloatingTimer; $('logging').checked=settings.loggingEnabled;$('start-at-login').checked=!!settings.startAtLogin;
      ['compactAlwaysOnTop','timeOnlyAlwaysOnTop','promptAlwaysOnTop','autoSendIncompleteReflections','confirmBeforeReset'].forEach(id=>$(id).checked=settings[id]!==false);
    } else if(form==='volume-form'&&!dirty.has('settings-volume-form')) setMasterVolume(settings.volume);
    else if(form==='connection-form') {
      populateConnection(settings); $('connection-token').value=''; $('connection-enabled').checked=settings.connected;$('extension-off').checked=!!settings.extensionDisabledConfirmed;
      setText($('token-help'),settings.hasToken?'Leave blank to keep the saved token. A token is saved.':'Leave blank to keep the saved token. No saved token yet.');
      $('restore-setup').setAttribute('aria-disabled',String(!settings.hasDraft));
    }
  }
  function submit(form, action, data) {
    $(form).addEventListener('submit',event=>{event.preventDefault();run(async()=>{
      await send(action,data()); dirty.delete(form);dirtyFields.delete(form);populate(form);
    });});
  }
  const appearance=()=>({theme:Number($('theme').value),placement:Number($('placement').value),popup:Number($('popup').value),
    overlap:Number($('overlap').value),reflectionSeparator:Number($('reflectionSeparator').value),logging:$('logging').checked,showCompact:$('show-compact').checked,startAtLogin:$('start-at-login').checked,
    compactAlwaysOnTop:$('compactAlwaysOnTop').checked,timeOnlyAlwaysOnTop:$('timeOnlyAlwaysOnTop').checked,promptAlwaysOnTop:$('promptAlwaysOnTop').checked,autoSendIncompleteReflections:$('autoSendIncompleteReflections').checked,confirmBeforeReset:$('confirmBeforeReset').checked,quiet:true});
  submit('appearance-form','saveAppearance',appearance);
  submit('volume-form','volume',()=>({volume:Number($('app-volume').value)}));
  submit('connection-form','connectionSave',connection);
  let savePending=false;
  async function saveSettings(){
    if(savePending)return;
    savePending=true;$('save-settings').setAttribute('aria-disabled','true');
    try{
      if(!settings)throw new Error('Settings are still loading. Please wait before saving.');
      if(!$('appearance-form').reportValidity())return;
      await displaySaving;await lowTime.flush();await timeReached.flush();await audio.flush();await volumeSaving;await send('saveAppearance',appearance());dirty.delete('appearance-form');dirtyFields.delete('appearance-form');
      if(dirty.has('volume-form')){await send('volume',{volume:Number($('app-volume').value),quiet:true});dirty.delete('volume-form');dirtyFields.delete('volume-form');}
      if(dirty.has('connection-form')){await send('connectionStore',connection());dirty.delete('connection-form');dirtyFields.delete('connection-form');populate('connection-form');}
      // One success sound after every part of this explicit save has succeeded.
      await send('settingsSaveComplete');
      announce('Settings saved.');
    }finally{savePending=false;$('save-settings').setAttribute('aria-disabled','false');}
  }
  bind('save-settings',saveSettings);
  let composing=false,nativeScope=false;
  const canSaveFromShortcut=()=>view==='main'&&document.body.dataset.tab==='settings'&&!composing&&!document.querySelector('dialog[open]');
  function syncShortcutScope(){
    const enabled=canSaveFromShortcut();if(nativeScope===enabled)return;nativeScope=enabled;
    run(()=>send('settingsShortcutScope',{enabled}));
  }
  if(view==='main'){
    // Capture before a focused control can consume Enter or submit its own form.
    document.addEventListener('keydown',event=>{
      if(!canSaveFromShortcut()||event.isComposing||!event.ctrlKey||event.altKey||event.metaKey||event.shiftKey||
        (event.key!=='Enter'&&event.key.toLowerCase()!=='s'))return;
      event.preventDefault();event.stopImmediatePropagation();
      if(!event.repeat)run(saveSettings);
    },true);
    document.addEventListener('appTabChanged',syncShortcutScope);
    document.addEventListener('compositionstart',()=>{composing=true;syncShortcutScope();});
    document.addEventListener('compositionend',()=>{composing=false;syncShortcutScope();});
    new MutationObserver(syncShortcutScope).observe(document.body,{subtree:true,attributes:true,attributeFilter:['open']});
  }
  const cutoffData=()=>({cutoff:$('cutoff-enabled').checked?$('cutoff').value:'',quiet:true});
  submit('cutoff-form','setCutoff',cutoffData);
  $('cutoff-enabled').addEventListener('change',()=>run(async()=>{$('cutoff').disabled=!$('cutoff-enabled').checked;if($('cutoff-enabled').checked)$('repeat').checked=true;if($('cutoff-enabled').checked&&(!$('cutoff').value||new Date($('cutoff').value).getTime()<=Date.now()))$('cutoff').value=localDateTime(Date.now()+3600000);await send('setCutoff',cutoffData());dirty.delete('cutoff-form');dirtyFields.delete('cutoff-form');}));
  $('cutoff').addEventListener('change',()=>run(async()=>{if($('cutoff-enabled').checked){await send('setCutoff',cutoffData());dirty.delete('cutoff-form');dirtyFields.delete('cutoff-form');}}));
  function saveDisplay(id,option,value){
    const revision=(displayRevisions.get(id)||0)+1;displayRevisions.set(id,revision);
    // An earlier reply must not clear a later arrow-key edit and repopulate it.
    const save=displaySaving.catch(()=>{}).then(()=>send('displayOption',{option,value,quiet:true})).then(()=>{
      if(displayRevisions.get(id)===revision)cleanField('appearance-form',id);
    });
    displaySaving=save;run(()=>save);
  }
  ['theme','placement','popup','overlap','reflectionSeparator'].forEach(id=>$(id).addEventListener('change',()=>saveDisplay(id,id,Number($(id).value))));
  $('show-compact').addEventListener('change',()=>saveDisplay('show-compact','showCompact',$('show-compact').checked?1:0));
  ['compactAlwaysOnTop','timeOnlyAlwaysOnTop','promptAlwaysOnTop','autoSendIncompleteReflections','confirmBeforeReset'].forEach(id=>$(id).addEventListener('change',()=>saveDisplay(id,id,$(id).checked?1:0)));
  function setMasterVolume(value){for(const id of ['app-volume','settings-volume']){$(id).value=value;setText($(id+'-caption'),'App sound ('+value+'%)');}}
  for(const id of ['app-volume','settings-volume'])$(id).addEventListener('input',()=>{
    const value=Number($(id).value),revision=++volumeRevision;setMasterVolume(value);dirty.add('volume-form');dirty.add('settings-volume-form');
    volumeSaving=volumeSaving.catch(()=>{}).then(()=>send('volume',{volume:value,quiet:true})).then(()=>{if(revision===volumeRevision){dirty.delete('volume-form');dirty.delete('settings-volume-form');}});
    run(()=>volumeSaving);
  });
  $('settings-volume-form').addEventListener('submit',event=>event.preventDefault());
  $('default-threshold').addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.ctrlKey&&!event.isComposing){event.preventDefault();$('settings-low-time').focus();}});
  $('sheet-mode').addEventListener('change',()=>{$('sheet-name').disabled=$('sheet-mode').value!=='fixed';});
  $('connection-enabled').closest('label').hidden=true;
  bind('pause-delivery',async()=>{await send('connectionPause');$('connection-enabled').checked=false;$('extension-off').checked=false;});
  bind('save-script',()=>send('setupScript',connection()));
  bind('new-token',()=>send('setupNewToken',connection()));bind('restore-setup',()=>send('setupRestore'));
  submit('import-form','setupImport',()=>({code:$('setup-import').value}));
  bind('export-setup',()=>send('setupExport'));
  bind('hide-setup',()=>{$('setup-export').value='';$('setup-export-group').hidden=true;$('export-setup').focus();});
  bind('setup-guide',()=>send('setupGuide'));
  bind('guided-setup',()=>setup.open());
  document.addEventListener('appTabChanged',event=>{if(event.detail==='diagnostics')run(()=>send('diagnostics'));});
  bind('mark-issue',()=>send('markIssue')); bind('show-diagnostics',()=>send('diagnostics')); bind('export-diagnostics',()=>send('exportDiagnostics'));
  bind('send-pending',()=>send('sendPending'));
  bind('import-schedules',()=>send('importSchedules'));bind('open-sheet',()=>send('openSheet'));
  bind('clear-diagnostics',()=>{$('clear-log-dialog').showModal();$('clear-log-title').focus();});
  bind('clear-log-confirm',async()=>{await send('clearDiagnostics',{confirmed:true});$('clear-log-dialog').close();$('clear-diagnostics').focus();});
  return {
    scheduleLow:()=>lowTime.scheduleData(),timerLow:()=>lowTime.timerData(),resetScheduleLow:()=>lowTime.resetSchedule(),
    load() { return view==='main'?send('settingsLoad'):Promise.resolve(); },
    state(state) {
      lowTime.state(state);
      updateTheme(state.theme??0);
      if(!dirty.has('appearance-form') && state.showFloatingTimer!==undefined) $('show-compact').checked=state.showFloatingTimer;
      if(!dirty.has('volume-form')&&!dirty.has('settings-volume-form') && state.appVolume!==undefined) setMasterVolume(state.appVolume);
      setText($('delivery-status'),state.connected?'Sheets delivery is enabled. Connected pending entries send automatically.':'Sheets delivery is off. Pending entries stay saved.');
      setText($('reflection-delivery'),state.connected?'Save & send queues this reflection for automatic Sheets delivery. Practice reflections use the receiver’s test tab.':'Save & send keeps this reflection in the Outbox. Sheets delivery is off.');
      if(!dirty.has('cutoff-form')) {$('cutoff').value=localDateTime(state.timer.autoRestartUntil);$('cutoff-enabled').checked=!!state.timer.autoRestartUntil;$('cutoff').disabled=!state.timer.autoRestartUntil;}
    },
    message(message) {
      if(view!=='main') return;
      if(message.type==='settingsSaveShortcut'){if(canSaveFromShortcut())run(saveSettings);return;}
      if(message.type==='settings') { settings=message.settings; ['appearance-form','volume-form','connection-form'].forEach(populate);audio.render(settings);lowTime.settings(settings);timeReached.render(settings);const theme=['Dark','Light','High Contrast','Glamour'][settings.theme]||'Dark';setText($('theme-notice'),theme+' theme. Saves immediately. Windows contrast themes take priority.');updateTheme(settings.theme); }
      else if(message.type==='shortcuts'){
        const descriptions=['Ctrl+Alt+T · hide or bring forward App.','Ctrl+Alt+` (backtick) · start, resume, or end the current session.','Ctrl+Alt+, · cycle compact controls → time-only → hidden → controls.','Ctrl+Alt+. (period) · once for Compact input; twice within 0.8 seconds for App input.','Ctrl+Alt+/ (slash) · focus the reflection box; if either reflection box is already focused, Save the draft and close. Otherwise reopen a pending reflection or open a check-in. Never opens App.','Ctrl+Space · start, resume, or pause the timer from any app, including when all timer windows are hidden. Uses the shared duration inputs, like Compact. Time-only stays small when pausing or resuming.','Ctrl+Alt+Space · same as Ctrl+Space: start, resume, or pause from any app. Time-only stays small, and hidden windows stay hidden.',"Ctrl+Alt+' (apostrophe) · switch Timer ↔ Stopwatch from any app. Pauses and preserves the current session; the other mode stays paused. Time-only stays small, and hidden windows stay hidden."];
        $('shortcut-notices').replaceChildren(...descriptions.map((text,i)=>{
          const p=document.createElement('p'),key=document.createElement('kbd'),[shortcut,description]=text.split(' · ');
          key.textContent=shortcut;
          p.append(key,document.createTextNode(' · '+description+(message.shortcuts[i]?.available===false?' Unavailable: quit another running timer or app using this shortcut. Retrying automatically.':'')));
          return p;
        }));
      }
      else if(message.type==='scheduledLow')lowTime.scheduled(message.low);
      else if(message.type==='setupImported') {
        populateConnection(message.connection); $('connection-token').value=message.connection.apiToken;
        $('connection-enabled').checked=false;$('extension-off').checked=false; dirty.add('connection-form'); $('setup-import').value='';
        if(message.advance)setup.imported();if(setup.opened&&!message.advance)$('connection-token').focus();else $('receiver-url').focus();
      } else if(message.type==='setupCode') {
        $('setup-export-group').hidden=false; $('setup-export').value=message.code; $('setup-export').focus();
      } else if(message.type==='diagnostics') {
        const report=message.report,events=report.events??[];setText($('diagnostic-report'),JSON.stringify(report,null,2));
        setText($('diagnostic-summary'),(report.enabled===false?'Paused':'Recording')+' · '+events.length+'/1200 events · storage '+(report.storageAvailable===false?'unavailable':'available'));
        setText($('diagnostic-recent'),events.slice(-30).reverse().map(e=>new Date(e.at).toLocaleString()+'  '+e.event).join('\n'));
      }
    }
  };
}
export function localDateTime(milliseconds) {
  if(!milliseconds) return '';
  const date=new Date(milliseconds);return new Date(date-date.getTimezoneOffset()*60000).toISOString().slice(0,16);
}
