// Only explicitly marked guidance is collapsible. Labels, values, delivery
// state, errors, and private setup/draft controls never become Help content.
export function mountHelp({view,selectTab}) {
  if(view!=='main')return {render(){}};
  const titles={timer:'Timer and views','timer-low':'Timer low-on-time audio','timer-reached':'Stopwatch alert',
    scheduler:'Scheduler','schedule-low':'Scheduled low-on-time audio',outbox:'Outbox and delivery',theme:'App theme',
    display:'Display and window positions',animations:'Animations',reset:'Reset confirmation',reflections:'Reflection prompts',audio:'Audio playback',
    'audio-low':'Low-on-time audio','audio-reached':'Time-reached audio',focus:'Focus mode',voice:'Voice announcements',
    duplicates:'Avoid duplicate timers',connection:'Google Sheets connection',startup:'Startup and settings',
    diagnostics:'Diagnostics',guidance:'Help and guidance','focus-picker':'Choose a focus target'};
  const groups=new Map(),helpIds=new Set();let preference;
  const contents=document.getElementById('help-contents'),topics=document.getElementById('help-topics');
  for(const node of document.querySelectorAll('[data-help]')){
    const key=node.dataset.help;
    if(!groups.has(key))groups.set(key,{key,title:titles[key]??key,nodes:[],open:false});
    const group=groups.get(key);
    node.id||=`help-text-${key}-${group.nodes.length}`;
    node.classList.remove('sr-only');helpIds.add(node.id);group.nodes.push(node);
  }
  const descriptions=[...document.querySelectorAll('[aria-describedby]')].map(control=>({control,
    ids:control.getAttribute('aria-describedby').split(/\s+/).filter(id=>helpIds.has(id))})).filter(ref=>ref.ids.length);
  function syncDescriptions(){
    for(const {control,ids} of descriptions){
      const keep=(control.getAttribute('aria-describedby')??'').split(/\s+/).filter(id=>id&&!helpIds.has(id));
      for(const id of ids)if(!document.getElementById(id).hidden)keep.push(id);
      if(keep.length)control.setAttribute('aria-describedby',keep.join(' '));else control.removeAttribute('aria-describedby');
    }
  }
  function open(group,expanded){
    group.open=expanded;group.nodes.forEach(node=>node.hidden=!expanded);
    group.button?.setAttribute('aria-expanded',String(expanded));syncDescriptions();
  }
  function copyGuidance(node){
    const copy=node.cloneNode(true);
    for(const element of [copy,...copy.querySelectorAll('*')]){
      for(const attribute of ['id','hidden','data-help','data-help-heading','data-settings-filtered','aria-describedby','aria-labelledby'])element.removeAttribute(attribute);
      element.classList.remove('sr-only');
    }
    return copy;
  }
  function addTopic(key,title,nodes){
    const section=document.createElement('section'),heading=document.createElement('h3'),body=document.createElement('div');
    heading.id=`help-topic-${key}`;heading.tabIndex=-1;heading.textContent=title;section.setAttribute('aria-labelledby',heading.id);
    section.append(heading,body);topics.append(section);
    const item=document.createElement('li'),link=document.createElement('a');link.href='#'+heading.id;link.textContent=title;link.dataset.helpLink='';item.append(link);contents.append(item);
    const render=()=>body.replaceChildren(...nodes.map(copyGuidance));render();
    // Some explanatory text (such as chooser keyboard guidance) changes with
    // the controls. Update its Help copy without touching status or input data.
    const observer=new MutationObserver(render);
    nodes.forEach(node=>observer.observe(node,{subtree:true,childList:true,characterData:true}));
  }
  for(const group of groups.values()){
    const heading=document.querySelector(`[data-help-heading="${group.key}"]`);
    if(!heading)throw new Error('Missing help heading: '+group.key);
    const button=document.createElement('button'),icon=document.createElement('span');
    button.id=`help-toggle-${group.key}`;button.type='button';button.className='help-button';button.setAttribute('aria-label',`Help for ${group.title}`);
    button.title=`Help for ${group.title}`;button.setAttribute('aria-controls',group.nodes.map(node=>node.id).join(' '));
    icon.textContent='?';icon.setAttribute('aria-hidden','true');button.append(icon);
    if(heading.tagName==='P'){
      heading.classList.add('help-label');heading.append(button);
    }else if(heading.tagName==='LEGEND'){
      const label=document.createElement('span');label.id=`help-label-${group.key}`;label.append(...heading.childNodes);
      heading.classList.add('help-label');heading.append(label,button);heading.parentElement.setAttribute('aria-labelledby',label.id);
    }else if(!heading.classList.contains('sr-only')){
      const label=document.createElement('span');label.id=`help-label-${group.key}`;label.append(...heading.childNodes);
      heading.classList.add('help-title');heading.append(label,button);heading.setAttribute('aria-labelledby',label.id);
    }else{
      const row=document.createElement('div');row.className='help-heading';heading.before(row);row.append(heading,button);
    }
    group.button=button;button.addEventListener('click',()=>open(group,!group.open));
    addTopic(group.key,group.title,group.nodes);open(group,false);
  }
  const connectionGuide=document.getElementById('setup-guide-content');
  addTopic('receiver','Set up Google Sheets',[...connectionGuide.querySelectorAll(':scope>ol,:scope>p')]);
  const promptKeys=document.createElement('p');promptKeys.textContent=document.getElementById('reflection-form').getAttribute('aria-description');
  addTopic('writing','Writing and saving reflections',[document.getElementById('reflection-help'),document.getElementById('reason-help'),promptKeys]);
  const shortcuts=document.getElementById('help-shortcuts');document.getElementById('help').append(shortcuts);
  const item=document.createElement('li'),link=document.createElement('a');link.href='#help-shortcuts-heading';link.textContent='Keyboard shortcuts';link.dataset.helpLink='';item.append(link);contents.prepend(item);
  document.addEventListener('click',event=>{
    const link=event.target.closest('a[data-help-link]');if(!link)return;
    const target=document.getElementById(link.hash.slice(1));if(!target)return;
    event.preventDefault();selectTab('help');
    const focus=target.matches('h2,h3')?target:target.querySelector('h2,h3')??target;
    focus.tabIndex=-1;focus.focus({preventScroll:true});target.scrollIntoView({block:'start'});
  });
  return {render(enabled){
    enabled=enabled===true;if(preference===enabled)return;preference=enabled;
    groups.forEach(group=>open(group,enabled));
  }};
}
