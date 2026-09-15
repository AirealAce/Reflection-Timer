import {setOptions} from './ui.js';
export function mountLowTime({send,run}){
  const $=id=>document.getElementById(id);let settings;
  const controls=[];
  for(const [target,enabledId,thresholdId] of [['timer','low-time','threshold'],['schedule','schedule-low','schedule-low-threshold']]){
    const enabled=$(enabledId),label=enabled.closest('label'),options=document.createElement('div');
    options.className='low-time-options';label.after(options);
    let threshold=$(thresholdId);
    if(threshold)threshold.closest('label').remove();else{threshold=document.createElement('input');threshold.id=thresholdId;}
    threshold.type='number';threshold.min='1';threshold.max='31536000';threshold.value='15';threshold.setAttribute('aria-label','Low-time seconds remaining');
    label.lastChild.textContent='Use threshold';
    const heading=document.createElement('p');heading.textContent='Low on time audio';
    const row=document.createElement('div');row.className='option-row';
    const suffix=document.createElement('span');suffix.textContent='seconds remaining';
    row.append(label,threshold,suffix);
    const caption=document.createElement('p');caption.textContent='Uncheck Use threshold to turn off low-on-time audio. Sound behavior follows Settings → Audio.';
    caption.id=target+'-low-threshold-help';threshold.setAttribute('aria-describedby',caption.id);
    const source=document.createElement('select');source.id=target+'-low-track';source.setAttribute('aria-label',target==='timer'?'Session low-time sound':'Scheduled low-time sound');
    const actions=document.createElement('div');actions.className='option-row';
    const preview=document.createElement('button'),browse=document.createElement('button');
    preview.type=browse.type='button';preview.textContent='Preview audio';browse.textContent='Choose MP3…';
    actions.append(source,preview,browse);options.append(heading,row,caption,actions);
    const fields=[{enabled,threshold}];
    if(target==='timer')fields.push({enabled:$('settings-low-time'),threshold:$('default-threshold')});
    const c={target,enabled,threshold,source,fields,value:{enabled:true,inherit:true,threshold:15,track:0,custom:false},dirty:false,revision:0,saving:Promise.resolve()};controls.push(c);
    function mirror(origin=fields[0]){
      for(const field of fields){
        if(field!==origin){field.enabled.checked=origin.enabled.checked;field.threshold.value=origin.threshold.value;}
        field.threshold.disabled=!origin.enabled.checked;
      }
    }
    function edit(field){c.dirty=true;c.revision++;mirror(field);}
    c.data=()=>{
      const seconds=Number(threshold.value);
      if(!Number.isInteger(seconds)||seconds<1||seconds>31536000){
        if(enabled.checked)throw new Error('Enter a low-time threshold between 1 and 31,536,000 whole seconds.');
        threshold.value=c.value.threshold;mirror();
      }
      return{enabled:enabled.checked,inherit:false,threshold:Number(threshold.value),track:source.value==='custom'?0:Number(source.value||0),keepCustom:source.value==='custom'};
    };
    c.save=()=>{
      const data=c.data(),revision=c.revision;
      c.saving=c.saving.catch(()=>{}).then(()=>send('lowTime',data)).then(()=>{
        if(c.revision===revision){c.dirty=false;c.value={...c.value,...data,custom:data.keepCustom};}
      });
      return c.saving;
    };
    for(const field of fields)for(const node of [field.enabled,field.threshold]){
      node.addEventListener('input',()=>edit(field));
      node.addEventListener('change',()=>{edit(field);if(target==='timer')run(c.save);});
    }
    source.addEventListener('input',()=>{c.dirty=true;c.revision++;});
    source.addEventListener('change',()=>{c.dirty=true;c.revision++;run(async()=>{
      if(target==='timer')await c.save();
      await send('previewLowSound',{target,options:c.data(),quiet:true});
    });});
    preview.addEventListener('click',()=>run(()=>send('previewLowSound',{target,options:c.data()})));
    browse.addEventListener('click',()=>run(async()=>{
      if(target==='timer'&&c.dirty)await c.save();
      await send('browseLowSound',{target,options:c.data()});c.dirty=false;render(c,c.value);
    }));
  }
  function render(c,value){
    c.value=value;if(c.dirty)return;
    const seconds=value.inherit?(settings?.threshold??value.threshold):value.threshold;
    for(const field of c.fields){field.enabled.checked=value.enabled;field.threshold.value=seconds;field.threshold.disabled=!value.enabled;}
    if(settings){
      const choices=settings.tracks.map(t=>({label:t.id===0?'Use Audio settings sound':t.name,value:t.id}));
      if(value.custom)choices.push({label:value.customName?'Custom MP3 · '+value.customName:'Custom MP3',value:'custom'});
      setOptions(c.source,choices,value.custom?'custom':value.track);
    }
  }
  return{
    settings(value){settings=value;controls.forEach(c=>render(c,c.value));},
    state(state){render(controls[0],state.timer.low??{enabled:state.timer.enabled,inherit:true,threshold:state.timer.threshold,track:0,custom:false});},
    scheduled(value){render(controls[1],value);},
    scheduleData(){return controls[1].data();},
    timerData(){return controls[0].data();},
    async flush(){const c=controls[0];if(c.dirty)await c.save();else await c.saving;},
    resetSchedule(){controls[1].dirty=false;render(controls[1],{enabled:true,inherit:true,threshold:settings?.threshold??15,track:0,custom:false});}
  };
}
