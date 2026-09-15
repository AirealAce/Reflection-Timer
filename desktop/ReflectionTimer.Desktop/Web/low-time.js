import {setOptions,setText} from './ui.js';
export function mountLowTime({send,run}){
  const $=id=>document.getElementById(id);let settings;
  const controls=[];
  for(const [target,enabledId,thresholdId] of [['timer','low-time','threshold'],['schedule','schedule-low','schedule-low-threshold']]){
    const enabled=$(enabledId),options=document.createElement('div');options.className='low-time-options';enabled.closest('label').after(options);
    enabled.closest('label').lastChild.textContent='Low on time audio';
    let threshold=$(thresholdId);
    if(threshold)threshold.closest('label').remove();else{threshold=document.createElement('input');threshold.id=thresholdId;}
    threshold.type='number';threshold.min='1';threshold.max='31536000';threshold.value='15';threshold.readOnly=true;threshold.setAttribute('aria-label','Low-time seconds remaining');
    const row=document.createElement('div');row.className='option-row';
    const label=document.createElement('label');label.className='check';const inherit=document.createElement('input');inherit.type='checkbox';inherit.checked=true;inherit.id=`${target}-low-inherit`;label.append(inherit,'Use default threshold');const suffix=document.createElement('span');suffix.textContent='seconds remaining';row.append(label,threshold,suffix);
    const help=document.createElement('span');help.id=`${target}-low-threshold-help`;help.className='sr-only';row.append(help);threshold.setAttribute('aria-describedby',help.id);
    const caption=document.createElement('p'),source=document.createElement('select');source.id=`${target}-low-track`;source.setAttribute('aria-label',target==='timer'?'Session low-time sound':'Scheduled low-time sound');
    const actions=document.createElement('div');actions.className='option-row';const preview=document.createElement('button'),browse=document.createElement('button');preview.type=browse.type='button';preview.textContent='Preview audio';browse.textContent='Choose MP3…';actions.append(source,preview,browse);options.append(row,caption,actions);
    const control={target,enabled,options,threshold,inherit,source,caption,help,value:{enabled:true,inherit:true,threshold:15,track:0,custom:false},dirty:false};controls.push(control);
    function data(){return{enabled:enabled.checked,inherit:inherit.checked,threshold:Number(threshold.value),track:source.value==='custom'?0:Number(source.value||0),keepCustom:source.value==='custom'};}
    control.data=data;
    const changed=()=>{control.dirty=true;options.hidden=!enabled.checked;syncThreshold(control);if(target==='timer')run(async()=>{await send('lowTime',data());control.dirty=false;});};
    [enabled,inherit,threshold,source].forEach(node=>{node.addEventListener('input',()=>control.dirty=true);node.addEventListener('change',changed);});
    source.addEventListener('change',()=>run(()=>send('previewLowSound',{target,options:data(),quiet:true})));
    preview.addEventListener('click',()=>run(()=>send('previewLowSound',{target,options:data()})));
    browse.addEventListener('click',()=>run(async()=>{await send('browseLowSound',{target,options:data()});control.dirty=false;render(control,control.value);}));
  }
  function syncThreshold(c){
    // An inherited value is still useful to read, focus, and copy.
    c.threshold.readOnly=c.inherit.checked;
    if(settings){
      if(c.inherit.checked)c.threshold.value=settings.threshold;
      setText(c.caption,`Settings default: ${settings.threshold} seconds remaining · sound behavior follows Settings → Audio.`);
    }
    setText(c.help,c.inherit.checked?'Following Settings → Audio. Uncheck Use default threshold to edit this '+(c.target==='timer'?'session':'schedule')+'.':'Custom threshold for this '+(c.target==='timer'?'session':'schedule')+'.');
  }
  function render(c,value){c.value=value;if(c.dirty){syncThreshold(c);return;}c.enabled.checked=value.enabled;c.inherit.checked=value.inherit;c.threshold.value=value.threshold;syncThreshold(c);c.options.hidden=!value.enabled;
    if(settings){const choices=settings.tracks.map(t=>({label:t.id===0?'Use Audio settings sound':t.name,value:t.id}));if(value.custom)choices.push({label:value.customName?`Custom MP3 · ${value.customName}`:'Custom MP3',value:'custom'});setOptions(c.source,choices,value.custom?'custom':value.track);}
  }
  return{settings(value){settings=value;controls.forEach(c=>render(c,c.value));},state(state){render(controls[0],state.timer.low??{enabled:state.timer.enabled,inherit:true,threshold:state.timer.threshold,track:0,custom:false});},scheduled(value){render(controls[1],value);},scheduleData(){return controls[1].data();},timerData(){return controls[0].data();},resetSchedule(){controls[1].dirty=false;render(controls[1],{enabled:true,inherit:true,threshold:settings?.threshold??15,track:0,custom:false});}};
}
