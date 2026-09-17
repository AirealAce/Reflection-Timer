/**
 * Reflection Timer receiver.
 *
 * Required Script Properties:
 *   SPREADSHEET_ID       The one spreadsheet this deployment may update.
 *   REFLECTION_API_TOKEN A long random token shared with the extension.
 * Optional Script Property:
 *   TEMPLATE_SHEET_NAME  Source for new daily tabs (defaults to Template).
 *
 * Deploy as a web app that executes as you. The extension posts text/plain so
 * the request works cleanly from a Manifest V3 service worker.
 */

const APP_VERSION = '2.8.1';
const DELIVERY_PROTOCOL = 'request-id-v1';
const RECEIPT_PREFIX = 'RT_RECEIPT_';
const ROWS_PER_BLOCK = 16;
const MAX_COLUMN_PAIRS = 100;
const MAX_REFLECTION_LENGTH = 5000;
const MAX_DURATION_SECONDS = 365 * 24 * 60 * 60;
const NOTE_PREFIX = 'Reflection Timer: ';
const HOUR_THEMES = [
  ['#312e81', '#818cf8'], ['#3730a3', '#a5b4fc'], ['#4c1d95', '#a78bfa'],
  ['#581c87', '#c084fc'], ['#701a75', '#e879f9'], ['#831843', '#f472b6'],
  ['#9a3412', '#fb923c'], ['#c2410c', '#fdba74'], ['#92400e', '#fbbf24'],
  ['#a16207', '#fde047'], ['#facc15', '#a16207'], ['#d9f99d', '#65a30d'],
  ['#166534', '#4ade80'], ['#065f46', '#34d399'], ['#115e59', '#2dd4bf'],
  ['#155e75', '#22d3ee'], ['#075985', '#38bdf8'], ['#1e40af', '#60a5fa'],
  ['#980000', '#ff4d4d'], ['#9f1239', '#fb7185'], ['#9d174d', '#f9a8d4'],
  ['#86198f', '#f0abfc'], ['#6b21a8', '#d8b4fe'], ['#4338ca', '#c7d2fe']
];

// Called ONLY by the private setupReflectionTimer wrapper generated on the
// user's PC and run in their own bound Apps Script editor. Never exposed by HTTP.
function initializeReflectionTimer_(spreadsheetId, apiToken) {
  if (!/^[A-Za-z0-9_-]{20,}$/.test(spreadsheetId) || !/^[a-f0-9]{64}$/.test(apiToken)) {
    throw new Error('Copy a fresh setup script from Reflection Timer Guided setup.');
  }
  const spreadsheet = SpreadsheetApp.getActiveSpreadsheet();
  if (!spreadsheet || spreadsheet.getId() !== spreadsheetId) {
    throw new Error('Open Extensions > Apps Script from the same spreadsheet selected in Guided setup. No configuration was changed.');
  }
  const lock = LockService.getScriptLock();
  lock.waitLock(15000);
  try {
    const props = PropertiesService.getScriptProperties();
    const oldId = props.getProperty('SPREADSHEET_ID');
    const oldToken = props.getProperty('REFLECTION_API_TOKEN');
    if ((oldId && oldId !== spreadsheetId) || (oldToken && oldToken !== apiToken)) {
      throw new Error('This script already has a different connection. Use existing connection setup; its credentials and data were not replaced.');
    }
    const templateName = props.getProperty('TEMPLATE_SHEET_NAME')
      || (spreadsheet.getSheetByName('Temp') ? 'Temp' : spreadsheet.getSheetByName('Template') ? 'Template' : 'Temp');
    for (const name of [templateName, 'test']) {
      if (spreadsheet.getSheetByName(name)) continue; // Never clear or restyle an existing tab.
      const sheet = spreadsheet.insertSheet(name);
      ensureColumns_(sheet, 6);
      const rows = Math.min(ROWS_PER_BLOCK, sheet.getMaxRows());
      sheet.getRange(1, 1, rows, 6)
        .setBackgrounds(Array.from({ length: rows }, () => ['#ffffff', '#000000', '#ffffff', '#000000', '#ffffff', '#000000']))
        .setFontColors(Array.from({ length: rows }, () => ['#000000', '#ffffff', '#000000', '#ffffff', '#000000', '#ffffff']))
        .setBorder(true, true, true, true, true, true, '#ffffff', SpreadsheetApp.BorderStyle.SOLID)
        .setWrap(true);
      [95, 440, 220, 220, 135, 300].forEach((width, column) => sheet.setColumnWidth(column + 1, width));
    }
    props.setProperty('SPREADSHEET_ID', spreadsheetId);
    props.setProperty('REFLECTION_API_TOKEN', apiToken);
    props.setProperty('TEMPLATE_SHEET_NAME', templateName);
    SpreadsheetApp.flush();
    console.log('Reflection Timer setup complete. Deploy as a web app, then paste its /exec URL into Guided setup.');
    return { success: true, template: templateName }; // Never print the private token.
  } finally { lock.releaseLock(); }
}

function doGet() {
  return jsonOutput_({
    success: true,
    service: 'Reflection Timer',
    version: APP_VERSION
  });
}

function doPost(event) {
  let lock;
  let writeStarted = false;
  try {
    if (!event || !event.postData || !event.postData.contents) {
      throw new Error('The request body is empty.');
    }

    const payload = JSON.parse(event.postData.contents);
    const config = getConfig_();
    requireValidToken_(payload.token, config.apiToken);

    const requestedSpreadsheetId = extractSpreadsheetId_(payload.sheetUrl);
    if (!requestedSpreadsheetId || requestedSpreadsheetId !== config.spreadsheetId) {
      throw new Error('The requested spreadsheet does not match SPREADSHEET_ID.');
    }

    const spreadsheet = SpreadsheetApp.openById(config.spreadsheetId);
    if (payload.action === 'ping') {
      const target = resolveTargetSheet_(spreadsheet, payload);
      return jsonOutput_({
        success: true,
        version: APP_VERSION,
        deliveryProtocol: DELIVERY_PROTOCOL,
        supportsCheckIns: true,
        supportsAutoSent: true,
        target: `${spreadsheet.getName()} / ${target.sheet ? target.sheet.getName() : target.name}`,
        willCreate: !target.sheet,
        template: target.templateName || null
      });
    }

    if (payload.action !== 'appendReflection') {
      throw new Error('Unsupported action.');
    }

    let message = String(payload.message || '').trim();
    const autoSentMarker = message === '[auto-sent]' || message.startsWith('[auto-sent]\n');
    if (autoSentMarker) message = message.slice('[auto-sent]'.length).trim();
    if (!message && !autoSentMarker && payload.autoSent !== true) {
      throw new Error('The reflection is empty.');
    }
    if (message.length > MAX_REFLECTION_LENGTH) {
      throw new Error(`The reflection exceeds ${MAX_REFLECTION_LENGTH} characters.`);
    }

    const durationSeconds = validateDuration_(payload.durationSeconds);
    const session = validateSession_(payload, durationSeconds);
    const requestId = validateRequestId_(payload.requestId, payload.deliveryProtocol);
    const fingerprint = requestId ? requestFingerprint_(payload) : '';
    lock = LockService.getScriptLock();
    lock.waitLock(15000);
    const properties = PropertiesService.getScriptProperties();
    const receiptKey = RECEIPT_PREFIX + requestId;
    if (requestId) {
      const saved = properties.getProperty(receiptKey);
      if (saved) return jsonOutput_(receiptReply_(JSON.parse(saved), fingerprint));
    }
    const target = resolveTargetSheet_(spreadsheet, payload);
    if (requestId && target.sheet) {
      const existing = findReceiptInSheet_(target.sheet, requestId);
      if (existing) return jsonOutput_(receiptReply_(existing, fingerprint));
    }
    // Reserve the ID before any sheet mutation. An interrupted partial write is
    // quarantined, never retried as a new insertion. No reflection text/token is stored here.
    if (requestId) {
      pruneReceipts_(properties);
      properties.setProperty(receiptKey, JSON.stringify({ status: 'writing', fingerprint, at: Date.now() }));
      writeStarted = true;
    }
    const sheet = target.sheet || createDailySheet_(spreadsheet, target);
    const result = appendReflection_(sheet, message, submissionDate_(spreadsheet, payload), spreadsheet, durationSeconds,
      { ...session, requestId, fingerprint });
    SpreadsheetApp.flush();
    if (requestId) properties.setProperty(receiptKey, JSON.stringify({ status: 'done', fingerprint,
      sheet: sheet.getName(), timestamp: result.timestamp.toISOString(), at: Date.now() }));

    return jsonOutput_({
      success: true,
      version: APP_VERSION,
      deliveryProtocol: DELIVERY_PROTOCOL,
      sheet: sheet.getName(),
      created: !target.sheet,
      range: result.range,
      timestamp: result.timestamp.toISOString()
    });
  } catch (error) {
    console.error(error);
    return jsonOutput_({
      success: false,
      code: writeStarted ? 'write_uncertain' : 'rejected',
      error: error && error.message ? error.message : String(error)
    });
  } finally {
    if (lock && lock.hasLock()) {
      lock.releaseLock();
    }
  }
}

function validateRequestId_(id, protocol) {
  if (protocol && protocol !== DELIVERY_PROTOCOL) throw new Error('Unsupported delivery protocol.');
  if (id === undefined || id === null || id === '') {
    if (protocol) throw new Error('Safe delivery requires an entry ID.');
    return '';
  }
  if (typeof id !== 'string' || !/^[a-zA-Z0-9_-]{16,100}$/.test(id)) throw new Error('Invalid entry ID.');
  return id;
}

function requestFingerprint_(p) {
  // An ID can never be reused for changed text, timing, reason or destination.
  const fields = [extractSpreadsheetId_(p.sheetUrl), p.sheetMode || 'date', p.sheetName || '', p.isTest === true,
    p.submittedAt || '', p.timezoneOffsetMinutes ?? null, String(p.message || '').trim(), p.durationSeconds ?? null,
    p.actualDurationSeconds ?? null, p.endedEarly === true, String(p.earlyEndReason || '').trim()];
  // Preserve fingerprints for pre-upgrade receipts, including explicit false.
  if (p.isCheckIn === true) fields.push('check-in');
  // Text-marker requests keep their pre-upgrade fingerprint and retry receipts.
  if (p.autoSent === true && !String(p.message || '').trim().match(/^\[auto-sent\](?:\n|$)/)) fields.push('auto-sent');
  const text = JSON.stringify(fields);
  return Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, text, Utilities.Charset.UTF_8)
    .map(value => (value & 255).toString(16).padStart(2, '0')).join('');
}

function receiptReply_(receipt, fingerprint) {
  if (receipt.fingerprint !== fingerprint) return { success: false, version: APP_VERSION, code: 'id_conflict', error: 'This entry ID already belongs to different content.' };
  if (receipt.status !== 'done') return { success: false, version: APP_VERSION, code: 'write_uncertain', error: 'An earlier write was interrupted. Review this entry in the Sheet before taking further action.' };
  return { success: true, version: APP_VERSION, deliveryProtocol: DELIVERY_PROTOCOL, duplicate: true,
    sheet: receipt.sheet, timestamp: receipt.timestamp };
}

function findReceiptInSheet_(sheet, id) {
  const notes = sheet.getRange(1, 1, Math.max(1, sheet.getLastRow()), 1).getNotes();
  for (const row of notes) {
    if (!row[0].startsWith(NOTE_PREFIX)) continue;
    let record;
    try { record = JSON.parse(row[0].slice(NOTE_PREFIX.length)); } catch (_) { continue; }
    if (record.requestId === id) return { status: 'done', fingerprint: record.requestFingerprint,
      sheet: sheet.getName(), timestamp: record.timestamp };
  }
  return null;
}

function pruneReceipts_(properties) {
  // Bound the fast receipt cache. Completed IDs also live in the row's note;
  // unresolved reservations are never pruned. Unrelated Script Properties are untouched.
  const completed = [];
  for (const [key, value] of Object.entries(properties.getProperties())) {
    if (!key.startsWith(RECEIPT_PREFIX)) continue;
    try { const receipt = JSON.parse(value); if (receipt.status === 'done') completed.push([key, receipt.at]); } catch (_) { /* Preserve unknown records. */ }
  }
  completed.sort((a, b) => b[1] - a[1]);
  for (const [key] of completed.slice(499)) properties.deleteProperty(key);
}

function resolveTargetSheet_(spreadsheet, payload) {
  // A test may never fall through to the live daily tab, even in fixed mode.
  if (payload.isTest === true) {
    const sheet = spreadsheet.getSheetByName('test');
    if (!sheet) throw new Error('The test tab "test" is missing. Create it before testing; no template or daily tab was changed.');
    return { sheet };
  }
  // Requests from older extension versions have no mode: date routing is the default.
  const mode = payload.sheetMode || 'date';
  if (mode === 'fixed') {
    const name = String(payload.sheetName || '').trim();
    if (!name || name.length > 100) {
      throw new Error('The target tab name is invalid.');
    }
    const sheet = spreadsheet.getSheetByName(name);
    if (!sheet) {
      throw new Error(`The sheet tab "${name}" does not exist.`);
    }
    return { sheet };
  }
  if (mode !== 'date') {
    throw new Error('The target tab mode is invalid.');
  }

  const date = submissionDate_(spreadsheet, payload);
  const matches = spreadsheet.getSheets().map((sheet) => ({
    sheet,
    parts: sheet.getName().trim().match(/^(\d{1,2})\/(\d{1,2})\/(\d{4}|\d{2})$/)
  })).filter(({ parts }) => parts
    && Number(parts[1]) === date.month
    && Number(parts[2]) === date.day
    && Number(parts[3]) === (parts[3].length === 4 ? date.year : date.year % 100));

  // Prefer an explicit four-digit year, then the fully padded name.
  matches.sort((left, right) => right.parts[3].length - left.parts[3].length
    || Number(right.sheet.getName() === date.name) - Number(left.sheet.getName() === date.name)
    || left.sheet.getName().localeCompare(right.sheet.getName()));
  if (matches.length > 0) {
    return { sheet: matches[0].sheet };
  }
  const templateName = String(PropertiesService.getScriptProperties()
    .getProperty('TEMPLATE_SHEET_NAME') || 'Template').trim();
  const template = spreadsheet.getSheetByName(templateName);
  if (!template) {
    throw new Error(`No dated tab matches ${date.name}, and the template tab "${templateName}" is missing. Restore it or set TEMPLATE_SHEET_NAME in Apps Script properties. Nothing was saved.`);
  }
  return { sheet: null, name: date.name, template, templateName };
}

function createDailySheet_(spreadsheet, target) {
  // Called only for a validated reflection while holding the script lock, never by ping.
  const sheet = spreadsheet.insertSheet(target.name, { template: target.template });
  try {
    // Clear copied log contents only: formatting, validation, and the rest of the template stay.
    const columns = Math.min(MAX_COLUMN_PAIRS * 2, Math.floor(sheet.getMaxColumns() / 2) * 2);
    if (columns > 0) {
      sheet.getRange(1, 1, Math.min(ROWS_PER_BLOCK, sheet.getMaxRows()), columns).clearContent();
    }
    // Test entries can now grow beyond 16 rows. Remove every copied A:B log entry and note.
    sheet.getRange(1, 1, sheet.getMaxRows(), Math.min(2, sheet.getMaxColumns()))
      .clearContent().clearNote();
    if (sheet.getMaxRows() < ROWS_PER_BLOCK) {
      sheet.insertRowsAfter(sheet.getMaxRows(), ROWS_PER_BLOCK - sheet.getMaxRows());
    }
    sheet.showSheet();
    return sheet;
  } catch (error) {
    // Roll back only this newly created copy; never remove an existing daily tab or template.
    spreadsheet.deleteSheet(sheet);
    throw error;
  }
}

function submissionDate_(spreadsheet, payload) {
  const timestamp = payload.submittedAt === undefined ? new Date() : new Date(payload.submittedAt);
  if ((payload.submittedAt !== undefined && typeof payload.submittedAt !== 'string')
      || !Number.isFinite(timestamp.getTime())) {
    throw new Error('The submission date is invalid.');
  }

  let parts;
  if (payload.timezoneOffsetMinutes !== undefined) {
    const offset = payload.timezoneOffsetMinutes;
    if (!Number.isInteger(offset) || Math.abs(offset) > 14 * 60) {
      throw new Error('The submission time zone is invalid.');
    }
    // getTimezoneOffset is UTC minus local time; UTC getters avoid the script's zone.
    const localTime = new Date(timestamp.getTime() - offset * 60000);
    parts = [localTime.getUTCFullYear(), localTime.getUTCMonth() + 1, localTime.getUTCDate(),
      localTime.getUTCHours(), localTime.getUTCMinutes()];
  } else {
    parts = Utilities.formatDate(timestamp, spreadsheet.getSpreadsheetTimeZone(), 'yyyy-MM-dd-HH-mm')
      .split('-').map(Number);
  }
  const [year, month, day, hour, minute] = parts;
  const hourStart = timestamp.getTime() - (minute * 60 + timestamp.getUTCSeconds()) * 1000 - timestamp.getUTCMilliseconds();
  return { year, month, day, hour, minute, hourStart, timestamp,
    name: `${String(month).padStart(2, '0')}/${String(day).padStart(2, '0')}/${year}` };
}

function getConfig_() {
  const properties = PropertiesService.getScriptProperties();
  const spreadsheetId = String(properties.getProperty('SPREADSHEET_ID') || '').trim();
  const apiToken = String(properties.getProperty('REFLECTION_API_TOKEN') || '').trim();

  if (!/^[A-Za-z0-9_-]{20,}$/.test(spreadsheetId)) {
    throw new Error('SPREADSHEET_ID is missing or invalid in Script Properties.');
  }
  if (apiToken.length < 16) {
    throw new Error('REFLECTION_API_TOKEN is missing or too short in Script Properties.');
  }
  return { spreadsheetId, apiToken };
}

function requireValidToken_(providedToken, expectedToken) {
  const providedDigest = digest_(String(providedToken || ''));
  const expectedDigest = digest_(expectedToken);
  if (!constantTimeEqual_(providedDigest, expectedDigest)) {
    throw new Error('Unauthorized request.');
  }
}

function digest_(value) {
  return Utilities.computeDigest(
    Utilities.DigestAlgorithm.SHA_256,
    value,
    Utilities.Charset.UTF_8
  );
}

function constantTimeEqual_(left, right) {
  if (!left || !right || left.length !== right.length) {
    return false;
  }
  let difference = 0;
  for (let index = 0; index < left.length; index += 1) {
    difference |= left[index] ^ right[index];
  }
  return difference === 0;
}

function extractSpreadsheetId_(value) {
  const input = String(value || '').trim();
  if (/^[A-Za-z0-9_-]{20,}$/.test(input)) {
    return input;
  }
  const match = input.match(/\/spreadsheets\/d\/([A-Za-z0-9_-]{20,})/);
  return match ? match[1] : null;
}

function validateDuration_(seconds) {
  // Legacy clients may omit the duration. Unknown is blank, never an invented zero.
  if (seconds === undefined || seconds === null) return null;
  if (!Number.isInteger(seconds) || seconds < 0 || seconds > MAX_DURATION_SECONDS) {
    throw new Error('The timer duration must be whole seconds from zero to one year.');
  }
  return seconds;
}

function durationNumberFormat_(seconds) {
  if (seconds === null) return '@';
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor(seconds / 60) % 60;
  const remaining = seconds % 60;
  const parts = [];
  if (hours) parts.push(`[h]" ${hours === 1 ? 'hr' : 'hrs'}"`);
  if (minutes) parts.push(`${hours ? 'm' : '[m]'}" min"`);
  if (remaining || !parts.length) parts.push(`${hours || minutes ? 's' : '[s]'}" ${remaining === 1 ? 'sec' : 'secs'}"`);
  return parts.join(' ');
}

function validateSession_(payload, allotted) {
  if (payload.autoSent !== undefined && typeof payload.autoSent !== 'boolean') throw new Error('Invalid auto-send status.');
  const autoSent = payload.autoSent === true || /^\[auto-sent\](?:\n|$)/.test(String(payload.message || '').trim());
  const actualDurationSeconds = validateDuration_(payload.actualDurationSeconds);
  if (actualDurationSeconds !== null && (allotted === null || actualDurationSeconds > allotted)) {
    throw new Error('Actual time cannot exceed the allotted time.');
  }
  if (payload.endedEarly !== undefined && typeof payload.endedEarly !== 'boolean') throw new Error('Invalid early-finish status.');
  const endedEarly = payload.endedEarly === true;
  if (payload.isCheckIn !== undefined && typeof payload.isCheckIn !== 'boolean') throw new Error('Invalid check-in status.');
  const isCheckIn = payload.isCheckIn === true;
  if (isCheckIn && (endedEarly || actualDurationSeconds === null)) throw new Error('A check-in requires actual time and cannot be an early finish.');
  if (endedEarly && actualDurationSeconds === null) throw new Error('An early finish must include actual time.');
  if (payload.earlyEndReason !== undefined && typeof payload.earlyEndReason !== 'string') throw new Error('Invalid early-finish reason.');
  const earlyEndReason = String(payload.earlyEndReason || '').trim();
  if (earlyEndReason.length > 1000) throw new Error('The early-finish reason exceeds 1000 characters.');
  return { actualDurationSeconds, endedEarly, isCheckIn, autoSent, earlyEndReason: endedEarly ? earlyEndReason : '' };
}

function appendReflection_(sheet, message, moment, spreadsheet, durationSeconds = null,
    session = { actualDurationSeconds: null, endedEarly: false, earlyEndReason: '' }) {
  ensureColumns_(sheet, 6);
  // Only widen the newly owned fields, never shrink a user-chosen wider column.
  for (const [column, width] of [[3, 220], [4, 220], [5, 135], [6, 300]]) {
    if (sheet.getColumnWidth(column) < width) sheet.setColumnWidth(column, width);
  }
  const previous = previousEntry_(sheet, spreadsheet);
  const needsHour = !previous || previous.hourStart !== moment.hourStart;
  const rows = needsHour ? 2 : 1;
  const background = previous && textColor_(previous.background) === '#000000' ? '#595959' : '#ffffff';
  reserveTopRows_(sheet, rows);
  excludeNewCellsFromConditionalRules_(sheet, rows);

  // Match column B's white cell outlines across every column.
  sheet.getRange(1, 1, 1, sheet.getMaxColumns())
    .setBorder(true, true, true, true, true, false, '#ffffff', SpreadsheetApp.BorderStyle.SOLID);
  const entry = sheet.getRange(1, 1, 1, 6);
  // Treat reflections as plain text, including messages beginning with '='.
  entry.setNumberFormat('@');
  // Store a real Sheets duration (fraction of a day), not an uncalculable label.
  entry.getCell(1, 3).setNumberFormat(durationNumberFormat_(session.actualDurationSeconds));
  entry.getCell(1, 4).setNumberFormat(durationNumberFormat_(durationSeconds));
  const clockLabel = `${moment.hour % 12 || 12}:${String(moment.minute).padStart(2, '0')}`;
  const response = session.autoSent && !message.trim() ? 'N/A' : message;
  entry.setValues([[clockLabel, response.startsWith('=') ? "'" + response : response,
    session.actualDurationSeconds === null ? '' : session.actualDurationSeconds / 86400,
    durationSeconds === null ? '' : durationSeconds / 86400, [session.isCheckIn ? 'Check-in' : session.endedEarly ? 'ended early' : '', session.autoSent ? 'auto-sent' : ''].filter(Boolean).join(' · '),
    session.earlyEndReason.startsWith('=') ? "'" + session.earlyEndReason : session.earlyEndReason]])
    .setFontWeight('normal').setVerticalAlignment('top').clearNote();
  entry.getCell(1, 1).setBackground(background)
    .setFontColor(textColor_(background)).setNote(NOTE_PREFIX + JSON.stringify({
      kind: 'entry', timestamp: moment.timestamp.toISOString(), hourStart: moment.hourStart, durationSeconds,
      actualDurationSeconds: session.actualDurationSeconds, endedEarly: session.endedEarly, isCheckIn: session.isCheckIn === true, autoSent: session.autoSent === true,
      requestId: session.requestId || undefined, requestFingerprint: session.fingerprint || undefined
    }));
  // New rows can inherit an hour band's fill. Restore column stripes explicitly:
  // B/D/F/... are black with white text; C/E/G/... are white with black text.
  // Leave A's independent alternation and the other cells' content/style intact.
  const stripedCells = sheet.getRange(1, 2, 1, sheet.getMaxColumns() - 1);
  const backgrounds = Array.from({ length: stripedCells.getNumColumns() },
    (_unused, index) => index % 2 === 0 ? '#000000' : '#ffffff');
  stripedCells.setBackgrounds([backgrounds])
    .setFontColors([backgrounds.map((color) => color === '#000000' ? '#ffffff' : '#000000')]);
  sheet.getRange(1, 2, 1, 5).setWrap(true);

  if (needsHour) {
    const theme = HOUR_THEMES[moment.hour];
    const marker = sheet.getRange(2, 1, 1, sheet.getMaxColumns());
    sheet.getRange(2, 1, 1, 6).setNumberFormat('@')
      .setValues([[`${moment.hour % 12 || 12}:00 ${moment.hour < 12 ? 'AM' : 'PM'}`, '', '', '', '', '']]).clearNote();
    marker.setBackground(theme[0]).setFontColor(textColor_(theme[0]))
      .setFontWeight('bold').setWrap(false)
      .setBorder(true, false, true, false, false, false, theme[1], SpreadsheetApp.BorderStyle.SOLID_MEDIUM);
    marker.getCell(1, 1).setNote(NOTE_PREFIX + JSON.stringify({ kind: 'hour', hourStart: moment.hourStart }));
  }
  return { range: entry.getA1Notation(), timestamp: moment.timestamp };
}

function previousEntry_(sheet, spreadsheet) {
  const count = Math.max(1, sheet.getLastRow());
  const range = sheet.getRange(1, 1, count, 2);
  const values = range.getValues();
  const notes = range.getNotes();
  for (let index = 0; index < count; index += 1) {
    let metadata = {};
    const note = notes[index][0];
    if (note.startsWith(NOTE_PREFIX)) {
      try { metadata = JSON.parse(note.slice(NOTE_PREFIX.length)); } catch (_error) { /* Ordinary note. */ }
    }
    if (metadata.kind === 'hour' || values[index][0] === '' || values[index][1] === '') continue;
    let hourStart = metadata.kind === 'entry' && Number.isFinite(metadata.hourStart) ? metadata.hourStart : null;
    const value = values[index][0];
    if (hourStart === null && value instanceof Date && value.getUTCFullYear() >= 2000) {
      hourStart = submissionDate_(spreadsheet, { submittedAt: value.toISOString() }).hourStart;
    }
    return { hourStart, background: sheet.getRange(index + 1, 1, 1, 1).getBackground() };
  }
  return null;
}

function reserveTopRows_(sheet, rows) {
  if (sheet.getMaxRows() < rows) sheet.insertRowsAfter(sheet.getMaxRows(), rows - sheet.getMaxRows());
  let emptyRows = 0;
  // Reuse only entirely empty rows; keep data in other columns with its original row.
  while (emptyRows < rows && sheet.getRange(emptyRows + 1, 1, 1, sheet.getMaxColumns()).isBlank()) emptyRows += 1;
  const insert = rows - emptyRows;
  if (insert > 0) sheet.insertRowsBefore(1, insert);
}

function excludeNewCellsFromConditionalRules_(sheet, rows) {
  // Protect the new full-width entry/hour rows without changing neighboring rules.
  const exclusions = [{ row: 1, col: 1, endRow: rows, endCol: sheet.getMaxColumns() }];
  const rules = sheet.getConditionalFormatRules();
  let changed = false;
  const updated = [];
  rules.forEach((rule) => {
    const ranges = [];
    rule.getRanges().forEach((range) => {
      const row = range.getRow(), col = range.getColumn();
      const endRow = row + range.getNumRows() - 1, endCol = col + range.getNumColumns() - 1;
      let parts = [{ row, col, endRow, endCol }];
      exclusions.forEach((cut) => {
        parts = parts.flatMap((part) => {
          const top = Math.max(part.row, cut.row), left = Math.max(part.col, cut.col);
          const bottom = Math.min(part.endRow, cut.endRow), right = Math.min(part.endCol, cut.endCol);
          if (top > bottom || left > right) return [part];
          changed = true;
          const remaining = [];
          if (part.row < top) remaining.push({ ...part, endRow: top - 1 });
          if (part.endRow > bottom) remaining.push({ ...part, row: bottom + 1 });
          if (part.col < left) remaining.push({ row: top, endRow: bottom, col: part.col, endCol: left - 1 });
          if (part.endCol > right) remaining.push({ row: top, endRow: bottom, col: right + 1, endCol: part.endCol });
          return remaining;
        });
      });
      parts.forEach((part) => ranges.push(sheet.getRange(part.row, part.col,
        part.endRow - part.row + 1, part.endCol - part.col + 1)));
    });
    if (ranges.length) updated.push(rule.copy().setRanges(ranges).build());
  });
  if (changed) sheet.setConditionalFormatRules(updated);
}

function textColor_(background) {
  const channels = background.replace('#', '').match(/.{2}/g).map((hex) => {
    const channel = parseInt(hex, 16) / 255;
    return channel <= 0.04045 ? channel / 12.92 : Math.pow((channel + 0.055) / 1.055, 2.4);
  });
  const luminance = channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
  return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? '#000000' : '#ffffff';
}

function ensureColumns_(sheet, requiredColumns) {
  const missingColumns = requiredColumns - sheet.getMaxColumns();
  if (missingColumns > 0) {
    sheet.insertColumnsAfter(sheet.getMaxColumns(), missingColumns);
  }
}

function jsonOutput_(value) {
  return ContentService
    .createTextOutput(JSON.stringify(value))
    .setMimeType(ContentService.MimeType.JSON);
}
