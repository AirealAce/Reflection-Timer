import {setOptions,setText,setValue} from './ui.js';
// Each original sound event keeps its own visible editor and saved settings.
export function mountAudio({send,run}) {
  const template=document.getElementById('sound-form'),editors=[];let settings;
  const names=['Session end · timer / stopwatch','Success messages','Failure messages','Low on time audio','Time reached · stopwatch','Focus mode · away from selected window or tab'];
  const eventNames=['SessionEnd','Success','Failure','LowTime','TimeReached','FocusMode'];
  for(const kind of [1,2,3,4,5,0]){
    const form=template.cloneNode(true);form.id=`sound-form-${kind}`;
    form.querySelectorAll('[id]').forEach(node=>{const old=node.id;node.id=`${old}-${kind}`;form.querySelectorAll('[aria-describedby]').forEach(control=>{if(control.getAttribute('aria-describedby')===old)control.setAttribute('aria-describedby',node.id);});});
    const field=id=>form.querySelector(`#${id}-${kind}`);
    field('sound-kind').closest('label').remove();
    const sourceRow=document.createElement('div');sourceRow.className='audio-source-row option-row';
    for(const id of ['sound-track','sound-behavior']){const label=field(id).closest('label');label.firstChild.textContent='';sourceRow.append(label);}
    sourceRow.append(field('preview-sound'),field('browse-sound'));form.prepend(sourceRow);field('stop-sound').remove();
    field('sound-track').setAttribute('aria-label',eventNames[kind]+' sound');field('sound-behavior').setAttribute('aria-label',eventNames[kind]+' playback behavior');
    field('sound-volume').setAttribute('aria-label',eventNames[kind]+' audio volume');field('sound-fade').setAttribute('aria-label',eventNames[kind]+' fade out after');field('sound-fade-seconds').setAttribute('aria-label',eventNames[kind]+' fade out after seconds');
    if(kind!==3&&kind!==4){field('sound-message-fade-row').remove();field('sound-message-fade-help').remove();}
    else field('sound-message-fade-seconds').addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.ctrlKey&&!event.isComposing){event.preventDefault();field('sound-message-fade').focus();}});
    if(kind===4){field('sound-message-fade-seconds').setAttribute('aria-label','Time-reached message-sent fade duration in seconds');field('sound-message-fade-help').textContent="Fade this session's time-reached audio when sending its reflection. Saving the draft does not fade it.";}
    field('sound-default').classList.add('sr-only');
    const fieldset=document.createElement('fieldset'),legend=document.createElement('legend');legend.textContent=names[kind];
    if([3,4,5].includes(kind))legend.dataset.helpHeading=kind===3?'audio-low':kind===4?'audio-reached':'focus';
    if([3,4].includes(kind))field('sound-message-fade-help').dataset.help=kind===3?'audio-low':'audio-reached';
    fieldset.append(legend,...form.childNodes);form.append(fieldset);
    if(kind===3)legend.after(document.getElementById('settings-low-options'));
    if(kind===5)legend.after(document.getElementById('focus-audio-options'));
    const save=form.querySelector('button[type=submit]');save.hidden=true;
    template.before(form);
    const instances=[{form,field}];
    if(kind===5){
      // Both surfaces use one editor revision/save queue. Editing either copy
      // updates the other immediately, so stale controls cannot overwrite it.
      const copy=form.cloneNode(true);copy.querySelector('.focus-mode-options').remove();
      for(const node of [copy,...copy.querySelectorAll('[id]')]){const old=node.id;node.id=old+'-picker';copy.querySelectorAll('[aria-describedby]').forEach(control=>{if(control.getAttribute('aria-describedby')===old)control.setAttribute('aria-describedby',node.id);});}
      copy.querySelector('legend').textContent='Focus audio';copy.querySelector('legend').removeAttribute('data-help-heading');copy.querySelector('legend').classList.add('sr-only');
      document.getElementById('focus-picker-audio-editor').append(copy);
      instances.push({form:copy,field:id=>copy.querySelector(`#${id}-${kind}-picker`)});
    }
    const editor={kind,form,field,instances,dirty:false,revision:0,saving:Promise.resolve()};editors.push(editor);
    function syncControls(source){
      for(const target of instances){
        if(target!==source)for(const id of ['sound-track','sound-behavior','sound-volume','sound-fade','sound-fade-seconds',...([3,4].includes(kind)?['sound-message-fade','sound-message-fade-seconds']:[])]){
          const from=source.field(id),to=target.field(id);to.value=from.value;if(from.type==='checkbox')to.checked=from.checked;
        }
        setText(target.field('sound-volume-caption'),`Volume (${target.field('sound-volume').value}%)`);
        target.field('sound-fade-seconds').disabled=!target.field('sound-fade').checked;
        if([3,4].includes(kind))target.field('sound-message-fade-seconds').disabled=!target.field('sound-message-fade').checked;
      }
    }
    const data=()=>({kind,track:field('sound-track').value==='custom'?0:Number(field('sound-track').value),keepCustom:field('sound-track').value==='custom',
      behavior:Number(field('sound-behavior').value),volume:Number(field('sound-volume').value),fade:field('sound-fade').checked,fadeSeconds:Number(field('sound-fade-seconds').value),quiet:true,
      ...([3,4].includes(kind)?{fadeAfterMessageSent:field('sound-message-fade').checked,messageSentFadeSeconds:Number(field('sound-message-fade-seconds').value)}:{})});
    let volumeTimer;
    function saveSound(){
      clearTimeout(volumeTimer);volumeTimer=undefined;
      if(editor.pending)return editor.saving;
      editor.pending=true;
      editor.saving=(async()=>{
        while(editor.dirty){const value=data(),revision=editor.revision;await send('saveSound',value);if(editor.revision===revision)editor.dirty=false;}
      })().finally(()=>{editor.pending=false;});
      return editor.saving;
    }
    editor.save=saveSound;
    for(const instance of instances){
      const {form,field}=instance;
      form.addEventListener('input',event=>{if(event.target.closest('#settings-low-options,.time-reached-options,.focus-mode-options'))return;editor.dirty=true;editor.revision++;syncControls(instance);if(event.target===field('sound-volume')){volumeTimer??=setTimeout(()=>{volumeTimer=undefined;run(saveSound);},150);}});
      form.addEventListener('change',event=>{if(event.target.closest('#settings-low-options,.time-reached-options,.focus-mode-options'))return;editor.dirty=true;editor.revision++;syncControls(instance);run(async()=>{await saveSound();if(event.target===field('sound-track'))await send('previewSound',{kind,quiet:true});});});
      form.addEventListener('submit',event=>{event.preventDefault();run(saveSound);});
      field('browse-sound').addEventListener('click',()=>run(async()=>{if(editor.dirty)await saveSound();await send('browseSound',{kind});renderEditor(editor);}));
      field('preview-sound').addEventListener('click',()=>run(async()=>{if(editor.dirty)await saveSound();await send('previewSound',{kind});}));
    }
  }
  template.remove();for(const id of ['stop-all-audio','focus-picker-stop-audio'])document.getElementById(id).addEventListener('click',()=>run(()=>send('stopSound')));
  function renderEditor(editor){if(!settings||editor.dirty)return;const sound=settings.sounds.find(s=>s.kind===editor.kind)??(editor.kind===4?settings.sounds.find(s=>s.kind===3):undefined);
    if(!sound)return;
    const choices=settings.tracks.map(t=>({label:t.id===0?`Default · ${sound.defaultName}`:t.name,value:t.id}));
    if(sound.custom)choices.push({label:sound.customName?`Custom MP3 · ${sound.customName}`:'Custom MP3',value:'custom'});
    if(!sound.custom&&!choices.some(c=>c.value===sound.track))choices.push({label:'Unavailable on this PC · '+sound.track,value:sound.track});
    for(const {field} of editor.instances){
      setOptions(field('sound-track'),choices,sound.custom?'custom':sound.track);
      setValue(field('sound-behavior'),sound.behavior);field('sound-volume').value=sound.volume;field('sound-fade').checked=sound.fadeOutEnabled;field('sound-fade-seconds').value=sound.fadeOutAfterSeconds;field('sound-fade-seconds').disabled=!sound.fadeOutEnabled;setText(field('sound-volume-caption'),`Volume (${sound.volume}%)`);
      if([3,4].includes(editor.kind)){field('sound-message-fade').checked=!!sound.fadeOutAfterMessageSent;field('sound-message-fade-seconds').value=sound.messageSentFadeSeconds??3;field('sound-message-fade-seconds').disabled=!sound.fadeOutAfterMessageSent;}
      setText(field('sound-default'),`Default: ${sound.defaultName}`);
    }
  }
  return {render(value){settings=value;editors.forEach(renderEditor);},validateFocus(){const form=editors.find(editor=>editor.kind===5).instances[1].form;if(form.checkValidity())return true;document.getElementById('focus-picker-audio').open=true;form.reportValidity();return false;},async flushFocus(){const editor=editors.find(editor=>editor.kind===5);if(editor.dirty)await editor.save();else await editor.saving;},async flush(){for(const editor of editors){if(editor.dirty)await editor.save();else await editor.saving;}}};
}
