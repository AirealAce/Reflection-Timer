// Reuses the Focus browser suite's synthetic bridge; no installed profile is read.
module.exports=async(page,check)=>{
  const dialog=page.locator('#focus-target-dialog'),table=page.locator('#focus-target-list');
  const header=page.locator('#focus-target-select-all'),kind=page.locator('#focus-target-kind');
  const box=key=>table.locator(`tbody tr[data-key="${key}"] input[type=checkbox]`);
  const ready=()=>page.waitForFunction(()=>document.querySelector('#focus-target-dialog').open&&document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');
  const open=async()=>{await page.locator('#choose-focus-target-settings').click();await ready();};
  const close=async()=>{await page.locator('#focus-target-cancel').click();await dialog.waitFor({state:'hidden'});};
  const select=async value=>{await kind.selectOption(String(value));await ready();};
  const saves=()=>page.evaluate(()=>window.messages.filter(message=>message.action==='focusSelect').length);
  const state=()=>header.evaluate(input=>({checked:input.checked,mixed:input.indeterminate,disabled:input.disabled,label:input.getAttribute('aria-label')}));
  const checked=()=>table.locator('tbody input[type=checkbox]:checked').count();
  const reset=async focus=>{
    await page.evaluate(focus=>{
      delete window.windowListing;delete window.windowReplacements;
      window.emptyTargets=false;window.repeatTarget=false;window.longNames=false;
      window.holdSelection=false;window.selectionError=null;
      window.settings.focusMode={enabled:false,delaySeconds:5,targets:[],pickerKind:0,multipleTargets:true,idleEnabled:false,idleSeconds:20,...focus};
      window.dispatchBridge({type:'settings',settings:window.settings});
    },focus);
  };
  await reset({multipleTargets:false});await open();
  check(await header.count()===0&&await table.getByRole('checkbox').count()===0,'Single-target mode omits the select-all checkbox');
  await page.locator('#focus-multiple-targets').check();
  check(await table.locator('thead input[type=checkbox]').count()===1&&await header.isVisible(),'Multiple Targets places one native select-all checkbox in the selection header');
  check(await header.evaluate(input=>{const box=input.getBoundingClientRect(),cell=input.parentElement.getBoundingClientRect();return cell.width<=33&&box.width===16&&box.left-cell.left>=7.5&&cell.right-box.right>=7.5;}),'The header retains the narrow 32-pixel selection column and the row-checkbox spacing');
  await page.locator('#focus-multiple-targets').uncheck();
  check(await header.count()===0,'Turning off Multiple Targets removes the header checkbox');await close();

  await reset({});await open();
  let value=await state(),count=await saves();
  check(!value.checked&&!value.mixed&&!value.disabled&&value.label==='Select all listed targets','An empty multiple selection has an unchecked, labelled select-all header');
  await header.click();value=await state();
  check(value.checked&&!value.mixed&&value.label==='Clear all listed targets'&&await checked()===await table.locator('tbody tr').count(),'Clicking the header selects every listed dynamic and ordinary Window target');
  check(await saves()===count&&await dialog.isVisible(),'Select all edits the draft without saving or closing the chooser');
  await header.click();value=await state();
  check(!value.checked&&!value.mixed&&await checked()===0,'Clicking an all-selected header clears its listed targets');
  await box('0-a').check();value=await state();
  check(!value.checked&&value.mixed&&value.label==='Select all listed targets','A partly selected category exposes a native indeterminate header');
  const cdp=await page.context().newCDPSession(page),tree=await cdp.send('Accessibility.getFullAXTree');
  const accessibleHeader=tree.nodes.find(node=>!node.ignored&&node.role?.value==='checkbox'&&node.name?.value==='Select all listed targets');
  check(accessibleHeader?.properties.some(property=>property.name==='checked'&&property.value.value==='mixed'),'The browser accessibility tree exposes the named header checkbox as mixed');await cdp.detach();
  await header.focus();await header.press('Space');value=await state();
  check(value.checked&&!value.mixed&&await checked()===await table.locator('tbody tr').count(),'Space selects all listed targets from a partial selection');
  await header.press('Space');value=await state();
  check(!value.checked&&!value.mixed&&await checked()===0&&await saves()===count,'Space clears an all-selected category without prematurely saving');

  await box('0-a').check();await select(1);value=await state();
  check(!value.checked&&!value.mixed&&await checked()===0,'The header reflects the current category independently of a checked Window');
  await header.click();await select(0);value=await state();
  check(value.mixed&&await box('0-a').isChecked()&&!await box('0-b').isChecked(),'Selecting all Browser Tabs retains a partly selected Window category');
  await header.click();await header.click();await select(1);value=await state();
  check(value.checked&&await checked()===await table.locator('tbody tr').count(),'Clearing all Windows preserves every selected Browser Tab');
  await header.press('Control+s');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.targets.length===4&&window.settings.focusMode.targets.every(target=>target.kind===1)),'Ctrl+S from the header saves only the retained category selections');

  await reset({});await open();await header.click();
  const added={id:'0-c',key:'0-c',kind:0,name:'New window',app:'notepad',windowName:'New window'};
  await page.evaluate(added=>window.windowListing=[
    {id:'0-a',key:'0-a',kind:0,name:'Work window',app:'EXCEL',windowName:'Browser window'},
    {id:'0-b',key:'0-b',kind:0,name:'Other window',app:'chrome',windowName:'Browser window'},added
  ],added);
  const loading=await page.evaluate(async()=>{
    document.querySelector('#focus-target-refresh').click();await Promise.resolve();
    return {disabled:document.querySelector('#focus-target-select-all').disabled,inert:document.querySelector('#focus-target-list').inert};
  });
  check(loading.disabled&&loading.inert,'Refreshing disables select all while the target inventory is loading');
  await ready();value=await state();
  check(value.mixed&&!await box('0-c').isChecked()&&await box('0-a').isChecked(),'A refreshed new row stays unselected and changes the header to indeterminate');
  await header.click();
  check(await box('0-c').isChecked()&&(await state()).checked,'Select all includes rows added by the latest refresh');
  await page.evaluate(()=>{window.holdSelection=true;delete window.releaseSelection;});count=await saves();await page.locator('#focus-target-use').click();
  await page.waitForFunction(()=>document.querySelector('#focus-target-dialog').getAttribute('aria-busy')==='true');
  check(await header.isDisabled()&&await table.evaluate(list=>list.inert),'An in-flight target save disables the select-all checkbox');
  await page.waitForFunction(()=>typeof window.releaseSelection==='function');
  await page.evaluate(()=>{window.holdSelection=false;window.releaseSelection();});await dialog.waitFor({state:'hidden'});
  check(await saves()===count+1&&await page.evaluate(()=>window.settings.focusMode.targets.some(target=>target.key==='0-c')),'Saving commits a newly listed target selected through the header once');

  await page.evaluate(()=>{window.emptyTargets=true;delete window.windowListing;});await open();await page.locator('#focus-target-refresh').click();await ready();
  check(await table.locator('tbody tr.unavailable').count()===3&&(await state()).checked,'Previously selected closed Windows participate in the header state');
  await header.click();
  check(await checked()===0,'Clear all removes selected closed Windows together with the other listed targets');
  await header.click();
  check(await checked()===await table.locator('tbody tr').count(),'Select all retains the existing row behavior for listed closed ordinary targets');await close();

  await reset({targets:[{id:'0-a',key:'0-a',kind:0,name:'Work window',app:'EXCEL'}]});await open();await header.focus();
  await page.evaluate(()=>{
    const target={id:'0-shortcut-new',key:'0-shortcut-new',kind:0,name:'Shortcut window',app:'notepad'};window.choices.set(target.id,target);
    window.dispatchBridge({type:'focusTargetToggled',target,checked:true,keys:[target.key],multipleTargets:true,enabled:false});
  });
  check(await header.evaluate(input=>document.activeElement===input)&&await box('0-shortcut-new').isChecked(),'A global target toggle that redraws the list preserves focus on the header checkbox');await close();

  const savedWindow={id:'0-a',key:'0-a',kind:0,name:'Work window',app:'EXCEL'};
  const savedSite={id:'saved-site',key:'site:saved.example',kind:3,name:'saved.example',siteHost:'saved.example',app:'chrome'};
  const blockedSite={id:'blocked-site',key:'site:blocked.example',kind:3,name:'blocked.example',siteHost:'blocked.example',app:'chrome'};
  await reset({pickerKind:3,browserCompanionEnabled:true,targets:[savedWindow,savedSite]});
  await page.evaluate(()=>window.dispatchBridge({type:'browserCompanionStatus',connected:false}));await open();
  await page.evaluate(({savedSite,blockedSite})=>{
    for(const target of [savedSite,blockedSite])window.choices.set(target.id,target);
    window.dispatchBridge({type:'focusTargets',kind:3,targets:[savedSite,blockedSite],browserConnected:false});
  },{savedSite,blockedSite});value=await state();
  check(value.checked&&!value.mixed&&!value.disabled&&await box(savedSite.key).isChecked()&&!await box(blockedSite.key).isChecked(),'Disconnected Site select all counts removable saved choices while excluding blocked additions');
  await header.focus();await page.evaluate(()=>window.dispatchBridge({type:'browserCompanionStatus',connected:true}));await ready();
  check(await header.evaluate(input=>document.activeElement===input)&&await box(savedSite.key).isChecked(),'An automatic Site reconnect refresh preserves header focus and the saved selection');
  await page.evaluate(()=>window.dispatchBridge({type:'browserCompanionStatus',connected:false}));
  await header.click();value=await state();
  check(value.disabled&&!value.checked&&!value.mixed&&await checked()===0,'Clearing disconnected Sites disables the header once no eligible rows remain');
  await page.locator('#focus-target-use').click();await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].key==='0-a'),'Clearing saved Sites preserves Window selections and cannot add a disconnected Site');

  const savedTab={id:'1-a',key:'1-a',kind:1,name:'Work tab',app:'chrome',tabPosition:1};
  await reset({enabled:true,idleEnabled:true,idleSeconds:30,targets:[savedWindow,savedTab]});await open();
  await header.click();await header.click();await select(1);await header.click();await header.click();
  check(await checked()===0&&!await page.locator('#focus-target-use').isDisabled(),'Clearing every target across categories still allows saving enabled idle-only Focus');
  await page.locator('#focus-idle-seconds').press('Control+s');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.enabled&&window.settings.focusMode.targets.length===0&&window.settings.focusMode.idleEnabled&&window.settings.focusMode.idleSeconds===30),'Ctrl+S retains Focus on and the 30-second idle trigger with no selected targets');
  check(await page.locator('#focus-enabled').getAttribute('aria-pressed')==='true','The Focus button remains on after saving an empty target selection with idle detection');
  await open();
  check(await checked()===0&&await page.locator('#focus-idle-enabled').isChecked()&&await page.locator('#focus-idle-seconds').inputValue()==='30','Reopening idle-only Focus preserves zero checked targets and its 30-second inactivity setting');await close();

  const centeredCheckboxes=()=>table.locator('.focus-selection-cell input[type=checkbox]').evaluateAll(inputs=>inputs.length>1&&inputs.every(input=>{
    const box=input.getBoundingClientRect(),cell=input.parentElement.getBoundingClientRect();
    return Math.abs((box.top+box.bottom-cell.top-cell.bottom)/2)<.75
      &&box.width===16&&box.height===16&&cell.width<=33
      &&box.left-cell.left>=7.5&&cell.right-box.right>=7.5;
  }));
  for(const theme of [0,1,2,3]){
    await reset({browserCompanionEnabled:true});
    await page.evaluate(theme=>{
      window.settings.theme=theme;window.longNames=true;
      window.dispatchBridge({type:'settings',settings:window.settings});
      window.dispatchBridge({type:'browserCompanionStatus',connected:true});
    },theme);await open();
    for(const targetKind of [0,1,2,3]){
      await select(targetKind);
      if(targetKind===3)await page.evaluate(({savedSite,blockedSite})=>{
        const targets=[{...savedSite,name:'Long website target '.repeat(25)},blockedSite];
        for(const target of targets)window.choices.set(target.id,target);
        window.dispatchBridge({type:'focusTargets',kind:3,targets,browserConnected:true});
      },{savedSite,blockedSite});
      check(await centeredCheckboxes(),'Theme '+theme+', category '+targetKind+' vertically centers ordinary, dynamic and header checkboxes within the original narrow column');
      const ordinary=box(targetKind===3?savedSite.key:targetKind+'-a');
      const row=ordinary.locator('..').locator('..'),shortHeight=await row.evaluate(row=>row.getBoundingClientRect().height);
      await ordinary.focus();
      check(await row.evaluate(row=>row.getBoundingClientRect().height)>shortHeight&&await centeredCheckboxes(),'Theme '+theme+', category '+targetKind+' keeps checkboxes centered when a focused full target name wraps');
      if(targetKind!==3){
        await box(targetKind+'-background').focus();
        check(await centeredCheckboxes(),'Theme '+theme+', category '+targetKind+' also centers a focused dynamic choice and the sticky selection header');
      }
    }
    await close();
  }
  await page.evaluate(()=>{window.settings.theme=0;window.longNames=false;window.dispatchBridge({type:'settings',settings:window.settings});});
};
