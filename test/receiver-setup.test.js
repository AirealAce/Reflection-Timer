'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '..', 'google-sheets-script.gs'), 'utf8');
const ID = 'synthetic-spreadsheet-owned-by-new-user';
const TOKEN = 'a'.repeat(64);

function fixture(names = [], initialProperties = {}, activeId = ID) {
  const props = new Map(Object.entries(initialProperties)); const sheets = new Map(); const created = []; const logs = [];
  const lock = { locked: false, waitLock() { this.locked = true; }, releaseLock() { this.locked = false; }, hasLock() { return this.locked; } };
  function sheet(name) {
    return { name, edits: [], getName() { return name; }, getMaxRows() { return 1000; }, getMaxColumns() { return 26; },
      setColumnWidth(c, w) { this.edits.push(['width', c, w]); },
      getRange(...args) {
        const edits = this.edits; edits.push(['range', ...args]);
        return Object.fromEntries(['setBackgrounds', 'setFontColors', 'setBorder', 'setWrap'].map(method => [method, function (...values) { edits.push([method, ...values]); return this; }]));
      }
    };
  }
  for (const name of names) sheets.set(name, sheet(name));
  const spreadsheet = { getId: () => activeId, getSheetByName: name => sheets.get(name), insertSheet(name) {
    assert.equal(lock.locked, true); assert.equal(sheets.has(name), false);
    const item = sheet(name); sheets.set(name, item); created.push(name); return item;
  } };
  const context = { console: { log: text => logs.push(text), error() {} },
    LockService: { getScriptLock: () => lock },
    PropertiesService: { getScriptProperties: () => ({ getProperty: key => props.get(key) || null, setProperty: (key, value) => props.set(key, value) }) },
    SpreadsheetApp: { getActiveSpreadsheet: () => activeId === null ? null : spreadsheet, openById: () => spreadsheet, flush() {}, BorderStyle: { SOLID: 'SOLID' } },
    ContentService: { MimeType: { JSON: 'JSON' }, createTextOutput(text) { return { text, setMimeType() { return this; } }; } },
    Utilities: { Charset: { UTF_8: 'utf8' }, DigestAlgorithm: { SHA_256: 'sha256' }, computeDigest: (_, value) => [...crypto.createHash('sha256').update(value).digest()] }
  };
  vm.createContext(context); vm.runInContext(source, context);
  return { context, props, sheets, created, logs, lock };
}
test('fresh receiver setup creates only missing Temp/test with consistent colors and private properties', () => {
  const f = fixture(['Sheet1']); f.context.initializeReflectionTimer_(ID, TOKEN);
  assert.deepEqual(f.created, ['Temp', 'test']); assert.equal(f.props.get('SPREADSHEET_ID'), ID); assert.equal(f.props.get('REFLECTION_API_TOKEN'), TOKEN);
  assert.equal(f.props.get('TEMPLATE_SHEET_NAME'), 'Temp'); assert.deepEqual(f.sheets.get('Sheet1').edits, []);
  const colors = f.sheets.get('Temp').edits.find(x => x[0] === 'setBackgrounds')[1][0];
  assert.deepEqual([...colors], ['#ffffff', '#000000', '#ffffff', '#000000', '#ffffff', '#000000', '#ffffff']);
  assert.deepEqual(f.sheets.get('Temp').edits.filter(x => x[0] === 'width').map(x => x.slice(1)),
    [[1, 95], [2, 440], [3, 220], [4, 220], [5, 135], [6, 300], [7, 135]]);
  assert.equal(f.logs.some(x => x.includes(TOKEN)), false); assert.equal(f.lock.locked, false);
});
for (const name of ['Temp', 'Template', 'My template']) test('receiver setup preserves existing ' + name + ' and test entirely, including receipts', () => {
  const f = fixture([name, 'test', '09/09/2026'], { TEMPLATE_SHEET_NAME: name, RT_RECEIPT_existing: 'preserve', unrelated: 'preserve' });
  f.context.initializeReflectionTimer_(ID, TOKEN); f.context.initializeReflectionTimer_(ID, TOKEN);
  assert.deepEqual(f.created, []); assert.equal(f.props.get('RT_RECEIPT_existing'), 'preserve'); assert.equal(f.props.get('unrelated'), 'preserve');
  for (const sheet of f.sheets.values()) assert.deepEqual(sheet.edits, []);
});
test('setup recognizes a legacy Template without changing its contents', () => {
  const f = fixture(['Template']); f.context.initializeReflectionTimer_(ID, TOKEN);
  assert.equal(f.props.get('TEMPLATE_SHEET_NAME'), 'Template'); assert.deepEqual(f.created, ['test']); assert.deepEqual(f.sheets.get('Template').edits, []);
});
test('receiver setup refuses a wrong or unbound spreadsheet before any mutation', () => {
  for (const id of ['different-synthetic-spreadsheet-id', null]) {
    const f = fixture([], {}, id); assert.throws(() => f.context.initializeReflectionTimer_(ID, TOKEN), /same spreadsheet/);
    assert.equal(f.props.size, 0); assert.deepEqual(f.created, []);
  }
});
test('receiver setup never silently rotates an existing secret or rebinds another user', () => {
  for (const props of [{ REFLECTION_API_TOKEN: 'b'.repeat(64) }, { SPREADSHEET_ID: 'different-synthetic-spreadsheet-id' }]) {
    const f = fixture([], props); assert.throws(() => f.context.initializeReflectionTimer_(ID, TOKEN), /different connection/);
    assert.deepEqual(Object.fromEntries(f.props), props); assert.deepEqual(f.created, []); assert.equal(f.lock.locked, false);
  }
});
test('public HTTP handler cannot invoke setup, even with a valid token', () => {
  const f = fixture([], { SPREADSHEET_ID: ID, REFLECTION_API_TOKEN: TOKEN });
  const response = JSON.parse(f.context.doPost({ postData: { contents: JSON.stringify({ action: 'setupReflectionTimer', token: TOKEN, sheetUrl: 'https://docs.google.com/spreadsheets/d/' + ID + '/edit' }) } }).text);
  assert.equal(response.success, false); assert.match(response.error, /Unsupported action/); assert.deepEqual(f.created, []);
});
