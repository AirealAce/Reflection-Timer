import {setText} from './ui.js';

// The original bow is decorative: keep it out of reading and keyboard order.
const bow='<svg class="theme-ornament" viewBox="0 0 60 60" aria-hidden="true" focusable="false"><path d="M30 27 C2 2 0 48 30 31 C60 48 58 2 30 27Z"/><path class="ribbon" d="M28 31 C23 39 21 47 13 52 M32 31 C37 39 39 47 47 52 M49 3 V13 M44 8 H54"/><ellipse cx="30" cy="29" rx="4" ry="5"/></svg>';
export function mountTheme(view){
  const title=document.getElementById('page-title');
  if(title)title.insertAdjacentHTML('beforeend',bow);
  const preview=document.getElementById('theme-preview');
  if(view==='main'&&preview){
    preview.setAttribute('role','img');
    preview.innerHTML='<div aria-hidden="true"><strong id="theme-preview-title"></strong><p id="theme-preview-description"></p><div class="theme-preview-samples"><span class="theme-sample-field">Your moment to reflect…</span><span class="theme-sample-action">Save &amp; send</span></div>'+bow+'</div>';
  }
  const contrast=matchMedia('(forced-colors: active)');
  let current=0;
  function update(value=current){
    current=Number(value)||0;
    if(document.documentElement.dataset.theme!==String(current))document.documentElement.dataset.theme=String(current);
    if(view!=='main'||!preview)return;
    const name=['Dark','Light','High Contrast','Glamour'][current]||'Dark';
    const description=contrast.matches?'Windows contrast colors take priority over decorative themes.':[
      'Charcoal surfaces, light text, and mint accents.',
      'Airy surfaces, dark ink, and forest-green accents.',
      'Black and white with bright yellow highlights.',
      'Blush satin · raspberry accents · rose-gold bows · pearl surfaces'
    ][current];
    setText(document.getElementById('theme-preview-title'),name+' · preview');
    setText(document.getElementById('theme-preview-description'),description);
    setText(document.getElementById('theme-notice'),name+' theme.');
    const label=name+' theme preview. '+description+' Sample reflection field and save button. This preview is not interactive.';
    if(preview.getAttribute('aria-label')!==label)preview.setAttribute('aria-label',label);
  }
  contrast.addEventListener('change',()=>update());update();
  return update;
}
