'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'google-sheets-script.gs'), 'utf8');
const SPREADSHEET_ID = 'synthetic-spreadsheet-id-for-tests';
const TOKEN = 'test-token-that-is-long-enough';

function columnLetter(column) {
  let result = '';
  for (let value = column; value > 0; value = Math.floor((value - 1) / 26)) {
    result = String.fromCharCode(65 + ((value - 1) % 26)) + result;
  }
  return result;
}

function createHarness(names = ['Template'], timezone = 'America/New_York', options = {}) {
  const grids = new Map();
  const formats = new Map();
  const notesBySheet = new Map();
  const insertedCells = [];
  const createdSheets = [];
  const deletedSheets = [];
  const properties = new Map();
  function createSheet(name, grid = Array.from({ length: 16 }, () => ['', ''])) {
    grids.set(name, grid);
    const formatsGrid = [];
    const notes = [];
    formats.set(name, formatsGrid);
    notesBySheet.set(name, notes);
    let maxRows = 774;
    const columnWidths = new Map();
    let conditionalRules = [];
    const sheet = {
      getName: () => name,
      getMaxColumns: () => 33,
      getMaxRows: () => maxRows,
      getColumnWidth: (column) => columnWidths.get(column) || 100,
      setColumnWidth(column, width) { columnWidths.set(column, width); },
      getLastRow: () => grid.length,
      insertRowsAfter(_row, count) { maxRows += count; },
      insertRowsBefore(row, count) {
        maxRows += count;
        this.getRange(row, 1, count, this.getMaxColumns()).insertCells('ROWS');
      },
      getConditionalFormatRules: () => conditionalRules,
      setConditionalFormatRules(rules) { conditionalRules = rules; },
      showSheet() {},
      insertColumnsAfter() {},
      getRange(row, column, rowCount, columnCount) {
        const read = (data, fallback) => Array.from({ length: rowCount }, (_unused, r) =>
          Array.from({ length: columnCount }, (_item, c) => data[row + r - 1]?.[column + c - 1] ?? fallback));
        const write = (data, values) => {
          for (let r = 0; r < values.length; r += 1) {
            data[row + r - 1] ||= [];
            for (let c = 0; c < values[r].length; c += 1) data[row + r - 1][column + c - 1] = values[r][c];
          }
        };
        const style = (field, value) => {
          const cells = read(formatsGrid, null).map((line) => line.map((item) => ({ ...item, [field]: value })));
          write(formatsGrid, cells);
        };
        const styleGrid = (field, values) => {
          assert.equal(values.length, rowCount);
          values.forEach((line) => assert.equal(line.length, columnCount));
          write(formatsGrid, read(formatsGrid, null).map((line, r) =>
            line.map((item, c) => ({ ...item, [field]: values[r][c] }))));
        };
        return {
          getRow: () => row, getColumn: () => column,
          getNumRows: () => rowCount, getNumColumns: () => columnCount,
          getValues: () => read(grid, ''),
          getNotes: () => read(notes, ''),
          isBlank: () => read(grid, '').every((line) => line.every((value) => value === '')),
          getBackground: () => formatsGrid[row - 1]?.[column - 1]?.background || '#ffffff',
          clearNote() { write(notes, read(notes, '').map((line) => line.map(() => ''))); return this; },
          setNote(value) { write(notes, read(notes, '').map((line) => line.map(() => value))); return this; },
          setBackground(value) { style('background', value); return this; },
          setFontColor(value) { style('fontColor', value); return this; },
          setBackgrounds(values) { styleGrid('background', values); return this; },
          setFontColors(values) { styleGrid('fontColor', values); return this; },
          setFontWeight(value) { style('fontWeight', value); return this; },
          setVerticalAlignment(value) { style('verticalAlignment', value); return this; },
          setNumberFormat(value) { style('numberFormat', value); return this; },
          setWrap(value) { style('wrap', value); return this; },
          setBorder(top, left, bottom, right, vertical, horizontal, color, borderStyle) {
            style('borders', { top, left, bottom, right, vertical, horizontal, color, borderStyle }); return this;
          },
          insertCells(dimension) {
            assert.equal(dimension, 'ROWS');
            insertedCells.push({ name, row, column, rowCount, columnCount });
            for (const [data, empty] of [[grid, ''], [notes, ''], [formatsGrid, null]]) {
              for (let r = Math.min(maxRows - 1, data.length + rowCount - 1); r >= row - 1; r -= 1) {
                data[r] ||= [];
                for (let c = column - 1; c < column + columnCount - 1; c += 1) {
                  data[r][c] = r >= row - 1 + rowCount ? data[r - rowCount]?.[c] ?? empty : empty;
                }
              }
            }
            return this;
          },
          getDisplayValues() {
            return Array.from({ length: rowCount }, (_unused, rowOffset) =>
              Array.from({ length: columnCount }, (_item, columnOffset) =>
                grid[row + rowOffset - 1]?.[column + columnOffset - 1] || ''));
          },
          clearContent() {
            if (options.failCopyClear) throw new Error('Copy cleanup failed');
            this.setValues(Array.from({ length: rowCount }, () => Array(columnCount).fill('')));
            return this;
          },
          setValues(values) {
            assert.equal(values.length, rowCount, 'values must match the target rows');
            values.forEach(line => assert.equal(line.length, columnCount, 'values must match the target columns'));
            if (/^\d{1,2}:00 (AM|PM)$/.test(values[0]?.[0])) {
              assert.equal(formatsGrid[row - 1]?.[column - 1]?.numberFormat, '@',
                'format hour labels as text before writing to prevent Sheets time coercion');
            }
            for (let rowOffset = 0; rowOffset < values.length; rowOffset += 1) {
              grid[row + rowOffset - 1] ||= [];
              for (let columnOffset = 0; columnOffset < values[rowOffset].length; columnOffset += 1) {
                grid[row + rowOffset - 1][column + columnOffset - 1] = values[rowOffset][columnOffset];
              }
            }
            return this;
          },
          getCell(r, c) { return sheet.getRange(row + r - 1, column + c - 1, 1, 1); },
          getA1Notation() {
            const endColumn = column + columnCount - 1;
            const endRow = row + rowCount - 1;
            return `${columnLetter(column)}${row}:${columnLetter(endColumn)}${endRow}`;
          }
        };
      }
    };
    return sheet;
  }
  const sheets = names.map((name) => createSheet(name));
  const spreadsheet = {
    getName: () => 'Timer Activities',
    getSheetByName: (name) => sheets.find((sheet) => sheet.getName() === name) || null,
    getSheets: () => sheets,
    getSpreadsheetTimeZone: () => timezone,
    insertSheet(name, { template }) {
      assert.equal(scriptLock.locked, true, 'daily tab creation must be locked');
      assert.equal(grids.has(name), false, 'do not replace an existing daily tab');
      const sheet = createSheet(name, structuredClone(grids.get(template.getName())));
      sheets.push(sheet);
      createdSheets.push(sheet);
      return sheet;
    },
    deleteSheet(sheet) {
      assert.ok(createdSheets.includes(sheet), 'only roll back a newly created copy');
      deletedSheets.push(sheet);
      grids.delete(sheet.getName());
      sheets.splice(sheets.indexOf(sheet), 1);
    }
  };
  const scriptLock = {
    locked: false,
    waitLock() { this.locked = true; },
    hasLock() { return this.locked; },
    releaseLock() { this.locked = false; }
  };
  const context = {
    console: { error() {}, log() {} },
    ContentService: {
      MimeType: { JSON: 'application/json' },
      createTextOutput(text) {
        return { text, setMimeType() { return this; } };
      }
    },
    LockService: { getScriptLock: () => scriptLock },
    PropertiesService: {
      getScriptProperties: () => ({
        getProperty(name) {
          if (name === 'TEMPLATE_SHEET_NAME') return options.templateName || '';
          return name === 'SPREADSHEET_ID' ? SPREADSHEET_ID : name === 'REFLECTION_API_TOKEN' ? TOKEN : properties.get(name) ?? null;
        },
        setProperty(name, value) { if (options.failReceiptDone && JSON.parse(value).status === 'done') throw new Error('Receipt save interrupted'); properties.set(name, value); },
        getProperties() { return Object.fromEntries(properties); },
        deleteProperty(name) { properties.delete(name); }
      })
    },
    SpreadsheetApp: {
      Dimension: { ROWS: 'ROWS' },
      BorderStyle: { SOLID: 'SOLID', SOLID_MEDIUM: 'SOLID_MEDIUM' },
      openById(id) {
        assert.equal(id, SPREADSHEET_ID);
        return spreadsheet;
      },
      flush() {}
    },
    Utilities: {
      formatDate(date, timeZone, format) {
        assert.equal(format, 'yyyy-MM-dd-HH-mm');
        const parts = Object.fromEntries(new Intl.DateTimeFormat('en-US', {
          timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
        }).formatToParts(date).map((part) => [part.type, part.value]));
        return `${parts.year}-${parts.month}-${parts.day}-${parts.hour}-${parts.minute}`;
      },
      Charset: { UTF_8: 'utf8' },
      DigestAlgorithm: { SHA_256: 'sha256' },
      computeDigest(_algorithm, value) {
        return [...crypto.createHash('sha256').update(value, 'utf8').digest()];
      }
    }
  };
  vm.runInNewContext(source, context, { filename: 'google-sheets-script.gs' });
  const request = (payload) => JSON.parse(context.doPost({ postData: { contents: JSON.stringify(payload) } }).text);
  return { grid: grids.get('Template'), grids, formats, notesBySheet, insertedCells, request, scriptLock, createdSheets, deletedSheets, context, sheets, properties };
}

test('Apps Script ping validates configuration and returns the target', () => {
  const harness = createHarness();
  const result = harness.request({
    action: 'ping',
    token: TOKEN,
    sheetUrl: `https://docs.google.com/spreadsheets/d/${SPREADSHEET_ID}/edit`,
    sheetName: 'Template',
    sheetMode: 'fixed'
  });
  assert.equal(result.success, true);
  assert.equal(result.target, 'Timer Activities / Template');
});

test('Apps Script prepends timestamp/activity pairs newest first', () => {
  const harness = createHarness();
  const base = {
    action: 'appendReflection',
    token: TOKEN,
    sheetUrl: `https://docs.google.com/spreadsheets/d/${SPREADSHEET_ID}/edit`,
    sheetName: 'Template',
    sheetMode: 'fixed'
  };
  const first = harness.request({ ...base, message: 'First session' });
  const second = harness.request({ ...base, message: 'Second session' });
  assert.equal(first.range, 'A1:G1');
  assert.equal(second.range, 'A1:G1');
  assert.equal(harness.grid[0][1], 'Second session');
  assert.equal(harness.grid[1][1], 'First session');
});

test('Apps Script grows A:G past 16 entries without using other column pairs', () => {
  const harness = createHarness();
  const base = {
    action: 'appendReflection',
    token: TOKEN,
    sheetUrl: `https://docs.google.com/spreadsheets/d/${SPREADSHEET_ID}/edit`,
    sheetName: 'Template',
    sheetMode: 'fixed'
  };
  let result;
  for (let index = 1; index <= 17; index += 1) {
    result = harness.request({ ...base, message: `Session ${index}` });
  }
  assert.equal(result.range, 'A1:G1');
  assert.equal(harness.grid[0][1], 'Session 17');
  assert.equal(harness.grid[16][1], 'Session 1');
  assert.equal(harness.grid[0][3] || '', '');
});

test('Apps Script rejects an invalid token without writing', () => {
  const harness = createHarness();
  const result = harness.request({
    action: 'appendReflection',
    token: 'wrong-token',
    sheetUrl: `https://docs.google.com/spreadsheets/d/${SPREADSHEET_ID}/edit`,
    sheetName: 'Template',
    sheetMode: 'fixed',
    message: 'Should not be written'
  });
  assert.equal(result.success, false);
  assert.equal(result.error, 'Unauthorized request.');
  assert.deepEqual(harness.grid[0], ['', '']);
});

const datedRequest = {
  action: 'appendReflection', token: TOKEN, sheetUrl: SPREADSHEET_ID,
  message: 'A dated reflection', submittedAt: '2026-09-06T02:30:00.000Z',
  timezoneOffsetMinutes: 240
};

test('stopwatch entries advertise support and store active time without allotted time or early-ending status', () => {
  const h=createHarness(['test']);
  assert.equal(h.request({...datedRequest,action:'ping',isTest:true}).supportsStopwatch,true);
  for(const seconds of [0,5,3607,31536000]){
    const request={...datedRequest,isTest:true,sessionMode:'stopwatch',durationSeconds:null,actualDurationSeconds:seconds,requestId:crypto.randomUUID(),deliveryProtocol:'request-id-v1'};
    assert.equal(h.request(request).success,true);
    assert.deepEqual(h.grids.get('test')[0].slice(2,7),[seconds/86400,'','','','stop watch']);
    assert.equal(h.request(request).success,true);
    assert.equal(h.request({...request,sessionMode:'timer',durationSeconds:31536000}).success,false);
  }
});

test('column G records exact mode labels independently of status in E and reason in F', () => {
  for (const sessionMode of [undefined, 'timer', 'stopwatch']) {
    for (const flags of [{}, { isCheckIn: true }, { autoSent: true }, { isCheckIn: true, autoSent: true }]) {
      const h = createHarness(['test']);
      const p = { ...datedRequest, isTest: true, sessionMode, ...flags,
        durationSeconds: sessionMode === 'stopwatch' ? null : 300, actualDurationSeconds: 12 };
      assert.equal(h.request(p).success, true);
      const row = h.grids.get('test')[0];
      assert.equal(row[4], [flags.isCheckIn ? 'Check-in' : '', flags.autoSent ? 'auto-sent' : ''].filter(Boolean).join(' · '));
      assert.equal(row[6], sessionMode === 'stopwatch' ? 'stop watch' : 'timer');
      assert.equal(row[5], '');
      const styles = h.formats.get('test')[0];
      for (const [column, background, fontColor] of [[5, '#000000', '#ffffff'], [6, '#ffffff', '#000000']]) {
        assert.equal(styles[column].background, background);
        assert.equal(styles[column].fontColor, fontColor);
        assert.equal(styles[column].numberFormat, '@');
        assert.equal(styles[column].wrap, true);
      }
    }
  }
});

test('new schema preserves historical mode/status/reason cells and prior stopwatch receipts', () => {
  const h = createHarness(['test']);
  const p = { ...datedRequest, isTest: true, sessionMode: 'stopwatch', durationSeconds: null,
    actualDurationSeconds: 12, requestId: crypto.randomUUID() };
  const fields = [SPREADSHEET_ID, 'date', '', true, p.submittedAt, p.timezoneOffsetMinutes,
    p.message, null, 12, false, '', 'stopwatch'];
  const fingerprint = crypto.createHash('sha256').update(JSON.stringify(fields)).digest('hex');
  assert.equal(h.context.requestFingerprint_(p), fingerprint);
  h.properties.set('RT_RECEIPT_' + p.requestId, JSON.stringify({ status: 'done', fingerprint,
    sheet: 'test', range: 'A1:F1', timestamp: p.submittedAt }));
  const historical = ['9:30', 'Historical entry', 12 / 86400, '', 'Stopwatch', 'Old column F', 'Keep G'];
  h.grids.get('test')[0] = historical.slice();
  assert.equal(h.request(p).duplicate, true);
  assert.deepEqual(h.grids.get('test')[0], historical);
  assert.equal(h.insertedCells.length, 0);
  assert.equal(h.request({ ...p, requestId: crypto.randomUUID() }).success, true);
  assert.deepEqual(h.grids.get('test')[2].slice(0, 7), historical);
});

test('invalid stopwatch metadata is rejected before any sheet write', () => {
  for(const invalid of [{sessionMode:'unknown'},{actualDurationSeconds:null},{actualDurationSeconds:-1},{actualDurationSeconds:1.5},{durationSeconds:60},{endedEarly:true}]){
    const h=createHarness(['test']);
    assert.equal(h.request({...datedRequest,isTest:true,sessionMode:'stopwatch',durationSeconds:null,actualDurationSeconds:5,...invalid}).success,false);
    assert.deepEqual(h.grids.get('test')[0],['','']);
  }
});

for (const [seconds, pattern] of [
  [0, '[s]" secs"'], [1, '[s]" sec"'], [15, '[s]" secs"'],
  [60, '[m]" min"'], [61, '[m]" min" s" sec"'],
  [2104, '[m]" min" s" secs"'], [3600, '[h]" hr"'],
  [3601, '[h]" hr" s" sec"'], [6620, '[h]" hr" m" min" s" secs"'],
  [7260, '[h]" hrs" m" min"'], [86400, '[h]" hrs"'],
  [31536000, '[h]" hrs"']
]) test(`duration ${seconds} is stored numerically in C with readable units`, () => {
  const harness = createHarness(['test', 'Temp', '09/05/2026']);
  const result = harness.request({ ...datedRequest, isTest: true, durationSeconds: seconds, actualDurationSeconds: seconds });
  assert.equal(result.success, true, result.error);
  assert.equal(result.range, 'A1:G1');
  assert.equal(harness.grids.get('test')[0][2], seconds / 86400);
  const cell = harness.formats.get('test')[0][2];
  assert.equal(cell.numberFormat, pattern);
  assert.equal(cell.fontColor, '#000000');
  assert.equal(cell.background, '#ffffff');
  assert.equal(cell.fontWeight, 'normal');
  assert.equal(cell.verticalAlignment, 'top');
  assert.equal(cell.wrap, true);
  assert.equal(cell.borders.color, '#ffffff');
  assert.equal(harness.grids.get('test')[1][2], '', 'hour dividers have no duration');
  assert.equal(JSON.parse(harness.notesBySheet.get('test')[0][0].slice('Reflection Timer: '.length)).durationSeconds, seconds);
  assert.deepEqual(harness.grids.get('Temp')[0], ['', '']);
  assert.deepEqual(harness.grids.get('09/05/2026')[0], ['', '']);
});

test('safe delivery repeats a completed ID without inserting again', () => {
  const h = createHarness(['test']);
  const p = { ...datedRequest, isTest: true, requestId: crypto.randomUUID(), deliveryProtocol: 'request-id-v1',
    durationSeconds: 60, actualDurationSeconds: 12, endedEarly: true, earlyEndReason: 'appointment' };
  assert.equal(h.request(p).success, true); const before = structuredClone(h.grids.get('test'));
  const retry = h.request(p); assert.equal(retry.success, true); assert.equal(retry.duplicate, true);
  assert.equal(retry.deliveryProtocol, 'request-id-v1'); assert.deepEqual(h.grids.get('test'), before);
  assert.equal(h.request({ ...p, message: 'changed' }).code, 'id_conflict');
  assert.equal(h.request({ ...p, token: 'wrong token' }).success, false);
  assert.deepEqual(h.grids.get('test'), before);
});

test('lost final receipt remains quarantined and cannot duplicate its row', () => {
  const h = createHarness(['test'], 'America/New_York', { failReceiptDone: true });
  const p = { ...datedRequest, isTest: true, requestId: crypto.randomUUID() };
  assert.equal(h.request(p).code, 'write_uncertain'); const before = structuredClone(h.grids.get('test'));
  assert.equal(h.request(p).code, 'write_uncertain'); assert.deepEqual(h.grids.get('test'), before);
  const record = JSON.parse(h.properties.get('RT_RECEIPT_' + p.requestId));
  assert.equal(record.status, 'writing'); assert.equal(record.message, undefined); assert.equal(record.token, undefined);
});

test('row notes recognize an older ID after completed receipt cache eviction', () => {
  const h = createHarness(['test']); const p = { ...datedRequest, isTest: true, requestId: crypto.randomUUID() };
  h.request(p); h.properties.delete('RT_RECEIPT_' + p.requestId);
  h.request({ ...p, requestId: crypto.randomUUID(), message: 'later entry' });
  const before = structuredClone(h.grids.get('test'));
  assert.equal(h.request(p).duplicate, true); assert.deepEqual(h.grids.get('test'), before);
  assert.equal(h.request({ ...p, earlyEndReason: 'changed' }).code, 'id_conflict');
});

test('receipt pruning preserves unresolved reservations and unrelated settings', () => {
  const h = createHarness(['test']);
  for (let i = 0; i < 505; i++) h.properties.set('RT_RECEIPT_' + i, JSON.stringify({ status: 'done', at: i }));
  h.properties.set('RT_RECEIPT_unresolved', JSON.stringify({ status: 'writing', at: -10 }));
  h.properties.set('unrelated-setting', 'keep'); h.context.pruneReceipts_({
    getProperties: () => Object.fromEntries(h.properties), deleteProperty: key => h.properties.delete(key)
  });
  assert.equal(h.properties.size, 501); assert.ok(h.properties.has('RT_RECEIPT_unresolved')); assert.equal(h.properties.get('unrelated-setting'), 'keep');
});

test('safe protocol requires valid IDs and ping advertises support without writing', () => {
  const h = createHarness(['test']);
  const ping = h.request({ ...datedRequest, isTest: true, action: 'ping' });
  assert.equal(ping.deliveryProtocol, 'request-id-v1'); assert.equal(h.properties.size, 0);
  for (const requestId of [null, '', 'short', '../invalid/id/123456789']) {
    assert.equal(h.request({ ...datedRequest, isTest: true, requestId, deliveryProtocol: 'request-id-v1' }).success, false);
  }
  assert.equal(h.properties.size, 0); assert.equal(h.insertedCells.length, 0);
});

test('early-finish details write actual, allotted, status, safe reason and mode to C:G', () => {
  const h = createHarness(['test']);
  const result = h.request({ ...datedRequest, isTest: true, durationSeconds: 1800, actualDurationSeconds: 480,
    endedEarly: true, earlyEndReason: '=appointment' });
  assert.equal(result.success, true, result.error);
  assert.deepEqual(h.grids.get('test')[0].slice(2, 7), [480 / 86400, 1800 / 86400, 'ended early', "'=appointment", 'timer']);
  assert.deepEqual(h.grids.get('test')[1].slice(2, 7), ['', '', '', '', '']);
  assert.equal(h.formats.get('test')[0][5].numberFormat, '@');
});

test('completed entries leave status and reason blank and legacy actual time is unknown', () => {
  const h = createHarness(['test']);
  h.request({ ...datedRequest, isTest: true, durationSeconds: 60, actualDurationSeconds: 60, earlyEndReason: 'not applicable' });
  assert.deepEqual(h.grids.get('test')[0].slice(2, 7), [60 / 86400, 60 / 86400, '', '', 'timer']);
  h.request({ ...datedRequest, isTest: true, durationSeconds: 120 });
  assert.deepEqual(h.grids.get('test')[0].slice(2, 7), ['', 120 / 86400, '', '', 'timer']);
});

test('a grace-period completion retains shorter actual time without an early status or reason', () => {
  const h = createHarness(['test']);
  const result = h.request({ ...datedRequest, isTest: true, durationSeconds: 900, actualDurationSeconds: 885,
    endedEarly: false, earlyEndReason: 'Provisional reason' });
  assert.equal(result.success, true, result.error);
  assert.deepEqual(h.grids.get('test')[0].slice(2, 7), [885 / 86400, 900 / 86400, '', '', 'timer']);
});

test('auto-sent status combines with ended early and retains the response and reason', () => {
  const h=createHarness(['test']);
  const p={...datedRequest,isTest:true,durationSeconds:900,actualDurationSeconds:17,endedEarly:true,earlyEndReason:'Appointment',autoSent:true,message:'[auto-sent]\nFinal response',requestId:crypto.randomUUID(),deliveryProtocol:'request-id-v1'};
  assert.equal(h.request(p).success,true);
  assert.deepEqual(h.grids.get('test')[0].slice(1,7),['Final response',17/86400,900/86400,'ended early · auto-sent','Appointment','timer']);
  const before=structuredClone(h.grids.get('test'));assert.equal(h.request(p).duplicate,true);assert.deepEqual(h.grids.get('test'),before);
});

test('blank and full-length automatic responses are supported without losing text', () => {
  for(const text of ['', 'x'.repeat(5000)]){
    const h=createHarness(['test']);const p={...datedRequest,isTest:true,durationSeconds:900,actualDurationSeconds:900,autoSent:true,message:'[auto-sent]'+(text?'\n'+text:'')};
    assert.equal(h.request(p).success,true);assert.equal(h.grids.get('test')[0][1],text||'N/A');assert.equal(h.grids.get('test')[0][4],'auto-sent');
  }
});

test('blank automatic responses use N/A only in B and preserve other status flags', () => {
  for (const message of ['', ' \n ', '[auto-sent]', ' [auto-sent]\n \t ']) {
    for (const status of [{}, { endedEarly: true, earlyEndReason: 'Appointment' }, { isCheckIn: true }]) {
      const h = createHarness(['test']);
      const p = { ...datedRequest, isTest: true, durationSeconds: 900, actualDurationSeconds: 17,
        autoSent: true, message, ...status, requestId: crypto.randomUUID(), deliveryProtocol: 'request-id-v1' };
      assert.equal(h.request(p).success, true);
      assert.equal(h.grids.get('test')[0][1], 'N/A');
      assert.equal(h.grids.get('test')[0][4], status.endedEarly ? 'ended early · auto-sent' : status.isCheckIn ? 'Check-in · auto-sent' : 'auto-sent');
      assert.equal(h.grids.get('test')[0][5], status.earlyEndReason || '');
      const before = structuredClone(h.grids.get('test'));
      assert.equal(h.request(p).duplicate, true);
      assert.deepEqual(h.grids.get('test'), before);
    }
  }
  const h = createHarness(['test']);
  assert.equal(h.request({ ...datedRequest, isTest: true, message: ' \n ' }).success, false);
  assert.equal(h.insertedCells.length, 0);
});

test('legacy auto-sent markers preserve real text and formula-like responses as plain text', () => {
  for (const message of ['[auto-sent]\nA real response', '[auto-sent]\n=SUM(1,2)', '[auto-sent]']) {
    const h = createHarness(['test']);
    assert.equal(h.request({ ...datedRequest, isTest: true, message }).success, true);
    const response = message.slice('[auto-sent]'.length).trim();
    assert.equal(h.grids.get('test')[0][1], response.startsWith('=') ? "'" + response : response || 'N/A');
    assert.equal(h.grids.get('test')[0][4], 'auto-sent');
  }
});

test('auto-send validation and legacy marker receipt compatibility', () => {
  const h=createHarness(['test']);assert.equal(h.request({...datedRequest,action:'ping',isTest:true}).supportsAutoSent,true);
  assert.equal(h.request({...datedRequest,isTest:true,autoSent:'yes'}).success,false);assert.equal(h.insertedCells.length,0);
  const p={...datedRequest,isTest:true,durationSeconds:60,actualDurationSeconds:60,message:'[auto-sent]\nPreviously sent',requestId:crypto.randomUUID()};
  const fields=[SPREADSHEET_ID,'date','',true,p.submittedAt,p.timezoneOffsetMinutes,p.message,60,60,false,''];
  const fingerprint=crypto.createHash('sha256').update(JSON.stringify(fields)).digest('hex');
  assert.equal(h.context.requestFingerprint_({...p,autoSent:true}),fingerprint);
  h.properties.set('RT_RECEIPT_'+p.requestId,JSON.stringify({status:'done',fingerprint,sheet:'test',timestamp:p.submittedAt}));
  assert.equal(h.request({...p,autoSent:true}).duplicate,true);assert.equal(h.insertedCells.length,0);
});

test('check-ins write elapsed/allotted durations and Check-in status with no early-end reason', () => {
  for (const actualDurationSeconds of [0, 25, 60]) {
    const h = createHarness(['test', 'Temp', '09/05/2026']);
    const p = { ...datedRequest, isTest: true, isCheckIn: true, durationSeconds: 60,
      actualDurationSeconds, earlyEndReason: 'not applicable', requestId: crypto.randomUUID(), deliveryProtocol: 'request-id-v1' };
    const result = h.request(p); assert.equal(result.success, true, result.error);
    assert.deepEqual(h.grids.get('test')[0].slice(2, 7), [actualDurationSeconds / 86400, 60 / 86400, 'Check-in', '', 'timer']);
    assert.equal(JSON.parse(h.notesBySheet.get('test')[0][0].slice('Reflection Timer: '.length)).isCheckIn, true);
    assert.equal(h.formats.get('test')[0][2].background, '#ffffff');
    assert.equal(h.formats.get('test')[0][3].background, '#000000');
    assert.equal(h.formats.get('test')[0][4].background, '#ffffff');
    const before = structuredClone(h.grids.get('test'));
    assert.equal(h.request(p).duplicate, true); assert.deepEqual(h.grids.get('test'), before);
    assert.equal(h.request({ ...p, isCheckIn: false }).code, 'id_conflict');
    assert.deepEqual(h.grids.get('Temp')[0], ['', '']); assert.deepEqual(h.grids.get('09/05/2026')[0], ['', '']);
  }
});

test('check-in capability is explicit and malformed or contradictory check-ins never write', () => {
  const h = createHarness(['test']);
  assert.equal(h.request({ ...datedRequest, isTest: true, action: 'ping' }).supportsCheckIns, true);
  for (const bad of [{ isCheckIn: 'true', actualDurationSeconds: 5 }, { isCheckIn: true },
    { isCheckIn: true, actualDurationSeconds: 5, endedEarly: true }]) {
    assert.equal(h.request({ ...datedRequest, isTest: true, durationSeconds: 60, ...bad }).success, false);
  }
  assert.equal(h.insertedCells.length, 0); assert.equal(h.properties.size, 0);
});

test('new receiver preserves legacy fingerprints and recognizes old receipts', () => {
  const h = createHarness(['test']);
  const p = { ...datedRequest, isTest: true, durationSeconds: 60, actualDurationSeconds: 60, requestId: crypto.randomUUID() };
  const legacyFields = [SPREADSHEET_ID, 'date', '', true, p.submittedAt, p.timezoneOffsetMinutes,
    p.message, 60, 60, false, ''];
  const fingerprint = crypto.createHash('sha256').update(JSON.stringify(legacyFields)).digest('hex');
  assert.equal(h.context.requestFingerprint_(p), fingerprint);
  assert.equal(h.context.requestFingerprint_({ ...p, isCheckIn: false }), fingerprint);
  h.properties.set('RT_RECEIPT_' + p.requestId, JSON.stringify({ status: 'done', fingerprint, sheet: 'test', timestamp: p.submittedAt }));
  assert.equal(h.request({ ...p, isCheckIn: false }).duplicate, true);
  assert.equal(h.insertedCells.length, 0);
});

test('invalid session details fail before sheet mutations', () => {
  for (const details of [{ actualDurationSeconds: 61 }, { actualDurationSeconds: -1 }, { actualDurationSeconds: '3' },
    { endedEarly: 'yes' }, { endedEarly: true }, { earlyEndReason: 3 }, { earlyEndReason: 'x'.repeat(1001) }]) {
    const h = createHarness();
    assert.equal(h.request({ ...datedRequest, durationSeconds: 60, ...details }).success, false);
    assert.equal(h.createdSheets.length, 0); assert.equal(h.insertedCells.length, 0);
  }
});

test('zero actual time and optional early reason remain meaningful', () => {
  const h = createHarness(['test']);
  assert.equal(h.request({ ...datedRequest, isTest: true, durationSeconds: 60, actualDurationSeconds: 0, endedEarly: true }).success, true);
  assert.deepEqual(h.grids.get('test')[0].slice(2, 7), [0, 60 / 86400, 'ended early', '', 'timer']);
});

test('legacy missing durations stay blank instead of inventing a duration', () => {
  for (const durationSeconds of [undefined, null]) {
    const harness = createHarness(['test']);
    assert.equal(harness.request({ ...datedRequest, isTest: true, durationSeconds }).success, true);
    assert.equal(harness.grids.get('test')[0][2], '');
  }
});

test('invalid durations are rejected before creating a tab or changing any cells', () => {
  for (const durationSeconds of [-1, 1.5, '15', '', false, {}, 31536001, Number.MAX_SAFE_INTEGER]) {
    const harness = createHarness();
    const original = structuredClone(harness.grid);
    const result = harness.request({ ...datedRequest, durationSeconds });
    assert.equal(result.success, false);
    assert.match(result.error, /timer duration must be whole seconds/);
    assert.deepEqual(harness.grid, original);
    assert.equal(harness.createdSheets.length, 0);
    assert.equal(harness.insertedCells.length, 0);
  }
});

test('durations move with their reflections and only C:G is widened when needed', () => {
  const harness = createHarness(['test']);
  const sheet = harness.sheets[0];
  sheet.setColumnWidth(2, 450); sheet.setColumnWidth(8, 105);
  for (const [minute, durationSeconds] of [[1, 15], [2, 2104], [3, 6620]]) {
    assert.equal(harness.request({ ...datedRequest, isTest: true, durationSeconds, actualDurationSeconds: durationSeconds,
      submittedAt: `2026-09-05T18:0${minute}:00-04:00` }).success, true);
  }
  assert.deepEqual(harness.grids.get('test').slice(0, 4).map(row => row[2]), [6620 / 86400, 2104 / 86400, 15 / 86400, '']);
  assert.equal(sheet.getColumnWidth(3), 220);
  assert.equal(sheet.getColumnWidth(2), 450); assert.equal(sheet.getColumnWidth(8), 105);
  assert.equal(sheet.getColumnWidth(6), 300); assert.equal(sheet.getColumnWidth(7), 135);
  sheet.setColumnWidth(3, 300);
  assert.equal(harness.request({ ...datedRequest, isTest: true, durationSeconds: 60 }).success, true);
  assert.equal(sheet.getColumnWidth(3), 300, 'retain a user-chosen wider duration column');
});

test('a new daily tab receives the timer duration without changing its source template', () => {
  const harness = createHarness(['Temp'], 'America/New_York', { templateName: 'Temp' });
  const original = structuredClone(harness.grids.get('Temp'));
  const result = harness.request({ ...datedRequest, durationSeconds: 6620, actualDurationSeconds: 6620 });
  assert.equal(result.success, true, result.error); assert.equal(result.created, true);
  assert.equal(harness.grids.get(result.sheet)[0][2], 6620 / 86400);
  assert.equal(harness.grids.get(result.sheet)[1][2], '');
  assert.deepEqual(harness.grids.get('Temp'), original);
});

test('hour markers follow the example and entry colors alternate across the divider', () => {
  const harness = createHarness(['test']);
  for (const time of ['17:54', '17:55', '17:57', '17:58', '18:21']) {
    const result = harness.request({ ...datedRequest, isTest: true,
      submittedAt: `2026-09-05T${time}:00-04:00`, message: `Test ${time}` });
    assert.equal(result.success, true, result.error);
  }
  const grid = harness.grids.get('test');
  const format = harness.formats.get('test');
  assert.equal(grid[0][1], 'Test 18:21');
  assert.equal(grid[0][0], '6:21');
  assert.equal(grid[1][0], '6:00 PM');
  assert.equal(grid[2][1], 'Test 17:58');
  assert.equal(grid[3][1], 'Test 17:57');
  assert.equal(grid[4][1], 'Test 17:55');
  assert.equal(format[0][0].background, '#ffffff');
  assert.equal(format[0][0].fontColor, '#000000');
  assert.equal(format[2][0].background, '#595959');
  assert.equal(format[2][0].fontColor, '#ffffff');
  assert.equal(format[3][0].background, '#ffffff');
  assert.equal(format[4][0].background, '#595959');
  assert.equal(format[1][0].background, '#980000');
  assert.equal(format[1][1].background, '#980000');
  assert.deepEqual(format[1][0].borders, { top: true, bottom: true,
    left: false, right: false, vertical: false, horizontal: false,
    color: '#ff4d4d', borderStyle: 'SOLID_MEDIUM' });
});

test('reused and inserted entry rows have thin white borders in every column without changing hour dividers', () => {
  const harness = createHarness(['test']);
  for (const time of ['17:58', '17:59', '18:01', '18:02']) {
    const result = harness.request({ ...datedRequest, isTest: true,
      submittedAt: `2026-09-05T${time}:00-04:00` });
    assert.equal(result.success, true, result.error);
    const format = harness.formats.get('test');
    assert.equal(format[0].length, 33);
    for (const cell of format[0]) {
      assert.deepEqual(cell.borders, { top: true, left: true, bottom: true,
        right: true, vertical: true, horizontal: false, color: '#ffffff', borderStyle: 'SOLID' });
    }
    const notes = harness.notesBySheet.get('test');
    for (let row = 0; row < notes.length; row += 1) {
      if (!notes[row]?.[0]?.includes('"kind":"hour"')) continue;
      assert.equal(format[row].length, 33);
      for (const cell of format[row]) {
        assert.equal(cell.borders.top, true);
        assert.equal(cell.borders.bottom, true);
        assert.equal(cell.borders.left, false);
        assert.equal(cell.borders.right, false);
        assert.equal(cell.borders.vertical, false);
        assert.notEqual(cell.borders.color, '#ffffff');
        assert.equal(cell.borders.borderStyle, 'SOLID_MEDIUM');
      }
    }
  }
});

test('full-row column stripes preserve the other columns font weight and notes', () => {
  const harness = createHarness(['test']);
  harness.sheets[0].getRange(1, 8, 1, 26).setBackground('#abcdef')
    .setFontColor('#123456').setFontWeight('bold').setNote('Keep my note');
  assert.equal(harness.request({ ...datedRequest, isTest: true }).success, true);
  const format = harness.formats.get('test')[0];
  const notes = harness.notesBySheet.get('test')[0];
  for (let column = 7; column < 33; column += 1) {
    assert.equal(format[column].background, column % 2 === 1 ? '#000000' : '#ffffff');
    assert.equal(format[column].fontColor, column % 2 === 1 ? '#ffffff' : '#000000');
    assert.equal(format[column].fontWeight, 'bold');
    assert.equal(format[column].borders.color, '#ffffff');
    assert.equal(notes[column], 'Keep my note');
  }
});

test('column stripes cover reused and inserted rows, with or without a new hour and duration metadata', () => {
  const harness = createHarness(['test']);
  // Simulate the color a blank row can inherit from an hour band.
  harness.sheets[0].getRange(1, 1, 1, 33).setBackground('#980000').setFontColor('#ffffff');
  for (const [index, time] of ['17:58', '17:59', '18:01', '18:02'].entries()) {
    const result = harness.request({ ...datedRequest, isTest: true,
      submittedAt: `2026-09-05T${time}:00-04:00`,
      ...(index % 2 === 0 ? { durationSeconds: 120, actualDurationSeconds: 30,
        endedEarly: true, earlyEndReason: 'Test interruption' } : {}) });
    assert.equal(result.success, true, result.error);
    const format = harness.formats.get('test');
    for (let column = 1; column < 33; column++) {
      assert.equal(format[0][column].background, column % 2 === 1 ? '#000000' : '#ffffff');
      assert.equal(format[0][column].fontColor, column % 2 === 1 ? '#ffffff' : '#000000');
    }
    assert.equal(format[0][0].background, index % 2 === 0 ? '#ffffff' : '#595959');
    if (index % 2 === 0) {
      assert.ok(format[1].every((cell) => cell.background === (index === 0 ? '#1e40af' : '#980000')));
    }
  }
});

test('column stripes on a newly copied daily tab do not repaint the template', () => {
  const harness = createHarness(['Temp'], 'America/New_York', { templateName: 'Temp' });
  harness.sheets[0].getRange(1, 1, 1, 33).setBackground('#abcdef').setFontColor('#123456');
  const original = structuredClone(harness.formats.get('Temp'));
  const result = harness.request({ ...datedRequest, durationSeconds: 120, actualDurationSeconds: 120 });
  assert.equal(result.success, true, result.error);
  for (let column = 1; column < 33; column++) {
    assert.equal(harness.formats.get(result.sheet)[0][column].background, column % 2 === 1 ? '#000000' : '#ffffff');
  }
  assert.deepEqual(harness.formats.get('Temp'), original);
});

test('empty top pairs are reused, while partial pairs and empty-result formulas are preserved', () => {
  const empty = createHarness(['test']);
  empty.request({ ...datedRequest, isTest: true });
  assert.equal(empty.insertedCells.length, 0);
  for (const pair of [['existing time', ''], ['', 'existing note'], ['=""', '']]) {
    const harness = createHarness(['test']);
    harness.grids.get('test')[0] = [...pair, 'C moves with its row', 'D moves with its row'];
    assert.equal(harness.request({ ...datedRequest, isTest: true }).success, true);
    assert.deepEqual(harness.grids.get('test')[2].slice(0, 2), pair);
    assert.deepEqual(harness.grids.get('test')[2].slice(2, 4), ['C moves with its row', 'D moves with its row']);
    assert.equal(harness.insertedCells[0].columnCount, 33);
  }
});

test('whole-row insertion keeps full-width hour themes, notes and neighboring contents aligned', () => {
  const harness = createHarness(['test']);
  const sheet = harness.sheets[0];
  const grid = harness.grids.get('test');
  grid[0] = ['', '', 'Keep C', '=SUM(1,2)'];
  sheet.getRange(1, 3, 1, 1).setNote('Keep this note').setBackground('#abcdef');
  for (const time of ['17:58', '18:01', '18:02']) {
    assert.equal(harness.request({ ...datedRequest, isTest: true,
      submittedAt: `2026-09-05T${time}:00-04:00` }).success, true);
  }
  const format = harness.formats.get('test');
  const notes = harness.notesBySheet.get('test');
  assert.deepEqual(grid[5].slice(0, 4), ['', '', 'Keep C', '=SUM(1,2)']);
  assert.equal(notes[5][2], 'Keep this note');
  assert.equal(format[5][2].background, '#abcdef');
  for (const [row, background] of [[2, '#980000'], [4, '#1e40af']]) {
    assert.equal(format[row].length, 33);
    assert.ok(format[row].every((cell) => cell.background === background));
    assert.match(notes[row][0], /"kind":"hour"/);
  }
});

test('new hours get different readable themes, including midnight and noon', () => {
  const harness = createHarness(['test']);
  const themes = new Set();
  for (let hour = 0; hour < 24; hour += 1) {
    harness.request({ ...datedRequest, isTest: true,
      submittedAt: `2026-09-05T${String(hour).padStart(2, '0')}:21:00-04:00` });
    const grid = harness.grids.get('test');
    const format = harness.formats.get('test')[1][0];
    assert.equal(grid[1][0], `${hour % 12 || 12}:00 ${hour < 12 ? 'AM' : 'PM'}`);
    assert.equal(format.fontColor, harness.context.textColor_(format.background));
    assert.notEqual(format.background, format.borders.color);
    themes.add(format.background);
  }
  assert.equal(themes.size, 24);
});

test('same-hour entries do not duplicate the divider and time gaps add only the current hour', () => {
  const harness = createHarness(['test']);
  for (const time of ['12:01', '12:59', '15:04']) {
    harness.request({ ...datedRequest, isTest: true, submittedAt: `2026-09-05T${time}:00-04:00` });
  }
  const markers = harness.grids.get('test').filter((row) => /AM|PM/.test(row[0]));
  assert.deepEqual(markers.map((row) => row[0]), ['3:00 PM', '12:00 PM']);
});

test('test writes always use test and never create or write a template or daily tab', () => {
  const harness = createHarness(['test', 'Temp', 'Template', '09/05/2026', 'Custom']);
  const result = harness.request({ ...datedRequest, isTest: true, sheetMode: 'fixed', sheetName: 'Custom' });
  assert.equal(result.sheet, 'test');
  assert.deepEqual(harness.grids.get('Custom')[0], ['', '']);
  assert.deepEqual(harness.grids.get('09/05/2026')[0], ['', '']);
  assert.deepEqual(harness.grids.get('Temp')[0], ['', '']);
  assert.deepEqual(harness.grids.get('Template')[0], ['', '']);
  assert.equal(harness.createdSheets.length, 0);
  const missingTest = createHarness(['Temp', 'Template', '09/05/2026']);
  const failure = missingTest.request({ ...datedRequest, isTest: true });
  assert.equal(failure.success, false);
  assert.match(failure.error, /test tab "test" is missing/);
  assert.deepEqual(missingTest.grids.get('09/05/2026')[0], ['', '']);
});

test('newly written cells are excluded from old conditional rules without changing neighbors', () => {
  const harness = createHarness(['test']);
  const sheet = harness.sheets[0];
  const makeRule = (ranges) => ({ getRanges: () => ranges,
    copy() { return { setRanges(next) { return { build: () => makeRule(next) }; } }; } });
  sheet.setConditionalFormatRules([makeRule([sheet.getRange(1, 1, 774, 33)])]);
  harness.request({ ...datedRequest, isTest: true });
  const ranges = sheet.getConditionalFormatRules()[0].getRanges();
  assert.deepEqual(Array.from(ranges, (r) => r.getA1Notation()), ['A3:AG774']);
});

test('same-hour entries exclude only the new full-width row from conditional colors', () => {
  const harness = createHarness(['test']);
  harness.request({ ...datedRequest, isTest: true });
  const sheet = harness.sheets[0];
  const makeRule = (ranges) => ({ getRanges: () => ranges,
    copy() { return { setRanges(next) { return { build: () => makeRule(next) }; } }; } });
  sheet.setConditionalFormatRules([makeRule([sheet.getRange(1, 1, 774, 33)])]);
  assert.equal(harness.request({ ...datedRequest, isTest: true }).success, true);
  const ranges = sheet.getConditionalFormatRules()[0].getRanges();
  assert.deepEqual(Array.from(ranges, (r) => r.getA1Notation()), ['A2:AG774']);
});

test('reflections beginning with equals are stored as literal text', () => {
  const harness = createHarness(['test']);
  harness.request({ ...datedRequest, isTest: true, message: '=1+1' });
  assert.equal(harness.grids.get('test')[0][1], "'=1+1");
  assert.equal(harness.formats.get('test')[0][1].numberFormat, '@');
});

test('default routing accepts two/four digit years and optional leading zeros', () => {
  for (const name of ['09/05/2026', '09/05/26', '9/5/26', '9/05/2026', ' 9/5/2026 ']) {
    const harness = createHarness(['Template', name]);
    const result = harness.request({ ...datedRequest, sheetName: 'Template' });
    assert.equal(result.success, true, name);
    assert.equal(result.sheet, name);
    assert.equal(harness.grids.get(name)[0][1], datedRequest.message);
    assert.deepEqual(harness.grid[0], ['', '']);
    assert.equal(harness.scriptLock.locked, false);
  }
});

test('explicit date mode ignores a stale fixed target', () => {
  const harness = createHarness(['Template', '09/05/2026']);
  const result = harness.request({ ...datedRequest, sheetMode: 'date', sheetName: 'Template' });
  assert.equal(result.sheet, '09/05/2026');
});

test('the send date uses the browser local zone, not the UTC date or completion date', () => {
  const harness = createHarness(['09/05/2026', '09/06/2026', '09/07/2026']);
  const beforeMidnight = harness.request({ ...datedRequest, completedAt: '2026-09-05T00:00:00Z' });
  assert.equal(beforeMidnight.sheet, '09/05/2026');
  const afterMidnight = harness.request({ ...datedRequest, submittedAt: '2026-09-06T04:01:00Z' });
  assert.equal(afterMidnight.sheet, '09/06/2026');
  const eastOfUtc = harness.request({ ...datedRequest, submittedAt: '2026-09-06T19:00:00Z', timezoneOffsetMinutes: -330 });
  assert.equal(eastOfUtc.sheet, '09/07/2026');
});

test('date routing crosses New Year correctly and matches two-digit years', () => {
  const harness = createHarness(['12/31/26', '01/01/2027']);
  const result = harness.request({ ...datedRequest, submittedAt: '2027-01-01T02:30:00Z' });
  assert.equal(result.sheet, '12/31/26');
});

test('four-digit year then padded naming takes priority, regardless of tab order', () => {
  const harness = createHarness(['09/05/26', '9/5/2026', '09/05/2026']);
  const result = harness.request(datedRequest);
  assert.equal(result.sheet, '09/05/2026');
});

test('missing date and template tabs never fall back to another year or a yearless tab', () => {
  const harness = createHarness(['09/05/2025', '09/05', '05/09/2026']);
  const result = harness.request(datedRequest);
  assert.equal(result.success, false);
  assert.match(result.error, /No dated tab matches 09\/05\/2026/);
  assert.match(result.error, /template tab "Template" is missing/);
  assert.match(result.error, /Nothing was saved/);
  for (const grid of harness.grids.values()) assert.deepEqual(grid[0], ['', '']);
  assert.equal(harness.scriptLock.locked, false);
});

test('the first send copies Template once and clears only the copy log area', () => {
  const harness = createHarness();
  harness.grid[0] = ['old timestamp', 'old reflection'];
  harness.grid[16] = ['old log', 'old test', '=SUM(C1:C16)', 'keep outside log area'];
  harness.grid[0][32] = 'keep unpaired column';
  const original = structuredClone(harness.grid);
  const first = harness.request(datedRequest);
  assert.equal(first.success, true);
  assert.equal(first.sheet, '09/05/2026');
  assert.equal(first.created, true);
  assert.equal(first.range, 'A1:G1');
  const dailyGrid = harness.grids.get(first.sheet);
  assert.equal(dailyGrid[0][1], datedRequest.message);
  assert.deepEqual(dailyGrid[18].slice(0, 4), ['', '', ...original[16].slice(2)]);
  assert.equal(dailyGrid[2][32], 'keep unpaired column');
  assert.deepEqual(harness.grid, original, 'source template is never cleared');
  const second = harness.request({ ...datedRequest, message: 'Next session' });
  assert.equal(second.created, false);
  assert.equal(second.range, 'A1:G1');
  assert.equal(harness.createdSheets.length, 1);
});

test('ping previews creation without creating a tab or clearing the template', () => {
  const harness = createHarness(['Temp'], 'America/New_York', { templateName: 'Temp' });
  const result = harness.request({ ...datedRequest, action: 'ping' });
  assert.equal(result.success, true);
  assert.equal(result.target, 'Timer Activities / 09/05/2026');
  assert.equal(result.willCreate, true);
  assert.equal(result.template, 'Temp');
  assert.equal(harness.createdSheets.length, 0);
  assert.equal(harness.grids.size, 1);
});

test('new tabs support a configured template name, but existing dated tabs need no template', () => {
  const harness = createHarness(['Temp'], 'America/New_York', { templateName: 'Temp' });
  assert.equal(harness.request(datedRequest).sheet, '09/05/2026');
  const existingOnly = createHarness(['9/5/26']);
  assert.equal(existingOnly.request(datedRequest).sheet, '9/5/26');
});

test('bad messages cannot create a daily tab', () => {
  for (const message of ['', 'x'.repeat(5001)]) {
    const harness = createHarness();
    assert.equal(harness.request({ ...datedRequest, message }).success, false);
    assert.equal(harness.createdSheets.length, 0);
  }
});

test('a failed new-tab initialization rolls back only the copy', () => {
  const harness = createHarness(['Template'], 'America/New_York', { failCopyClear: true });
  harness.grid[0] = ['old timestamp', 'old reflection'];
  const result = harness.request(datedRequest);
  assert.equal(result.success, false);
  assert.equal(result.error, 'Copy cleanup failed');
  assert.equal(harness.deletedSheets.length, 1);
  assert.deepEqual(harness.grid[0], ['old timestamp', 'old reflection']);
  assert.equal(harness.grids.has('09/05/2026'), false);
  assert.equal(harness.scriptLock.locked, false);
});

test('dated connection tests select the target without writing', () => {
  const harness = createHarness(['09/05/26']);
  const result = harness.request({ ...datedRequest, action: 'ping' });
  assert.equal(result.target, 'Timer Activities / 09/05/26');
  assert.deepEqual(harness.grids.get('09/05/26')[0], ['', '']);
});

test('clients without a time-zone offset use the spreadsheet time zone', () => {
  const harness = createHarness(['09/05/26'], 'America/Los_Angeles');
  const result = harness.request({ ...datedRequest, timezoneOffsetMinutes: undefined });
  assert.equal(result.sheet, '09/05/26');
});

test('invalid date, time zone, mode, token, and spreadsheet are rejected without writes', () => {
  for (const invalid of [
    { submittedAt: 'not-a-date' }, { submittedAt: null },
    { timezoneOffsetMinutes: '240' }, { timezoneOffsetMinutes: 841 },
    { timezoneOffsetMinutes: 1.5 }, { sheetMode: 'typo' },
    { token: 'wrong' }, { sheetUrl: 'other-spreadsheet-id-is-not-allowed' }
  ]) {
    const harness = createHarness(['09/05/2026']);
    const result = harness.request({ ...datedRequest, ...invalid });
    assert.equal(result.success, false, JSON.stringify(invalid));
    assert.deepEqual(harness.grids.get('09/05/2026')[0], ['', '']);
  }
});
