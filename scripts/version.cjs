// VERSION is authoritative; these user-facing labels are generated mirrors.
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const mirrors = [
  'README.md', 'desktop/README.md', 'desktop/ReflectionTimer.Desktop/README.md',
  'desktop/ReflectionTimer.Desktop/START-HERE.txt', 'desktop/START-HERE.html',
  'desktop/HOTKEYS.md', 'desktop/ReflectionTimer.Desktop/Web/index.html'
];
const labelPattern = /(Reflection Timer(?: desktop)?(?: ·)? |desktop source is |desktop version, \*\*|Extract the verified |It should say )(\d+\.\d+\.\d+)/g;
function validateVersion(value) {
  if (!/^\d+\.\d+\.\d+$/.test(value)) throw new Error('VERSION must contain a three-part release version.');
  return value;
}
function syncVersions(base = root, write = false) {
  const version = validateVersion(fs.readFileSync(path.join(base, 'VERSION'), 'utf8').trim());
  const packagePath = path.join(base, 'package.json');
  const pkg = JSON.parse(fs.readFileSync(packagePath, 'utf8'));
  if(typeof pkg.version!=='string')throw new Error('package.json must have a version mirror.');
  const mismatches = [];
  function update(file, before, after) {
    if (before === after) return;
    if (write) fs.writeFileSync(path.join(base, file), after);
    else mismatches.push(file);
  }
  const packageText = fs.readFileSync(packagePath, 'utf8');
  update('package.json', packageText, packageText.replace(/("version"\s*:\s*")[^"]+/, '$1' + version));
  for (const file of mirrors) {
    const before = fs.readFileSync(path.join(base, file), 'utf8');
    // Match current labels, not historical release notes or archived projects.
    if (![...before.matchAll(labelPattern)].length) throw new Error('Missing current version label: ' + file);
    const after = before.replace(labelPattern, (_, prefix) => prefix + version);
    update(file, before, after);
  }
  const lockPath = path.join(base, 'package-lock.json');
  if (fs.existsSync(lockPath)) {
    const before = fs.readFileSync(lockPath, 'utf8');
    const lock = JSON.parse(before);
    if (lock.version !== version || lock.packages?.['']?.version !== version) {
      lock.version = version;
      if (lock.packages?.['']) lock.packages[''].version = version;
      update('package-lock.json', before, JSON.stringify(lock, null, 2) + '\n');
    }
  }
  if (mismatches.length) throw new Error('Version mismatch: ' + mismatches.join(', ') + '. Run node scripts/version.cjs --write.');
  return version;
}
if (require.main === module) {
  try { console.log('Version consistency passed: ' + syncVersions(root, process.argv.includes('--write'))); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = {syncVersions, validateVersion, mirrors};
