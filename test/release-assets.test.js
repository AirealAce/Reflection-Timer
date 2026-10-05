const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const {execFileSync} = require('node:child_process');
const {approvedTracks, validateBundledAudio} = require('../scripts/bundled-audio.cjs');
const root = path.join(__dirname, '..');

test('all fourteen catalogued MP3s exist and match their recorded hashes', () => {
  assert.equal(approvedTracks().length, 14);
  assert.deepEqual(validateBundledAudio(), []);
});

test('the audio catalog rejects unsafe paths, duplicates and missing defaults', () => {
  const catalog = JSON.parse(fs.readFileSync(path.join(root, 'desktop/Sounds/sources.json'), 'utf8'));
  for (const change of [
    c => { c.tracks[0].repositoryPath = '../private.mp3'; },
    c => { c.tracks[0].file = '../private.mp3'; },
    c => { c.tracks[0] = c.tracks[1]; },
    c => { c.tracks[0].sha256 = 'invalid'; },
    c => { c.defaults.Success = 'missing.mp3'; }
  ]) {
    const altered = structuredClone(catalog); change(altered);
    assert.throws(() => approvedTracks(altered));
  }
});

test('public packages reject missing, changed and additional MP3s', t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ReflectionTimer-QA-audio-package-'));
  t.after(() => fs.rmSync(directory, {recursive:true, force:true}));
  const tracks = approvedTracks();
  for (const track of tracks) fs.copyFileSync(path.join(root, track.repositoryPath), path.join(directory, track.file));
  assert.deepEqual(validateBundledAudio(directory, true), []);
  const target = path.join(directory, tracks[0].file);
  fs.appendFileSync(target, 'corrupt');
  assert.ok(validateBundledAudio(directory, true).some(x => x.problem === 'bundled audio checksum mismatch'));
  fs.rmSync(target);
  assert.ok(validateBundledAudio(directory, true).some(x => x.problem === 'missing bundled audio'));
  fs.writeFileSync(path.join(directory, 'private.mp3'), 'private test fixture');
  fs.mkdirSync(path.join(directory, 'nested'));
  fs.writeFileSync(path.join(directory, 'nested/private.mp3'), 'private test fixture');
  assert.equal(validateBundledAudio(directory, true).filter(x => x.problem === 'unapproved package audio').length, 2);
});

test('desktop includes only the approved clips in both developer and public builds', () => {
  const project = fs.readFileSync(path.join(root, 'desktop/ReflectionTimer.Desktop/ReflectionTimer.Desktop.csproj'), 'utf8');
  const elements = [...project.matchAll(/<Content\s+Include="([^"]*\.mp3)"[^>]*\/>/g)];
  const included = elements.flatMap(match => {
    assert.ok(!match[0].includes('Condition='), 'bundled audio must also ship in public builds');
    assert.ok(!match[1].includes('*'), 'do not ship wildcard-selected personal audio');
    return match[1].split(';').map(x => path.relative(root, path.resolve(root, 'desktop/ReflectionTimer.Desktop', x)).split(path.sep).join('/'));
  });
  assert.deepEqual(included.sort(), approvedTracks().map(x => x.repositoryPath).sort());
  assert.ok(project.includes('Sounds/AUDIO-NOTICES.txt'));
  const ignored = execFileSync('git', ['check-ignore', '--stdin'], {cwd:root, input:'private.mp3\ndesktop/Sounds/my-private.mp3\n', encoding:'utf8'});
  assert.equal(ignored.trim().split(/\r?\n/).length, 2);
});
