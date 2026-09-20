// All viewer documents share the same shortcut, including WebView's native
// accelerator route. Store navigation only; reflection text stays encrypted.
export function bindResetAndReload({bridge,send,run,canReset,selectTab=()=>{}}) {
  const key='timer-reset-reload-view';
  let pending=false,restore;
  try{restore=JSON.parse(sessionStorage.getItem(key));sessionStorage.removeItem(key);}catch{}
  function invoke(){
    if(pending||!canReset()||document.querySelector('dialog[open]'))return;
    pending=true;
    run(async()=>{
      try{
        const active=document.activeElement;
        sessionStorage.setItem(key,JSON.stringify({tab:document.body.dataset.tab,scroll:document.getElementById('main')?.scrollTop||0,focus:active?.id}));
        await send('resetAndReload');
      }catch(error){pending=false;sessionStorage.removeItem(key);throw error;}
    });
  }
  document.addEventListener('keydown',event=>{
    if(event.key.toLowerCase()!=='r'||!event.ctrlKey||event.altKey||event.shiftKey||event.metaKey||event.isComposing)return;
    event.preventDefault();event.stopPropagation();
    if(!event.repeat)invoke();
  },true);
  bridge?.addEventListener('message',event=>{if(event.data.type==='resetAndReloadShortcut')invoke();});
  return ()=>{
    if(!restore)return;
    selectTab(restore.tab);
    const active=document.getElementById(restore.focus);
    if(active&&!active.closest('[hidden]')&&active.getClientRects().length)active.focus({preventScroll:true});
    const main=document.getElementById('main');if(main)main.scrollTop=restore.scroll;
    restore=undefined;
  };
}
export function setText(element, value) {
  const text = String(value ?? '');
  if (element.textContent !== text) element.textContent = text;
}
export function setValue(element, value) {
  if (element.value !== String(value)) element.value = String(value);
}
// Delegate once: dropdowns added by audio editors, scheduling, or dialogs get
// the same WebView2 selection feedback without changing native keyboard behavior.
export function announceSelectChanges(doc = document) {
  const regions = new WeakMap(), selections = new WeakMap();
  let pending, timer;
  const signature = select => JSON.stringify([select.selectedIndex, select.value, select.selectedOptions[0]?.label]);
  const eligible = element => element?.tagName === 'SELECT' && !element.multiple && element.size <= 1 && !element.disabled;
  function regionFor(select) {
    // A body-level live region is inert while a modal dialog is open.
    const scope = select.closest('dialog') || doc.body;
    if (!regions.has(scope)) {
      const region = doc.createElement('span');
      region.className = 'sr-only';
      region.dataset.selectAnnouncement = '';
      region.setAttribute('role', 'status');
      region.setAttribute('aria-live', 'polite');
      region.setAttribute('aria-atomic', 'true');
      scope.append(region);
      regions.set(scope, region);
    }
    return regions.get(scope);
  }
  function cancel() {
    clearTimeout(timer);
    if (pending) pending.region.textContent = '';
    pending = undefined;
  }
  doc.addEventListener('focusin', event => {
    if (eligible(event.target)) {
      selections.set(event.target, signature(event.target));
      regionFor(event.target);
    }
  });
  function changed(event) {
    const select = event.target;
    if (!event.isTrusted || !eligible(select) || doc.activeElement !== select || !doc.hasFocus()) return;
    const selected = signature(select), label = select.selectedOptions[0]?.label.trim();
    if (selections.get(select) === selected) return; // input + change describe one choice.
    selections.set(select, selected);
    cancel();
    if (!label) return;
    const region = regionFor(select);
    region.textContent = '';
    pending = {select, region};
    // Let native selection and autosave settle. Holding an arrow speaks the
    // latest choice instead of building a queue of obsolete choices.
    timer = setTimeout(() => {
      if (!select.isConnected || doc.activeElement !== select || !doc.hasFocus() ||
          !eligible(select) || select.closest('[hidden], [inert]') || signature(select) !== selected) { cancel(); return; }
      region.textContent = label + ' selected.';
    }, 150);
  }
  doc.addEventListener('input', changed, true);
  doc.addEventListener('change', changed, true);
  doc.addEventListener('focusout', event => { if (event.target === pending?.select) cancel(); });
  doc.defaultView.addEventListener('blur', cancel);
}
export function setOptions(select, choices, value) {
  const signature=JSON.stringify(choices);
  if(select.dataset.choices!==signature){select.replaceChildren(...choices.map(c=>new Option(c.label,String(c.value))));select.dataset.choices=signature;}
  setValue(select,value);
}
export function formatClock(seconds) {
  const h = Math.floor(seconds / 3600), m = Math.floor(seconds / 60) % 60, s = seconds % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
}
// Completed sessions preview the duration in the controls. The engine's zero
// remaining time (or a late countdown frame) must not become the idle display.
export function displayClock(clock, values, edited=false) {
  if(clock.stopwatch)return clock;
  if(clock.status!=='Finished'&&!(edited&&clock.status!=='Running'))return clock;
  const seconds=durationPreviewSeconds(values),parts=[];
  const hours=Math.floor(seconds/3600),minutes=Math.floor(seconds/60)%60,remainder=seconds%60;
  if(hours)parts.push(`${hours} hour${hours===1?'':'s'}`);
  if(minutes)parts.push(`${minutes} minute${minutes===1?'':'s'}`);
  if(remainder||!parts.length)parts.push(`${remainder} second${remainder===1?'':'s'}`);
  return {...clock,seconds,text:parts.join(' ')};
}
export function durationSeconds(values) {
  const result = durationPreviewSeconds(values);
  if (result < 1) throw new Error('Enter a duration between one second and one year.');
  return result;
}
// An empty unit is zero while editing; a zero preview is valid, but cannot start a session.
export function durationPreviewSeconds(values) {
  const parts = values.map(v => v.trim() || '0');
  if (parts.some(v => !/^\d+$/.test(v))) throw new Error('Enter whole, non-negative numbers for hours, minutes, and seconds.');
  const result = Number(parts[0]) * 3600 + Number(parts[1]) * 60 + Number(parts[2]);
  if (!Number.isSafeInteger(result) || result > 31536000) throw new Error('Enter a duration between one second and one year.');
  return result;
}
export function normalizeEmptyDuration(input, changed) {
  input.addEventListener('blur', () => {
    if (input.value === '' && !input.validity.badInput) { input.value = '0'; changed(); }
  });
}
// One deliberate Enter (or button submit) means one toggle, even while the
// native app is replying. Holding Enter must not alternate pause and resume.
export function bindTimerEditor(form, inputs, run, toggle) {
  let pending = false;
  form.addEventListener('submit', event => {
    event.preventDefault();
    if (pending) return;
    pending = true;
    run(async () => {
      try { await toggle(event.submitter); }
      finally { pending = false; }
    });
  });
  inputs.forEach(input => input.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.isComposing) return;
    event.preventDefault();
    if (!event.repeat) form.requestSubmit();
  }));
}
// Keep existing rows, cells, and buttons attached. Updates must not replace the
// focused element or the objects a screen reader is currently navigating.
export function reconcileRows(container, records, create, update) {
  const existing = new Map([...container.children].map(row => [row.dataset.id, row]));
  const wanted = new Set(records.map(record => record.id));
  for (const [id, row] of existing) if (!wanted.has(id)) row.remove();
  records.forEach((record, index) => {
    let row = existing.get(record.id);
    if (!row) { row = create(record); row.dataset.id = record.id; }
    const atIndex = container.children[index];
    if (atIndex !== row) container.insertBefore(row, atIndex ?? null);
    update(row, record);
  });
}
