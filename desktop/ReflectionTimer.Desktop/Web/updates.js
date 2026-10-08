import {setText} from './ui.js';

// Updates are explicit host actions, separate from saving Settings. Keep the
// existing controls attached so live progress never replaces keyboard focus.
export function mountUpdates(view,send) {
  if(view!=='main')return ()=>{};
  const $=id=>document.getElementById(id);
  const check=$('check-for-updates'),install=$('install-update'),actions=$('updates-actions');
  const progress=$('updates-progress'),progressGroup=$('updates-progress-group');
  const busyStatuses=new Set(['checking','downloading','installing']);
  const statuses=new Set(['idle','checking','available','current','downloading','ready','installing','error']);
  let state={status:'idle'},pending=false;
  function render(update) {
    if(!update||!statuses.has(update.status))return;
    state={...state,...update,message:update.message??''};
    const busy=pending||busyStatuses.has(state.status);
    actions.setAttribute('aria-busy',String(busy));
    check.setAttribute('aria-disabled',String(busy));
    install.setAttribute('aria-disabled',String(busy||!['available','ready'].includes(state.status)));
    // Retain the update button during installation, including keyboard focus.
    const hideInstall=!['available','downloading','ready','installing'].includes(state.status);
    const returnFocus=hideInstall&&document.activeElement===install&&document.hasFocus();
    install.hidden=hideInstall;
    if(returnFocus)check.focus({preventScroll:true});
    if(state.currentVersion)setText($('updates-version'),'Current version: '+state.currentVersion);
    const messages={idle:'Check for updates when you are ready.',checking:'Checking for updates…',
      available:state.availableVersion?'Version '+state.availableVersion+' is available.':'An update is available.',
      current:'You have the latest version.',downloading:'Downloading the update…',
      ready:'Update downloaded and verified. Pause any running session, then choose Update now to restart.',installing:'Installing the update and restarting…',
      error:'Unable to update. Check your connection and try again.'};
    setText($('updates-status'),state.message||messages[state.status]);
    progressGroup.hidden=state.status!=='downloading';
    if(state.status==='downloading'&&Number.isFinite(update.progress)){
      const percent=Math.min(100,Math.max(0,update.progress));
      progress.value=percent;setText($('updates-progress-caption'),Math.round(percent)+'%');
    }else{
      progress.removeAttribute('value');setText($('updates-progress-caption'),'');
    }
  }
  async function request(action,status) {
    if(pending||busyStatuses.has(state.status)||(action==='installUpdate'&&!['available','ready'].includes(state.status)))return;
    pending=true;render({...state,status,message:'',progress:undefined});
    try{await send(action);}
    catch(error){render({...state,status:'error',message:error.message||'Unable to update. Try again.'});}
    finally{pending=false;render(state);}
  }
  check.addEventListener('click',()=>request('checkForUpdates','checking'));
  install.addEventListener('click',()=>request('installUpdate','downloading'));
  render(state);return render;
}
