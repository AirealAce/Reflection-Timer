// The reviewed catalog is an exact allowlist, not a wildcard for local audio.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.join(__dirname, '..');

function approvedTracks(catalog = JSON.parse(fs.readFileSync(path.join(root, 'desktop/Sounds/sources.json'), 'utf8'))) {
  if (catalog.formatVersion !== 1 || !Array.isArray(catalog.tracks) || catalog.tracks.length !== 14)
    throw new Error('Invalid bundled audio catalog.');
  const names = new Set();
  for (const track of catalog.tracks) {
    if (!/^[a-z0-9-]+\.mp3$/.test(track.file) || names.has(track.file)
      || track.repositoryPath !== (track.file === 'popup.mp3' ? 'popup.mp3' : 'desktop/Sounds/' + track.file)
      || !/^[a-f0-9]{64}$/.test(track.sha256) || !Number.isSafeInteger(track.bytes) || track.bytes < 1)
      throw new Error('Invalid bundled audio entry.');
    names.add(track.file);
  }
  for (const kind of ['Success', 'Failure', 'LowTime', 'SessionEnd'])
    if (!names.has(catalog.defaults?.[kind])) throw new Error('Missing bundled audio default.');
  return catalog.tracks;
}

function validateBundledAudio(directory = root, packageMode = false, tracks = approvedTracks()) {
  const errors = [];
  const approved = new Set(tracks.map(track => packageMode ? track.file : track.repositoryPath));
  for (const track of tracks) {
    const relative = packageMode ? track.file : track.repositoryPath;
    const file = path.join(directory, relative);
    if (!fs.existsSync(file)) { errors.push({file:relative, problem:'missing bundled audio'}); continue; }
    if (fs.lstatSync(file).isSymbolicLink() || !fs.statSync(file).isFile()) {
      errors.push({file:relative, problem:'redirected bundled audio'}); continue;
    }
    const bytes = fs.readFileSync(file);
    if (bytes.length !== track.bytes || crypto.createHash('sha256').update(bytes).digest('hex') !== track.sha256)
      errors.push({file:relative, problem:'bundled audio checksum mismatch'});
  }
  if (packageMode) {
    function walk(parent) {
      for (const entry of fs.readdirSync(parent, {withFileTypes:true})) {
        const full = path.join(parent, entry.name);
        const relative = path.relative(directory, full).split(path.sep).join('/');
        if (entry.isSymbolicLink()) errors.push({file:relative, problem:'redirected package file'});
        else if (entry.isDirectory()) walk(full);
        else if (/\.mp3$/i.test(entry.name) && !approved.has(relative))
          errors.push({file:relative, problem:'unapproved package audio'});
      }
    }
    walk(directory);
  }
  return errors;
}

module.exports = {approvedTracks, validateBundledAudio};
if (require.main === module) {
  const errors = validateBundledAudio(process.argv[2] ?? root, Boolean(process.argv[2]));
  if (errors.length) { console.error(JSON.stringify({findings:errors}, null, 2)); process.exitCode = 1; }
  else console.log(`Verified all ${approvedTracks().length} bundled MP3s against the audio catalog.`);
}
