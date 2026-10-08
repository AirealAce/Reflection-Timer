// Scan tracked and non-ignored new source without printing private values.
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const {approvedTracks, validateBundledAudio} = require('./bundled-audio.cjs');
const root = path.join(__dirname, '..');
const files = [...new Set(execFileSync('git', ['ls-files', '--cached', '--others', '--exclude-standard', '-z'], {cwd:root}).toString().split('\0').filter(Boolean))];
const rules = [
  ['private key', /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/],
  ['GitHub token', /\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b/],
  ['Google API key', /\bAIza[A-Za-z0-9_-]{35}\b/],
  ['Google OAuth token', /\bya29\.[A-Za-z0-9_-]{30,}/],
  ['personal spreadsheet URL', /https:\/\/docs\.google\.com\/spreadsheets\/d\/[A-Za-z0-9_-]{40,}/],
  ['personal receiver URL', /https:\/\/script\.google\.com\/macros\/s\/[A-Za-z0-9_-]{40,}/],
  ['private build path', /[A-Z]:\\Users\\[A-Za-z0-9._-]+\\/i]
];
const tracks = approvedTracks();
const approvedAudio = new Set(tracks.map(track => track.repositoryPath));
const findings = validateBundledAudio();
for (const file of files) {
  if (/(?:^|\/)(?:\.env(?:\..+)?|state\.dat.*|diagnostics\.dat.*|credentials.*\.json|private-setup.*)$|\.(?:pem|key|pdb)$/i.test(file) && !file.endsWith('.env.example'))
    findings.push({file, problem:'private package input'});
  if (/\.mp3$/i.test(file) && !approvedAudio.has(file)) findings.push({file, problem:'unapproved audio input'});
  if (/\.(?:png|webp|ico|gif|jpe?g|wav)$/i.test(file)) continue;
  const bytes = fs.readFileSync(path.join(root, file));
  for (const encoding of ['utf8', 'utf16le']) {
    const text = bytes.toString(encoding);
    for (const [problem, pattern] of rules) if (pattern.test(text)) findings.push({file, problem});
  }
}
if (findings.length) {
  console.error(JSON.stringify({findings}, null, 2));
  process.exitCode = 1;
} else console.log(`Public-source scan passed for ${files.length} source files (pattern scan; not a security guarantee).`);
