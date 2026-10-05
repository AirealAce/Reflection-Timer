import {setText} from './ui.js';
export const randomTrack=10;
export function randomChoices(tracks,labelDefault){
  return [{label:'Random',value:randomTrack},...tracks.filter(t=>t.id!==randomTrack).map(t=>({label:t.id===0?labelDefault:t.name,value:t.id}))];
}

// One accessible editor shared by event, session, Scheduler and Focus selectors.
export function mountRandomTracks({after,id,kind,name}){
  const details=document.createElement('details'),summary=document.createElement('summary'),help=document.createElement('p');
  details.id=id;details.className='random-audio';details.hidden=true;
  summary.textContent='Random probabilities';summary.setAttribute('aria-label',name+' Random probabilities');
  help.id=id+'-help';help.className='sr-only';help.textContent='Check the tracks to include. Equal weights give equal chances. Increasing a weight increases that track’s share of the total. A zero weight excludes a track. All tracks excluded makes Random silent.';
  summary.setAttribute('aria-describedby',help.id);
  const table=document.createElement('table');table.setAttribute('aria-label',name+' Random track probabilities');
  const head=document.createElement('thead'),header=document.createElement('tr');
  for(const label of ['Track','Weight','Chance']){const cell=document.createElement('th');cell.scope='col';cell.textContent=label;header.append(cell);}
  head.append(header);const body=document.createElement('tbody');table.append(head,body);
  const empty=document.createElement('p');empty.hidden=true;empty.textContent='All tracks excluded; Random is silent.';
  details.append(summary,help,table,empty);after.after(details);
  let signature,rows=[],savedEntries=[];
  function show(selected){details.hidden=!selected;if(!selected)details.open=false;}
  function update(){
    const total=rows.reduce((sum,row)=>sum+(row.enabled.checked?Math.max(0,Number(row.weight.value)||0):0),0);
    for(const row of rows){
      row.weight.disabled=!row.enabled.checked;
      const chance=total&&row.enabled.checked?Math.max(0,Number(row.weight.value)||0)/total*100:0;
      setText(row.chance,`${new Intl.NumberFormat(undefined,{maximumFractionDigits:2}).format(chance)}%`);
    }
    empty.hidden=total>0;
  }
  function set(entries){
    savedEntries=entries??[];
    for(const row of rows){
      const song=row.track.song??([6,7].includes(row.track.id)||row.track.id>=11);
      const entry=entries?.find(e=>e.track===row.track.id)??{enabled:song===[3,4,5].includes(kind),weight:1};
      row.enabled.checked=entry.enabled;row.weight.value=entry.weight;
    }
    update();
  }
  return{
    show,set,
    render(tracks,entries,selected){
      const available=tracks.filter(t=>![0,9,randomTrack].includes(t.id));
      const next=JSON.stringify(available);
      if(signature!==next){
        signature=next;body.replaceChildren();rows=available.map(track=>{
          const row=document.createElement('tr'),nameCell=document.createElement('th'),weightCell=document.createElement('td'),chanceCell=document.createElement('td');
          nameCell.scope='row';const label=document.createElement('label'),enabled=document.createElement('input'),text=document.createElement('span');
          enabled.type='checkbox';enabled.id=`${id}-${track.id}-enabled`;text.textContent=track.name;label.className='check';label.append(enabled,text);nameCell.append(label);
          const weight=document.createElement('input'),chance=document.createElement('span');weight.type='number';weight.min='0';weight.max='1000';weight.step='1';weight.id=`${id}-${track.id}-weight`;
          weight.setAttribute('aria-label',track.name+' chance weight');chance.id=`${id}-${track.id}-chance`;weight.setAttribute('aria-describedby',chance.id+' '+help.id);enabled.setAttribute('aria-describedby',chance.id);
          weightCell.append(weight);chanceCell.append(chance);row.append(nameCell,weightCell,chanceCell);body.append(row);
          for(const control of [enabled,weight])for(const event of ['input','change'])control.addEventListener(event,update);
          return{track,enabled,weight,chance};
        });
      }
      set(entries);show(selected);
    },
    data(validate=true){return [...rows.map(row=>{
      const weight=Number(row.weight.value);
      if(validate&&(!Number.isInteger(weight)||weight<0||weight>1000))throw new Error('Enter whole-number chance weights from 0 to 1,000.');
      return{track:row.track.id,enabled:row.enabled.checked,weight};
    }),...savedEntries.filter(entry=>!rows.some(row=>row.track.id===entry.track))];}
  };
}
