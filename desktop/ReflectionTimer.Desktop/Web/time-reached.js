// One durable stopwatch alert preference, mirrored in Timer and Settings.
export function mountTimeReached({send,run}) {
  const controls=[];
  let dirty=false,revision=0,saving=Promise.resolve();
  for(const prefix of ['timer','settings']) {
    const group=document.createElement('div');group.className='time-reached-options';
    const heading=document.createElement('p');heading.textContent='Time reached · stopwatch';
    const row=document.createElement('div');row.className='option-row';
    const label=document.createElement('label');label.className='check';
    const enabled=document.createElement('input');enabled.type='checkbox';enabled.checked=true;enabled.id=prefix+'-time-reached-enabled';
    label.append(enabled,'Alert after');
    const seconds=document.createElement('input');seconds.type='number';seconds.min='1';seconds.max='31536000';seconds.step='1';seconds.value='300';seconds.id=prefix+'-time-reached-seconds';seconds.setAttribute('aria-label','Stopwatch alert after seconds');
    const suffix=document.createElement('span');suffix.textContent='seconds of active time';row.append(label,seconds,suffix);
    const help=document.createElement('p');help.textContent='Default: 5 minutes. Plays once per stopwatch session, excluding pauses. Change its audio in Settings.';
    group.append(row,help);
    if(prefix==='timer'){group.prepend(heading);document.getElementById('low-time').closest('.low-time-options').after(group);}
    else document.querySelector('#sound-form-4 legend').after(group);
    const control={enabled,seconds};controls.push(control);
    function edit(){dirty=true;revision++;for(const other of controls){other.enabled.checked=enabled.checked;other.seconds.value=seconds.value;other.seconds.disabled=!enabled.checked;}}
    for(const input of [enabled,seconds]){input.addEventListener('input',edit);input.addEventListener('change',()=>{edit();run(save);});}
    seconds.addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.ctrlKey&&!event.isComposing){event.preventDefault();enabled.focus();}});
  }
  function save(){
    const {enabled,seconds}=controls[0],value=Number(seconds.value),editRevision=revision;
    if(!Number.isInteger(value)||value<1||value>31536000)throw new Error('Enter a stopwatch alert time between 1 and 31,536,000 whole seconds.');
    const data={enabled:enabled.checked,seconds:value,quiet:true};
    saving=saving.catch(()=>{}).then(()=>send('timeReached',data)).then(()=>{if(revision===editRevision)dirty=false;});
    return saving;
  }
  return {
    render(settings){if(dirty)return;for(const c of controls){c.enabled.checked=settings.timeReachedEnabled!==false;c.seconds.value=settings.timeReachedSeconds??300;c.seconds.disabled=!c.enabled.checked;}},
    async flush(){if(dirty)await save();else await saving;}
  };
}
