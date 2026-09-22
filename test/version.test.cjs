const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const {syncVersions,validateVersion,mirrors}=require('../scripts/version.cjs');
const root=path.resolve(__dirname,'..');
test('current release mirrors match the single version source',()=>{assert.equal(syncVersions(),fs.readFileSync(path.join(root,'VERSION'),'utf8').trim());});
test('invalid version input fails closed',()=>{for(const value of ['','1.0','1.2.3-rc','1.2.3\nextra','$(Injected)'])assert.throws(()=>validateVersion(value));});
test('version synchronization repairs all mirrors and preserves historical notes',()=>{
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),'reflection-version-'));
  try {
    for(const file of ['VERSION','package.json',...mirrors]){fs.mkdirSync(path.dirname(path.join(temp,file)),{recursive:true});fs.copyFileSync(path.join(root,file),path.join(temp,file));}
    fs.writeFileSync(path.join(temp,'VERSION'),'9.8.7\n');
    assert.throws(()=>syncVersions(temp),/Version mismatch/);
    assert.equal(syncVersions(temp,true),'9.8.7');
    assert.equal(syncVersions(temp),'9.8.7');
    assert.match(fs.readFileSync(path.join(temp,'desktop/HOTKEYS.md'),'utf8'),/old 3\.6\.4 build/);
    const html=path.join(temp,'desktop/ReflectionTimer.Desktop/Web/index.html');
    fs.writeFileSync(html,fs.readFileSync(html,'utf8').replace('9.8.7','9.8.6'));
    assert.throws(()=>syncVersions(temp),/Version mismatch/);
  } finally {fs.rmSync(temp,{recursive:true,force:true});}
});
test('CI and packaging use the same validation gate without publishing credentials',()=>{
  const workflow=fs.readFileSync(path.join(root,'.github/workflows/validate.yml'),'utf8');
  const packaging=fs.readFileSync(path.join(root,'desktop/package-release.ps1'),'utf8');
  assert.match(workflow,/contents: read/);assert.match(workflow,/persist-credentials: false/);
  assert.match(workflow,/desktop\/validate\.ps1/);assert.match(packaging,/validate\.ps1/);
  assert.doesNotMatch(workflow,/secrets\.|pull_request_target|contents: write/);
});
