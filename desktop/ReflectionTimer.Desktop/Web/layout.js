// Keep the original five App tabs. Native HTML supplies the reading structure
// underneath the familiar layout; it must not add App controls to compact mode.
export function arrangeApp(view) {
  const $=id=>document.getElementById(id);
  if(view!=='main') return {select(){},cycle(){}};
  const nav=document.querySelector('nav'),main=$('main');nav.replaceChildren();nav.setAttribute('role','tablist');nav.setAttribute('aria-label','App views');
  const definitions=[['timer','Timer'],['schedules','Scheduler'],['outbox','Outbox'],['settings','Settings'],['diagnostics','Diagnostics']];
  const panels=new Map(),buttons=new Map(),scroll=new Map();let current='timer';
  for(const [id,label] of definitions){const button=document.createElement('button');button.type='button';button.id=`tab-${id}`;button.textContent=label;button.setAttribute('role','tab');button.setAttribute('aria-controls',`panel-${id}`);nav.append(button);buttons.set(id,button);
    const panel=document.createElement('div');panel.id=`panel-${id}`;panel.setAttribute('role','tabpanel');panel.setAttribute('aria-labelledby',button.id);panels.set(id,panel);
    if(id!=='settings')panel.append($(id));else panel.append($('appearance'),$('audio'),$('duplicate-timers'),$('connection'),$('startup'));
    main.append(panel);button.addEventListener('click',()=>select(id));
    button.addEventListener('keydown',event=>{const offset=event.key==='ArrowRight'?1:event.key==='ArrowLeft'?-1:0;let next;
      if(offset)next=definitions[(definitions.findIndex(x=>x[0]===id)+offset+definitions.length)%definitions.length][0];
      else if(event.key==='Home')next=definitions[0][0];else if(event.key==='End')next=definitions.at(-1)[0];
      if(next){event.preventDefault();select(next);buttons.get(next).focus();}
    });
  }
  function select(id){if(!panels.has(id))return;scroll.set(current,main.scrollTop);for(const [key,panel] of panels){panel.hidden=key!==id;const button=buttons.get(key);button.setAttribute('aria-selected',String(key===id));button.tabIndex=key===id?0:-1;}current=id;document.body.dataset.tab=id;document.querySelector('footer').hidden=id!=='settings';document.dispatchEvent(new CustomEvent('appTabChanged',{detail:id}));main.scrollTop=scroll.get(id)||0;}
  function cycle(backward=false){
    if(document.querySelector('dialog[open]'))return;
    const next=definitions[(definitions.findIndex(([id])=>id===current)+(backward?-1:1)+definitions.length)%definitions.length][0];
    select(next);buttons.get(next).focus();
  }
  document.addEventListener('keydown',event=>{
    if(event.key==='Tab'&&event.ctrlKey&&!event.altKey&&!event.metaKey){
      if(document.querySelector('dialog[open]'))return;
      event.preventDefault();cycle(event.shiftKey);
    }
  });
  document.querySelector('.page-header>.main-only').classList.add('sr-only');
  document.querySelector('.eyebrow').classList.add('sr-only');
  const timer=$('timer');$('timer-heading').classList.add('sr-only');$('timer-state').after($('time-snapshot'));
  const notice=document.createElement('p');notice.className='timer-notice';notice.textContent='Desktop timer active · Use only one timer app per session';timer.prepend(notice);
  notice.after($('timer-state'));$('time-snapshot').classList.add('sr-only');
  timer.querySelector('.hint').classList.add('sr-only');$('read-time').classList.add('sr-only');
  const editor=$('timer-editor'),actions=editor.querySelector('.actions'),options=editor.querySelector('.options');editor.querySelector('legend').classList.add('sr-only');$('duration-help').classList.add('sr-only');
  const playback=document.createElement('div');playback.className='app-playback';playback.setAttribute('role','group');playback.setAttribute('aria-label','Timer controls');
  playback.innerHTML='<button id="playback-reset" class="timer-transport" type="button" aria-label="Reset timer" title="Reset timer"><svg viewBox="0 0 20 20" aria-hidden="true" focusable="false"><path d="M11 4 5 10l6 6M5 10h10"/></svg></button><button id="playback-toggle" class="timer-transport" type="submit" form="timer-editor" aria-label="Start timer" title="Start timer"><span aria-hidden="true">▶</span></button><button id="playback-end" class="timer-transport" type="button" aria-label="End timer early" title="End timer early" aria-disabled="true"><svg viewBox="0 0 20 20" aria-hidden="true" focusable="false"><path d="m9 4 6 6-6 6M5 10h10"/></svg></button>';
  playback.insertAdjacentHTML('afterbegin','<button id="playback-repeat" type="button" aria-label="Auto-start next session" aria-pressed="false" title="Auto-start next session"><span aria-hidden="true">Auto<br>Start</span></button><button id="playback-compact" type="button" aria-label="Show or hide compact view" aria-pressed="false" aria-describedby="compact-view-status" title="Show compact view">Comp</button><span id="compact-view-status" class="sr-only">Compact view is hidden.</span>');
  $('visual-clock').after(playback);
  $('playback-reset').addEventListener('click',()=>$('reset').click());
  $('playback-end').addEventListener('click',()=>$('end').click());
  editor.after(options);$('repeat').closest('label').after($('cutoff-form'));$('end').hidden=true;$('check-in').classList.add('sr-only');
  const quick=document.createElement('form');quick.id='quick-schedule-form';quick.className='option-row';quick.innerHTML='<label for="quick-start">Start timer at</label><input id="quick-start" type="datetime-local" required><button type="submit">Schedule session</button>';
  const later=new Date(Date.now()+3600000);quick.querySelector('input').value=new Date(later-later.getTimezoneOffset()*60000).toISOString().slice(0,16);
  editor.after(quick);const help=document.createElement('p');help.textContent='Uses the duration and options on this page. View or cancel it in Scheduler.';quick.after(help);
  options.after($('volume-form'));
  const pending=$('pending');pending.querySelector('h2').classList.add('sr-only');pending.querySelectorAll('p')[1].hidden=true;$('pending-list').hidden=true;
  const buttonsRow=document.createElement('div');buttonsRow.className='actions';buttonsRow.append($('practice'));
  const pendingButton=document.createElement('button');pendingButton.id='show-pending';pendingButton.type='button';pendingButton.textContent='Pending reflections';buttonsRow.append(pendingButton);
  const mark=document.createElement('button');mark.id='timer-mark-issue';mark.type='button';mark.textContent='Mark issue';buttonsRow.append(mark);pending.prepend(buttonsRow);
  timer.append(pending,$('open-compact'));
  const quit=document.createElement('button');quit.id='quit';quit.type='button';quit.textContent='Quit desktop app';
  const hint=document.createElement('p');hint.textContent='Closing this window keeps the timer running in the tray. Right-click its tray icon to quit. Test reflections only go to the test tab.';timer.append(hint,quit);
  const guide=document.createElement('details'),summary=document.createElement('summary');summary.textContent='Accessibility preview review guide';guide.append(summary,$('review'));panels.get('diagnostics').append(guide);
  ['schedule-heading','outbox-heading','diagnostics-heading'].forEach(id=>$(id).classList.add('sr-only'));
  $('appearance-heading').textContent='App theme';$('audio-heading').textContent='Audio';
  $('practice').textContent='Test reflection prompt';$('open-compact').textContent='Show / hide floating timer';
  $('reset').textContent='Reset';
  select('timer');return {select,cycle};
}
