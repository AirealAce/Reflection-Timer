// Reuses the Focus browser suite's synthetic bridge; no installed profile is read.
module.exports=async(page,check)=>{
  const dialog=page.locator('#focus-target-dialog'),table=page.locator('#focus-target-list');
  const kind=page.locator('#focus-target-kind'),search=page.locator('#focus-target-search-input');
  const clear=page.locator('#focus-target-search-clear'),header=page.locator('#focus-target-select-all');
  const status=page.locator('#focus-picker-status');
  const row=key=>table.locator(`tbody tr[data-key="${key}"]`),box=key=>row(key).locator('input[type=checkbox]');
  const visibleKeys=()=>table.locator('tbody tr:visible').evaluateAll(rows=>rows.map(row=>row.dataset.key));
  const visible=async keys=>JSON.stringify(await visibleKeys())===JSON.stringify(keys);
  const ready=()=>page.waitForFunction(()=>document.querySelector('#focus-target-dialog').open&&document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');
  const open=async()=>{await page.locator('#choose-focus-target-settings').click();await ready();};
  const close=async()=>{await page.locator('#focus-target-cancel').click();await dialog.waitFor({state:'hidden'});};
  const select=async value=>{await kind.selectOption(String(value));await ready();};
  const requests=()=>page.evaluate(()=>window.messages.filter(message=>message.action==='focusTargets').length);
  const saves=()=>page.evaluate(()=>window.messages.filter(message=>message.action==='focusSelect').length);
  const focused=control=>control.evaluate(element=>element===document.activeElement);
  const selected=async count=>new RegExp(`\\b${count} selected across all categories\\b`,'i').test(await status.textContent());
  const matching=async count=>new RegExp(`\\b${count} matching targets?\\b`,'i').test(await status.textContent());
  const inventory=async(category,targets,connected)=>page.evaluate(({category,targets,connected})=>{
    for(const target of targets)window.choices.set(target.id,target);
    window.dispatchBridge({type:'focusTargets',kind:category,targets,...(connected===undefined?{}:{browserConnected:connected})});
  },{category,targets,connected});
  const reset=async(focus={},windows)=>page.evaluate(({focus,windows})=>{
    delete window.windowListing;delete window.windowReplacements;
    window.emptyTargets=false;window.repeatTarget=false;window.longNames=false;
    window.holdSelection=false;window.selectionError=null;
    if(windows)window.windowListing=windows;
    window.settings.focusMode={enabled:false,delaySeconds:5,targets:[],pickerKind:0,multipleTargets:true,idleEnabled:false,idleSeconds:20,...focus};
    window.dispatchBridge({type:'settings',settings:window.settings});
  },{focus,windows});
  const toggle=async target=>page.evaluate(target=>{
    window.choices.set(target.id,target);
    window.settings.focusMode={...window.settings.focusMode,targets:[...window.settings.focusMode.targets.filter(saved=>saved.key!==target.key),target],multipleTargets:true};
    window.dispatchBridge({type:'settings',settings:window.settings});
    window.dispatchBridge({type:'focusTargetToggled',target,checked:true,keys:[target.key],multipleTargets:true,enabled:false});
  },target);
  const windows=[
    {id:'search-ledger',key:'search:ledger',kind:0,name:'Quarterly   Ledger',app:'EXCEL',windowName:'Finance Browser'},
    {id:'search-notes',key:'search:notes',kind:0,name:'Personal Notes',app:'notepad',windowName:'Home Desk'},
    {id:'search-ledger-other',key:'search:ledger-other',kind:0,name:'Ledger archive',app:'chrome',windowName:'Archive window'}
  ];
  const tabs=[
    {id:'search-tab-ledger',key:'search:tab-ledger',kind:1,name:'Ledger',app:'chrome',windowName:'Finance Browser',tabPosition:12},
    {id:'search-tab-review',key:'search:tab-review',kind:1,name:'Sprint Review',app:'msedge',windowName:'Product Browser',tabPosition:4},
    {id:'search-tab-draft',key:'search:tab-draft',kind:1,name:'Ledger draft',app:'chrome',windowName:'Finance Browser',tabPosition:21}
  ];
  await reset({},windows);await open();await inventory(0,windows);
  check(await page.getByRole('searchbox',{name:'Search targets',exact:true}).count()===1&&await search.getAttribute('type')==='search','Target search uses a native search input with the accessible label Search targets');
  check(await page.getByRole('button',{name:'Clear search',exact:true}).count()===1&&await clear.evaluate(button=>button.tagName==='BUTTON'&&button.textContent.trim()==='Clear search'),'Target search has one native Clear search button');
  check(await page.evaluate(()=>{
    const input=document.querySelector('#focus-target-search-input'),clear=document.querySelector('#focus-target-search-clear'),type=document.querySelector('.focus-target-type-row');
    let row=input.parentElement;while(row&&!row.contains(clear))row=row.parentElement;
    return !!row&&row.querySelectorAll('p').length===0&&input.getBoundingClientRect().top>=type.getBoundingClientRect().bottom;
  }),'Search is below the Target type selector without an instructional paragraph');
  check(await focused(kind)&&await search.inputValue()==='','Opening target search retains initial focus on Target type and starts with an empty query');
  let nativeCount=await page.evaluate(()=>window.messages.length),saveCount=await saves();
  await search.fill('LEDGER');
  check(await visible(['search:ledger','search:ledger-other'])&&await matching(2),'Target names match case insensitively and status reports the matching count');
  await search.fill('  ledger    ExCeL   FINANCE  ');
  check(await visible(['search:ledger']),'Every whitespace-separated search word must match across name, app and window-name fields');
  await search.fill('quarterly ledger');
  check(await visible(['search:ledger']),'Search normalizes repeated whitespace within a target name');
  await search.fill('home desk');
  check(await visible(['search:notes']),'Search includes the target window name even when it is absent from the visible Window columns');
  await search.fill('NOTEPAD');
  check(await visible(['search:notes']),'Search matches the app field case insensitively');
  await search.press('Enter');
  check(await saves()===saveCount&&await dialog.isVisible()&&await focused(search),'Plain Enter in target search keeps focus and cannot save or close the chooser');
  check(await page.evaluate(()=>window.messages.length)===nativeCount,'Typing and pressing plain Enter filter locally without native bridge requests');

  await search.fill('ledger');await select(1);await inventory(1,tabs);
  check(await search.inputValue()==='ledger'&&await visible(['search:tab-ledger','search:tab-draft']),'Switching categories retains the query and applies it only to Browser Tab rows');
  await box('search:tab-ledger').focus();await page.keyboard.press('ArrowDown');
  check(await focused(box('search:tab-draft')),'Arrow Down skips Browser Tabs hidden by the query');
  await page.keyboard.press('ArrowUp');
  check(await focused(box('search:tab-ledger')),'Arrow Up returns to the previous matching Browser Tab');
  await page.keyboard.press('End');
  check(await focused(box('search:tab-draft')),'End moves to the last matching target');
  await page.keyboard.press('Home');
  check(await focused(box('search:tab-ledger')),'Home moves to the first matching target');
  await search.fill('12 CHROME');
  check(await visible(['search:tab-ledger']),'Search matches the numeric tab position together with its app');
  await search.fill('4 product');
  check(await visible(['search:tab-review']),'Search combines a tab number with its containing window name');
  await search.fill('ledger');await select(2);
  const groups=[
    {id:'search-group-ledger',key:'search:group-ledger',kind:2,name:'Ledger team',app:'chrome',windowName:'Engineering',tabPosition:7},
    {id:'search-group-other',key:'search:group-other',kind:2,name:'Other group',app:'msedge',windowName:'Planning',tabPosition:3}
  ];
  await inventory(2,groups);
  check(await search.inputValue()==='ledger'&&await visible(['search:group-ledger']),'The same query follows an explicit switch to Browser Tab Groups');
  await search.fill('7 engineering');
  check(await visible(['search:group-ledger']),'Search includes the numeric group position and containing window name');
  nativeCount=await requests();await clear.click();
  check(await search.inputValue()===''&&await visible(['search:group-ledger','search:group-other'])&&await focused(search),'Clear search restores all current-category rows and returns focus to search');
  check(await requests()===nativeCount,'Clearing target search does not request a new native inventory');await close();

  const savedTab={id:'search-tab-hidden',key:'search:tab-hidden',kind:1,name:'Saved task',app:'chrome',tabPosition:8};
  await reset({targets:[windows[1],savedTab]},windows);await open();await search.fill('ledger');
  check(await selected(2)&&!await header.isChecked()&&!await header.evaluate(input=>input.indeterminate),'Hidden checked targets remain counted while the header reflects only matching rows');
  await header.click();
  check(await box('search:ledger').isChecked()&&await box('search:ledger-other').isChecked()&&await selected(4),'Select all checks only matching targets while retaining hidden and other-category selections');
  await header.click();await clear.click();
  check(await box('search:notes').isChecked()&&!await box('search:ledger').isChecked()&&!await box('search:ledger-other').isChecked()&&await selected(2),'Clearing all matching targets leaves a hidden Window and Browser Tab selected');
  await search.fill('ledger');await header.click();saveCount=await saves();await search.press('Control+s');await dialog.waitFor({state:'hidden'});
  check(await saves()===saveCount+1&&await page.evaluate(()=>{
    const keys=window.settings.focusMode.targets.map(target=>target.key).sort();
    return JSON.stringify(keys)===JSON.stringify(['search:ledger','search:ledger-other','search:notes','search:tab-hidden']);
  }),'Ctrl+S from target search saves visible, hidden and other-category selections together');
  await open();
  check(await search.inputValue()===''&&await focused(kind)&&await box('search:notes').isChecked(),'Reopening clears the previous search while retaining saved targets and Target type focus');
  await search.fill('no-target-can-match-this-query');
  check(await visible([])&&await status.textContent().then(text=>/no matching targets|no results|0 matching targets/i.test(text))&&await selected(4),'An empty search result announces no matches and retains the total picked count');
  check(await header.isDisabled()&&!await header.isChecked()&&!await header.evaluate(input=>input.indeterminate),'Select all is disabled with no matching results');
  await clear.click();
  check(await box('search:ledger').isChecked()&&await box('search:ledger-other').isChecked()&&await box('search:notes').isChecked(),'Clearing an empty result restores selected rows without changing their checked state');await close();

  await reset({multipleTargets:false,targets:[windows[1]]},windows);await open();await search.fill('ledger');saveCount=await saves();
  await search.press('Enter');
  check(await saves()===saveCount&&await dialog.isVisible()&&await selected(1),'Plain Enter in single-target search also preserves its hidden selected target without saving');
  await select(1);await inventory(1,tabs);
  check(await search.inputValue()==='ledger'&&await visible(['search:tab-ledger','search:tab-draft'])&&await table.locator('tbody tr.picked').count()===0&&await selected(1),'A searched category switch retains the hidden single Window without choosing its first Browser Tab result');
  await search.press('Control+Enter');await dialog.waitFor({state:'hidden'});
  check(await saves()===saveCount+1&&await page.evaluate(()=>window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].key==='search:notes'),'Ctrl+Enter from target search explicitly saves the hidden single target');

  await reset({targets:[windows[1]]},windows);await open();await search.fill('ledger');
  const newWindow={id:'search-ledger-new',key:'search:ledger-new',kind:0,name:'Ledger incoming',app:'notepad',windowName:'New document'};
  await page.evaluate(({windows,newWindow})=>window.windowListing=[...windows,newWindow],{windows,newWindow});
  nativeCount=await requests();await page.locator('#focus-target-refresh').click();await ready();
  check(await requests()===nativeCount+1&&await search.inputValue()==='ledger'&&await visible(['search:ledger','search:ledger-other','search:ledger-new']),'Refresh retains the query and filters newly returned inventory rows');
  check(!await box('search:ledger-new').isChecked()&&await selected(1),'A refreshed matching target stays unchecked and preserves the hidden selection');
  const shortcutWindow={id:'search-ledger-shortcut',key:'search:ledger-shortcut',kind:0,name:'Ledger shortcut',app:'notepad',windowName:'New shortcut window'};
  await search.focus();await toggle(shortcutWindow);
  check(await search.inputValue()==='ledger'&&await focused(search)&&await box(shortcutWindow.key).isChecked()&&await visible(['search:ledger','search:ledger-other','search:ledger-new','search:ledger-shortcut']),'A global target shortcut redraw retains the query, search focus and matching checked row');
  const hiddenShortcut={id:'search-hidden-shortcut',key:'search:hidden-shortcut',kind:0,name:'Mail inbox',app:'outlook',windowName:'Messages'};
  await toggle(hiddenShortcut);
  check(await search.inputValue()==='ledger'&&await focused(search)&&await visible(['search:ledger','search:ledger-other','search:ledger-new','search:ledger-shortcut'])&&await selected(3),'A shortcut selection that does not match stays hidden and contributes to the total picked count');await close();

  const savedSite={id:'search-site-saved',key:'site:research.example',kind:3,name:'Research portal',siteHost:'research.example',app:'chrome'};
  const blockedSite={id:'search-site-blocked',key:'site:team.internal',kind:3,name:'Team dashboard',siteHost:'team.internal',app:'msedge'};
  await reset({pickerKind:3,browserCompanionEnabled:true,targets:[windows[1],savedSite]},windows);
  await page.evaluate(()=>window.dispatchBridge({type:'browserCompanionStatus',connected:false}));await open();await inventory(3,[savedSite,blockedSite],false);
  await search.fill('RESEARCH.EXAMPLE');
  check(await visible([savedSite.key])&&await box(savedSite.key).isChecked(),'Search matches a Site host even when its visible name is different');
  await search.fill('internal');
  check(await visible([blockedSite.key])&&await header.isDisabled()&&await row(blockedSite.key).getAttribute('aria-disabled')==='true'&&await selected(2),'Filtered select all cannot add a disconnected Site and keeps a hidden saved Site selected');
  await search.fill('portal chrome');await header.click();
  check(await header.isDisabled()&&await selected(1),'Filtered select all can remove an eligible saved Site while preserving a hidden Window');await close();

  await reset({pickerKind:3,browserCompanionEnabled:true,targets:[windows[1],savedSite]},windows);
  await page.evaluate(()=>window.dispatchBridge({type:'browserCompanionStatus',connected:false}));await open();await inventory(3,[savedSite,blockedSite],false);
  await search.fill('portal');nativeCount=await requests();
  await page.evaluate(()=>window.dispatchBridge({type:'browserCompanionStatus',connected:true}));await ready();
  check(await requests()===nativeCount+1&&await search.inputValue()==='portal'&&await focused(search)&&await visible([savedSite.key])&&await box(savedSite.key).isChecked(),'Automatic Site reconnect refresh preserves the query, search focus and saved checkbox selection');
  const newSite={id:'search-site-new',key:'site:new.example',kind:3,name:'New portal',siteHost:'new.example',app:'chrome'};
  await inventory(3,[savedSite,blockedSite,newSite],true);
  check(await search.inputValue()==='portal'&&await visible([savedSite.key,newSite.key])&&!await box(newSite.key).isChecked()&&await selected(2),'New Site inventory rows obey the retained query without selecting additional results');await close();
};
