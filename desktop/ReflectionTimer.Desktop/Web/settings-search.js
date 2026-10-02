// Filter existing Settings nodes. Never copy controls, index typed values,
// or change their hidden/help state, saved settings, or form ownership.
export function mountSettingsSearch({view}) {
  if(view!=='main')return;
  const panel=document.getElementById('panel-settings');
  const bar=document.createElement('div');bar.id='settings-search';bar.className='settings-search';bar.setAttribute('role','search');bar.setAttribute('aria-label','Settings');
  bar.innerHTML='<div class="settings-search-row"><label for="settings-search-input" class="sr-only">Search settings</label><input id="settings-search-input" type="search" placeholder="Search settings" autocomplete="off" aria-controls="panel-settings"><button id="settings-search-clear" type="button" hidden>Clear search</button></div><p id="settings-search-status" role="status" aria-atomic="true" hidden></p>';
  panel.prepend(bar);
  const input=bar.querySelector('input'),clear=bar.querySelector('button'),status=bar.querySelector('[role=status]');
  const normalise=text=>text.normalize('NFKD').replace(/[\u0300-\u036f]/g,'').toLocaleLowerCase().replace(/[^\p{L}\p{N}]+/gu,' ').replace(/\bcontrol\b/g,'ctrl').trim();
  const excluded='input,textarea,.sr-only,.help-button,.focus-target-button,#focus-settings-target,#theme-preview,#theme-notice,#setup-export-group';
  let composing=false,announcement;
  function words(nodes){
    const parts=[];
    for(const node of nodes){
      const walker=document.createTreeWalker(node,NodeFilter.SHOW_TEXT);
      for(let text=walker.nextNode();text;text=walker.nextNode())if(!text.parentElement?.closest(excluded))parts.push(text.textContent);
      for(const control of [node,...node.querySelectorAll('[aria-label],[title]')]){
        if(control.closest('.help-button,.focus-target-button,#setup-export-group'))continue;
        parts.push(control.getAttribute('aria-label')??'',control.getAttribute('title')??'');
      }
    }
    return normalise(parts.join(' '));
  }
  function groups(section){
    if(section.id==='appearance'){
      const form=document.getElementById('appearance-form'),result=[];
      let nodes=[...section.children].filter(node=>node!==form);
      for(const node of form.children){
        if(node.matches('h2,h3,.help-heading')&&nodes.length){result.push(nodes);nodes=[];}
        nodes.push(node);
      }
      if(nodes.length)result.push(nodes);return result;
    }
    if(section.id==='audio'){
      const editors=[...section.querySelectorAll(':scope>form')];
      const shared=[...section.children].filter(node=>!editors.includes(node)&&node.id!=='audio-heading');
      return [...editors.map(node=>[node]),shared].filter(nodes=>nodes.length);
    }
    return [[...section.children]];
  }
  function apply(){
    const terms=normalise(input.value).split(/\s+/).filter(Boolean);let matches=0;
    panel.querySelectorAll('[data-settings-filtered]').forEach(node=>node.removeAttribute('data-settings-filtered'));
    for(const section of panel.querySelectorAll(':scope>section')){
      let visible=0;
      for(const nodes of groups(section)){
        const text=words(nodes)+(section.id==='audio'?' audio':'');
        const match=!terms.length||terms.every(term=>text.includes(term));
        if(match){matches++;visible++;}else nodes.forEach(node=>node.setAttribute('data-settings-filtered',''));
      }
      if(!visible)section.setAttribute('data-settings-filtered','');
    }
    clear.hidden=!input.value;status.hidden=!input.value;
    clearTimeout(announcement);
    announcement=setTimeout(()=>{status.textContent=!terms.length?'Showing all settings.':matches?`${matches} settings ${matches===1?'section':'sections'} found.`:'No matching settings.';},180);
  }
  function reset(focus=false){input.value='';apply();if(focus)input.focus();}
  input.addEventListener('input',event=>{if(!event.isComposing&&!composing)apply();});
  input.addEventListener('compositionstart',()=>composing=true);
  input.addEventListener('compositionend',()=>{composing=false;apply();});
  clear.addEventListener('click',()=>reset(true));
  // Reveal invalid pending edits before reportValidity tries to focus them,
  // so Save settings and native Ctrl+S/Ctrl+Enter work with an active search.
  panel.addEventListener('invalid',event=>{if(event.target.closest('[data-settings-filtered]'))reset();},true);
  const observer=new MutationObserver(records=>{
    if(input.value&&!composing&&records.some(record=>!bar.contains(record.target)))apply();
  });
  observer.observe(panel,{subtree:true,childList:true,characterData:true});
}
