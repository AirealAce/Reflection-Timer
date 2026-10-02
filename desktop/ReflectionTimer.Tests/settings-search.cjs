// Reuse the synthetic, disconnected Help harness; search never uses the user profile.
module.exports=async function settingsSearch(page,check){
  const input=page.getByRole('searchbox',{name:'Search settings',exact:true}),status=page.locator('#settings-search-status');
  const query=async text=>{await input.fill(text);await page.waitForTimeout(220);};
  check(await input.isVisible()&&await page.locator('#panel-settings').evaluate(panel=>panel.firstElementChild.id==='settings-search'),'Settings begins with a labeled search landmark and search bar');
  check(await page.getByRole('search',{name:'Settings',exact:true}).count()===1,'Settings search exposes its own screen-reader landmark');
  const before=await page.evaluate(()=>window.messages.filter(m=>!['settingsShortcutScope'].includes(m.action)).length);
  await query('color theme');
  check(await page.locator('#theme').isVisible()&&!await page.locator('#confirmBeforeReset').isVisible()&&!await page.locator('#sound-track-5').isVisible(),'Multiple search words keep the matching theme settings and filter unrelated controls');
  check(await status.textContent()==='1 settings section found.'&&await input.evaluate(e=>e===document.activeElement),'Search announces its result count while keeping keyboard focus in the search field');
  check(await page.evaluate(()=>window.messages.filter(m=>!['settingsShortcutScope'].includes(m.action)).length)===before,'Typing a settings search does not save preferences, play audio, or call the host');
  await query('LOW-ON-TIME');
  check(await page.locator('#default-threshold').isVisible()&&await page.locator('#sound-track-3').isVisible()&&!await page.locator('#theme').isVisible(),'Search ignores case and punctuation and finds the low-on-time controls');
  await query('fade');
  check(await page.locator('#sound-fade-0').isVisible()&&await page.locator('#sound-fade-5').isVisible()&&!await page.locator('#connection-form').isVisible(),'Shared audio settings can be found across their individual event sections');
  await query('μnonexistent setting 942');
  check(await status.textContent()==='No matching settings.'&&await page.locator('#panel-settings>section:visible').count()===0,'An unmatched query has a readable empty result without unrelated settings');
  await page.getByRole('button',{name:'Clear search',exact:true}).click();
  check(await input.inputValue()===''&&await input.evaluate(e=>e===document.activeElement)&&await page.locator('#theme').isVisible()&&await page.locator('#connection-form').isVisible(),'Clear search restores every setting and returns focus to the search bar');
  check(await page.locator('[data-help]').evaluateAll(nodes=>nodes.every(node=>node.hidden)),'Searching and clearing leave collapsed help state unchanged');
  await page.locator('#help-toggle-reset').click();
  await query('theme');await page.getByRole('button',{name:'Clear search',exact:true}).click();
  check(await page.locator('#reset-confirmation-help').isVisible()&&await page.locator('#help-toggle-reset').getAttribute('aria-expanded')==='true','An expanded help section keeps its existing state through filtering');
  await page.locator('#help-toggle-reset').click();
  await page.locator('#sheet-url').fill('https://private.invalid/unique-private-value-938');
  await query('unique-private-value-938');
  check(await status.textContent()==='No matching settings.','Private text input values are excluded from the search index');
  await query('google sheets');
  check(await page.locator('#sheet-url').isVisible()&&await page.locator('#sheet-url').inputValue()==='https://private.invalid/unique-private-value-938','Filtering preserves unfinished connection text in the original control');
  await page.locator('#sheet-url').fill('');
  await query('focus');
  await page.locator('#focus-settings-target').evaluate(e=>e.textContent='unique-private-target-835');
  await query('unique-private-target-835');
  check(await status.textContent()==='No matching settings.','Saved focus-target names are excluded from the settings search index');
  await page.locator('#focus-settings-target').evaluate(e=>e.textContent='No focus target selected.');
  await page.getByRole('button',{name:'Clear search',exact:true}).click();
  await query('theme');
  await page.locator('#tab-timer').click();
  check(await page.locator('#minutes').isVisible()&&!await input.isVisible(),'A settings search never filters other App tabs');
  await page.locator('#tab-settings').click();
  check(await input.inputValue()==='theme'&&!await page.locator('#confirmBeforeReset').isVisible(),'Returning to Settings retains the current search');
  await page.locator('#viewerAutoHideSeconds').evaluate(e=>e.value='0');
  const valid=await page.locator('#appearance-form').evaluate(form=>form.reportValidity());
  check(!valid&&await input.inputValue()===''&&await page.locator('#viewerAutoHideSeconds').isVisible()&&await page.locator('#viewerAutoHideSeconds').evaluate(e=>e===document.activeElement),'Saving reveals and focuses an invalid pending setting hidden by the search');
  await page.locator('#viewerAutoHideSeconds').evaluate(e=>e.value='3');
  await query('theme');
  for(const key of ['Control+s','Control+Enter']){
    const saves=await page.evaluate(()=>window.messages.filter(m=>m.action==='settingsSaveComplete').length);
    await input.focus();await page.keyboard.press(key);
    await page.waitForFunction(before=>window.messages.filter(m=>m.action==='settingsSaveComplete').length===before+1,saves);
    check(await input.evaluate(e=>e===document.activeElement)&&await input.inputValue()==='theme',key+' saves Settings from the search bar without losing the query or focus');
  }
  await page.getByRole('button',{name:'Clear search',exact:true}).click();
  for(const theme of [0,1,2,3]){
    await page.evaluate(theme=>window.dispatchBridge({type:'settings',settings:{...window.settings,theme}}),theme);
    await query('color theme');
    check(await input.evaluate(e=>e.getBoundingClientRect().right<=document.querySelector('#panel-settings').getBoundingClientRect().right+1)&&await page.locator('#settings-search-clear').isVisible(),'The settings search and clear button fit theme '+theme);
    await page.getByRole('button',{name:'Clear search',exact:true}).click();
  }
  await page.evaluate(()=>window.dispatchBridge({type:'settings',settings:{...window.settings,theme:0}}));
};
