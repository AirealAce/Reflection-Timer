// Reuse the Settings fields in a guided dialog; keep one draft and one set of IDs.
export function mountSetup({send,run}){
  const $=id=>document.getElementById(id),dialog=document.createElement('dialog');
  dialog.id='guided-dialog';dialog.setAttribute('aria-labelledby','guided-title');
  dialog.innerHTML='<h2 id="guided-title">Your timer. Your spreadsheet.</h2><div role="tablist" aria-label="Setup steps"></div><div id="guided-pages"></div><div class="actions"><button type="button" id="guided-back">Back</button><button type="button" id="guided-next">Next</button><button type="button" id="guided-close">Close setup</button></div>';
  document.body.append(dialog);const panels=[],tabs=[],moves=[];let current=0;
  for(const [i,label] of ['1 · Your sheet','2 · Google setup','3 · Connect'].entries()){
    const tab=document.createElement('button'),panel=document.createElement('section');
    tab.type='button';tab.id='guided-tab-'+i;tab.textContent=label;tab.setAttribute('role','tab');
    panel.id='guided-page-'+i;panel.setAttribute('role','tabpanel');panel.setAttribute('aria-labelledby',tab.id);tab.setAttribute('aria-controls',panel.id);
    tab.addEventListener('click',()=>select(i));tab.addEventListener('keydown',event=>{let next=event.key==='ArrowRight'?(i+1)%3:event.key==='ArrowLeft'?(i+2)%3:event.key==='Home'?0:event.key==='End'?2:null;if(next!==null){event.preventDefault();select(next);tabs[next].focus();}});
    dialog.querySelector('[role=tablist]').append(tab);$('guided-pages').append(panel);panels.push(panel);tabs.push(tab);
  }
  function select(index){current=index;panels.forEach((panel,i)=>{panel.hidden=i!==index;tabs[i].setAttribute('aria-selected',String(i===index));tabs[i].tabIndex=i===index?0:-1;});$('guided-back').disabled=index===0;$('guided-next').disabled=index===2;}
  function move(node,panel){
    const inputs=node.matches('input,select,textarea')?[node]:[...node.querySelectorAll('input,select,textarea')],owners=inputs.map(input=>input.form?.id);
    const marker=document.createComment('settings-position');node.before(marker);moves.push({node,marker});panel.append(node);
    inputs.forEach((input,i)=>{if(owners[i]&&input.form?.id!==owners[i]&&!input.hasAttribute('form')){input.dataset.guidedForm='true';input.setAttribute('form',owners[i]);}});
  }
  $('guided-back').addEventListener('click',()=>select(Math.max(0,current-1)));$('guided-next').addEventListener('click',()=>select(Math.min(2,current+1)));$('guided-close').addEventListener('click',()=>dialog.close());
  dialog.addEventListener('close',()=>{for(const {node,marker} of moves.reverse()){marker.replaceWith(node);}moves.length=0;document.querySelectorAll('[data-guided-form]').forEach(input=>{input.removeAttribute('form');delete input.dataset.guidedForm;});$('guided-setup').focus();});
  return{open(){
    move($('connection-form'),panels[2]);
    for(const id of ['sheet-url','connection-token'])move($(id).closest('label'),panels[0]);
    for(const id of ['token-help','new-token','restore-setup'])move($(id),panels[0]);
    move($('import-form').closest('details'),panels[0]);
    move($('setup-guide-content'),panels[1]);$('setup-guide-content').open=true;move($('save-script'),panels[1]);
    move($('connection-enabled').closest('label'),panels[2]);
    select(0);dialog.showModal();$('sheet-url').focus();
  },imported(){if(dialog.open)select(2);},get opened(){return dialog.open;}};
}
