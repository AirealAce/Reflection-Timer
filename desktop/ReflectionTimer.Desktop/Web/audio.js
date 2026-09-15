import {setOptions,setText,setValue} from './ui.js';
// Each original sound event keeps its own visible editor and saved settings.
export function mountAudio({send,run}) {
  const template=document.getElementById('sound-form'),editors=[];let settings;
  const names=['Session end · time limit reached','Success messages','Failure messages','Low on time audio'];
  const eventNames=['SessionEnd','Success','Failure','LowTime'];
  for(const kind of [1,2,3,0]){
    const form=template.cloneNode(true);form.id=`sound-form-${kind}`;
    form.querySelectorAll('[id]').forEach(node=>{const old=node.id;node.id=`${old}-${kind}`;form.querySelectorAll('[aria-describedby]').forEach(control=>{if(control.getAttribute('aria-describedby')===old)control.setAttribute('aria-describedby',node.id);});});
    const field=id=>form.querySelector(`#${id}-${kind}`);
    field('sound-kind').closest('label').remove();
    const sourceRow=document.createElement('div');sourceRow.className='audio-source-row option-row';
    for(const id of ['sound-track','sound-behavior']){const label=field(id).closest('label');label.firstChild.textContent='';sourceRow.append(label);}
    sourceRow.append(field('preview-sound'),field('browse-sound'));form.prepend(sourceRow);field('stop-sound').remove();
    field('sound-track').setAttribute('aria-label',eventNames[kind]+' sound');field('sound-behavior').setAttribute('aria-label',eventNames[kind]+' playback behavior');
    field('sound-volume').setAttribute('aria-label',eventNames[kind]+' audio volume');field('sound-fade').setAttribute('aria-label',eventNames[kind]+' fade out after');field('sound-fade-seconds').setAttribute('aria-label',eventNames[kind]+' fade out after seconds');
    if(kind!==3){field('sound-message-fade-row').remove();field('sound-message-fade-help').remove();}
    else field('sound-message-fade-seconds').addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.ctrlKey&&!event.isComposing){event.preventDefault();field('sound-message-fade').focus();}});
    field('sound-default').classList.add('sr-only');
    const fieldset=document.createElement('fieldset'),legend=document.createElement('legend');legend.textContent=names[kind];fieldset.append(legend,...form.childNodes);form.append(fieldset);
    if(kind===3)legend.after(document.getElementById('settings-low-options'));
    const save=form.querySelector('button[type=submit]');save.hidden=true;
    template.before(form);
    const editor={kind,form,field,dirty:false,revision:0,saving:Promise.resolve()};editors.push(editor);
    const data=()=>({kind,track:field('sound-track').value==='custom'?0:Number(field('sound-track').value),keepCustom:field('sound-track').value==='custom',
      behavior:Number(field('sound-behavior').value),volume:Number(field('sound-volume').value),fade:field('sound-fade').checked,fadeSeconds:Number(field('sound-fade-seconds').value),quiet:true,
      ...(kind===3?{fadeAfterMessageSent:field('sound-message-fade').checked,messageSentFadeSeconds:Number(field('sound-message-fade-seconds').value)}:{})});
    function saveSound(){const value=data(),revision=editor.revision;editor.saving=editor.saving.catch(()=>{}).then(()=>send('saveSound',value)).then(()=>{if(editor.revision===revision)editor.dirty=false;});return editor.saving;}
    editor.save=saveSound;
    form.addEventListener('input',event=>{if(event.target.closest('#settings-low-options'))return;editor.dirty=true;editor.revision++;if(event.target===field('sound-volume')){setText(field('sound-volume-caption'),`Volume (${event.target.value}%)`);run(saveSound);}});
    form.addEventListener('change',event=>{if(event.target.closest('#settings-low-options'))return;editor.dirty=true;editor.revision++;field('sound-fade-seconds').disabled=!field('sound-fade').checked;if(kind===3)field('sound-message-fade-seconds').disabled=!field('sound-message-fade').checked;run(async()=>{await saveSound();if(event.target===field('sound-track'))await send('previewSound',{kind,quiet:true});});});
    form.addEventListener('submit',event=>{event.preventDefault();run(saveSound);});
    field('browse-sound').addEventListener('click',()=>run(async()=>{if(editor.dirty)await saveSound();await send('browseSound',{kind});renderEditor(editor);}));
    field('preview-sound').addEventListener('click',()=>run(async()=>{if(editor.dirty)await saveSound();await send('previewSound',{kind});}));
  }
  template.remove();document.getElementById('stop-all-audio').addEventListener('click',()=>run(()=>send('stopSound')));
  function renderEditor(editor){if(!settings||editor.dirty)return;const sound=settings.sounds.find(s=>s.kind===editor.kind),field=editor.field;
    const choices=settings.tracks.map(t=>({label:t.id===0?`Default · ${sound.defaultName}`:t.name,value:t.id}));
    if(sound.custom)choices.push({label:sound.customName?`Custom MP3 · ${sound.customName}`:'Custom MP3',value:'custom'});
    if(!sound.custom&&!choices.some(c=>c.value===sound.track))choices.push({label:'Unavailable on this PC · '+sound.track,value:sound.track});
    setOptions(field('sound-track'),choices,sound.custom?'custom':sound.track);
    setValue(field('sound-behavior'),sound.behavior);field('sound-volume').value=sound.volume;field('sound-fade').checked=sound.fadeOutEnabled;field('sound-fade-seconds').value=sound.fadeOutAfterSeconds;field('sound-fade-seconds').disabled=!sound.fadeOutEnabled;setText(field('sound-volume-caption'),`Volume (${sound.volume}%)`);
    if(editor.kind===3){field('sound-message-fade').checked=!!sound.fadeOutAfterMessageSent;field('sound-message-fade-seconds').value=sound.messageSentFadeSeconds??3;field('sound-message-fade-seconds').disabled=!sound.fadeOutAfterMessageSent;}
    setText(field('sound-default'),`Default: ${sound.defaultName}`);
  }
  return {render(value){settings=value;editors.forEach(renderEditor);},async flush(){for(const editor of editors){if(editor.dirty)await editor.save();else await editor.saving;}}};
}
