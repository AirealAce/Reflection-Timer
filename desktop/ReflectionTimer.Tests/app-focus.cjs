// Synthetic bridge only: never opens or changes the installed user's profile.
module.exports=async(page,check)=>{
  const dialog=page.locator('#focus-target-dialog'),table=page.locator('#focus-target-list'),kind=page.locator('#focus-target-kind');
  const row=key=>table.locator(`tbody tr[data-key="${key}"]`),header=()=>page.locator('#focus-target-select-all');
  const ready=()=>page.waitForFunction(()=>document.querySelector('#focus-target-dialog').open&&document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false');
  const open=async()=>{await page.locator('#choose-focus-target-settings').click();await ready();};
  const close=async()=>{await page.locator('#focus-target-cancel').click();await dialog.waitFor({state:'hidden'});};
  const select=async value=>{await kind.selectOption(String(value));await ready();};
  const reset=async focus=>page.evaluate(focus=>{
    delete window.windowListing;delete window.windowReplacements;
    window.emptyTargets=false;window.repeatTarget=false;window.longNames=false;
    window.holdSelection=false;window.selectionError=null;
    window.settings.focusMode={enabled:false,delaySeconds:5,targets:[],pickerKind:4,multipleTargets:false,idleEnabled:false,idleSeconds:20,...focus};
    window.dispatchBridge({type:'settings',settings:window.settings});
  },focus);
  await reset({});await open();
  check(await kind.inputValue()==='4'&&await kind.locator('option[value="4"]').textContent()==='Apps','The saved Apps category opens without needing the browser companion');
  check(await table.locator('caption').textContent()==='Apps'&&await table.locator('th').allTextContents().then(values=>values.join('|')==='App'),'Apps use the existing table with one left-aligned App column');
  check(await table.locator('tbody tr').count()===2&&!await table.locator('tbody tr').evaluateAll(rows=>rows.some(row=>row.textContent.includes('Use focused'))),'Apps list one ordinary entry per returned app without dynamic window or tab choices');
  check(await row('4-a').locator('td').allTextContents().then(values=>values.join('|')==='Google Chrome')&&await row('4-b').locator('td').allTextContents().then(values=>values.join('|')==='Notepad'),'Apps display their app names without tab numbers or window titles');
  check(await row('4-a').getAttribute('aria-label')==='App: Google Chrome — chrome','App rows expose the app category and name to screen readers');
  await page.evaluate(()=>window.dispatchBridge({type:'focusTargets',kind:4,targets:[...window.choices.values()].filter(target=>target.kind===4).map(target=>({...target,current:target.key==='4-a'}))}));
  check(await row('4-a').getAttribute('aria-label').then(label=>label.includes('(current app)')&&!label.includes('(current tab)'))&&await row('4-a').locator('td').textContent()==='(current app) Google Chrome','A foreground App is labelled current app visually and for screen readers');
  check(await table.locator('th').evaluate(cell=>getComputedStyle(cell).textAlign==='left'),'The Apps header follows the other left-aligned column headers');
  await page.locator('#help-toggle-focus-target-type').click();
  check(await page.locator('#focus-target-type-help').isVisible()&&await page.locator('#focus-target-type-help').textContent().then(text=>text.includes('after restarting the app')),'Target type help explains that an App includes future windows and restarts');
  await row('4-b').focus();await row('4-b').press('Enter');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.pickerKind===4&&window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].key==='4-b'),'Enter saves the app identity and Apps category');
  const button=page.locator('#choose-focus-target-settings');
  check(await button.locator('.focus-target-type').textContent()==='App'&&await button.locator('.focus-target-label').textContent()==='Notepad'&&await button.getAttribute('aria-label')==='App: Notepad','A single selected App displays App above its name with a matching screen-reader label');
  check(await button.evaluate(button=>button.classList.contains('primary')),'A saved App highlights the target button');
  await open();
  check(await kind.inputValue()==='4'&&await row('4-b').evaluate(row=>row.classList.contains('picked')),'Reopening keeps the saved App checked in the Apps category');
  await row('4-a').focus();await row('4-a').press('Space');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.targets[0].key==='4-a'),'Space confirms an App in single-target mode');
  await open();await row('4-b').dblclick();await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.targets[0].key==='4-b'),'Double-click confirms an App in single-target mode');

  await reset({multipleTargets:true});await open();
  check(await header().isVisible()&&await table.locator('th').allTextContents().then(values=>values.join('|')==='|App'),'Multiple Targets adds the narrow checkbox column to Apps without a Select label');
  await row('4-a').getByRole('checkbox').check();
  check(await header().evaluate(input=>input.indeterminate),'The Apps select-all header exposes a partial selection');
  await header().press('Space');
  check(await row('4-a').getByRole('checkbox').isChecked()&&await row('4-b').getByRole('checkbox').isChecked()&&await header().isChecked(),'Space on the Apps header selects all listed Apps');
  const cdp=await page.context().newCDPSession(page),tree=await cdp.send('Accessibility.getFullAXTree');
  check(tree.nodes.some(node=>!node.ignored&&node.role?.value==='checkbox'&&node.name?.value==='App: Google Chrome — chrome'&&node.properties?.some(property=>property.name==='checked'&&property.value.value==='true')),'The accessibility tree exposes App entries as named, checked checkboxes');await cdp.detach();
  await page.locator('#focus-target-search-input').fill('notepad');
  check(await row('4-a').isHidden()&&await row('4-b').isVisible()&&await header().isChecked(),'Searching Apps filters the table and the header selection to matching apps');
  await header().click();await page.locator('#focus-target-search-clear').click();
  check(await row('4-a').getByRole('checkbox').isChecked()&&!await row('4-b').getByRole('checkbox').isChecked(),'Clearing a filtered Apps header preserves checked apps outside the search');
  await select(0);await row('0-a').getByRole('checkbox').check();await select(1);await row('1-a').getByRole('checkbox').check();await select(4);
  check(await row('4-a').getByRole('checkbox').isChecked(),'Switching from Apps through Window and Tab retains the checked App');
  await page.locator('#focus-target-search-input').press('Control+s');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.targets.map(target=>target.kind).sort().join(',')==='0,1,4'&&window.settings.focusMode.pickerKind===4),'Ctrl+S saves Apps together with the selected Window and Tab');
  check(await button.locator('.focus-target-type').textContent()==='App, Window, Tab'&&await button.locator('.focus-target-label').isHidden(),'The multiple-target button lists App before Window and Tab in its category summary');
  await open();await row('4-b').getByRole('checkbox').check();await row('4-b').getByRole('checkbox').press('Control+Enter');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.targets.filter(target=>target.kind===4).length===2),'Ctrl+Enter saves from an App checkbox');
  await reset({multipleTargets:true,targets:[{id:'4-a',key:'4-a',kind:4,name:'Google Chrome',app:'Google Chrome'},{id:'1-background',key:'1-background',kind:1,useFocused:true,captureScope:1,name:'Use focused tabs (including background)'}]});await open();
  if(!await page.locator('#focus-target-type-help').isVisible())await page.locator('#help-toggle-focus-target-type').click();
  check(await page.locator('#focus-picker-target-overlap').isVisible(),'Background-tab overlap guidance also accounts for a browser App with a friendly app label');await close();

  const saved={id:'saved-app',key:'app:stable-app',kind:4,name:'Saved App',app:'savedapp'};
  await reset({multipleTargets:true,targets:[saved]});await open();
  check(await row(saved.key).getByRole('checkbox').isChecked()&&!await row(saved.key).evaluate(row=>row.classList.contains('unavailable')),'An App that is currently closed stays checked and keeps its ordinary style');
  check(await row(saved.key).getAttribute('aria-label').then(label=>label.includes('not running; included when opened'))&&await page.locator('#focus-picker-status').textContent().then(text=>text.includes('included when opened again')),'A closed saved App explains that its future windows are still targeted');
  await page.locator('#focus-target-refresh').click();await ready();
  check(await row(saved.key).getByRole('checkbox').isChecked()&&!await row(saved.key).evaluate(row=>row.classList.contains('unavailable')),'Refresh cannot remove or gray a saved closed App');
  await page.locator('#focus-target-search-input').fill('savedapp');
  check(await row(saved.key).isVisible()&&await row('4-a').isHidden(),'Apps can also be searched by their process label');
  await page.locator('#focus-target-search-input').press('Control+s');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.targets.length===1&&window.settings.focusMode.targets[0].key==='app:stable-app'),'Saving a closed App retains its durable key');

  await open();
  await page.evaluate(saved=>{
    const returned={...saved,id:'reopened-app'};window.choices.set(returned.id,returned);
    window.dispatchBridge({type:'focusTargets',kind:4,targets:[returned]});
  },saved);
  check(await table.locator('tbody tr').count()===1&&await row(saved.key).getByRole('checkbox').isChecked()&&!await row(saved.key).getAttribute('aria-label').then(label=>label.includes('not running')),'A reopened App with a new request ID replaces its dormant row by durable key without duplication');
  await page.locator('#focus-target-kind').press('Control+s');await dialog.waitFor({state:'hidden'});
  check(await page.evaluate(()=>window.settings.focusMode.targets[0].id==='reopened-app'&&window.settings.focusMode.targets[0].key==='app:stable-app'),'The reopened App saves its refreshed ID while retaining the same durable identity');

  for(const theme of [0,1,2,3]){
    await reset({multipleTargets:true});await page.evaluate(theme=>{window.longNames=true;window.settings.theme=theme;window.dispatchBridge({type:'settings',settings:window.settings});},theme);await open();
    check(await table.evaluate(list=>{
      const boxes=[...list.querySelectorAll('.focus-selection-cell input')];
      return boxes.length===3&&boxes.every(input=>{const box=input.getBoundingClientRect(),cell=input.parentElement.getBoundingClientRect();return cell.width<=33&&Math.abs((box.top+box.bottom-cell.top-cell.bottom)/2)<.75&&box.left-cell.left>=7.5&&cell.right-box.right>=7.5;})&&list.scrollWidth<=list.clientWidth+1;
    }),`Apps retain centered narrow checkboxes and no horizontal overflow in theme ${theme}`);
    check(await row('4-a').locator('.focus-cell').evaluate(cell=>{const style=getComputedStyle(cell);return style.textOverflow==='ellipsis'&&style.whiteSpace==='nowrap';}),`Long App names keep the existing ellipsis style until focused in theme ${theme}`);
    await row('4-a').getByRole('checkbox').focus();
    check(await row('4-a').locator('.focus-cell').evaluate(cell=>getComputedStyle(cell).whiteSpace==='normal'),`Focused App names use the existing wrapping style in theme ${theme}`);await close();
  }
};
