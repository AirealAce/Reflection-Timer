"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const { createCompanion, siteHost, HOST_NAME, LIMITS } = require("./background.js");
let checks = 0;
function check(condition, description) { assert.ok(condition, description); checks++; }
function event() {
  const listeners = new Set();
  return { addListener: fn => listeners.add(fn), removeListener: fn => listeners.delete(fn),
    fire: (...args) => { for (const fn of [...listeners]) fn(...args); }, size: () => listeners.size };
}
let epochCounter = 0;
const uuid = () => `00000000-0000-4000-8000-${(++epochCounter).toString(16).padStart(12, "0")}`;
const settle = () => new Promise(resolve => setImmediate(resolve));

function fixture() {
  let time = 10000, timerId = 0;
  const timeouts = new Map(), intervals = new Map(), ports = [];
  const data = {
    windows: [{ id: 1, focused: true, state: "normal", left: 0, top: 0, width: 1000, height: 800 }],
    tabs: [{ id: 11, windowId: 1, index: 0, active: true, title: "Synthetic page", url: "https://example.org/private-path?secret=value#fragment", groupId: 7, openerTabId: 99 }],
    groups: [{ id: 7, windowId: 1, title: "Synthetic group" }],
    currentWindowId: 1
  };
  const api = {
    runtime: { lastError: null, connectNative: name => {
      check(name === HOST_NAME, "Only the fixed local native host is contacted");
      const port = { messages: [], onMessage: event(), onDisconnect: event(), disconnected: false,
        postMessage(message) { this.messages.push(structuredClone(message)); },
        disconnect() { if (!this.disconnected) { this.disconnected = true; this.onDisconnect.fire(); } } };
      ports.push(port); return port;
    } },
    windows: { getAll: async () => structuredClone(data.windows), getLastFocused: async () => ({ id: data.currentWindowId }),
      onCreated: event(), onRemoved: event(), onFocusChanged: event() },
    tabs: { query: async () => structuredClone(data.tabs), onCreated: event(), onUpdated: event(), onActivated: event(),
      onMoved: event(), onAttached: event(), onDetached: event(), onRemoved: event(), onReplaced: event() },
    tabGroups: { query: async () => structuredClone(data.groups), onCreated: event(), onUpdated: event(), onRemoved: event(), onMoved: event() },
    webNavigation: { onCreatedNavigationTarget: event(), onCommitted: event() }
  };
  const companion = createCompanion(api, {
    browser: "chrome", uuid, now: () => time,
    setTimeout: (fn, delay) => { const id = ++timerId; timeouts.set(id, { fn, delay }); return id; },
    clearTimeout: id => timeouts.delete(id),
    setInterval: (fn, delay) => { const id = ++timerId; intervals.set(id, { fn, delay }); return id; },
    clearInterval: id => intervals.delete(id)
  });
  return { api, data, companion, ports, timeouts, intervals,
    now: value => { time = value; },
    tick: () => { for (const timer of [...intervals.values()]) timer.fn(); },
    flush: () => { const pending = [...timeouts.values()]; timeouts.clear(); for (const timer of pending) timer.fn(); },
    messages: type => ports.flatMap(port => port.messages).filter(message => message.type === type) };
}

(async function () {
  check(siteHost("https://User:Password@EXAMPLE.org:443/private?secret#fragment") === "example.org", "Only normalized host survives credentials, default port, path, query, and fragment removal");
  check(siteHost("http://example.org:8443/page") === "example.org:8443", "A non-default port remains part of the target identity");
  check(siteHost("https://example.org./") === "example.org", "Trailing host dots are canonicalized");
  check(siteHost("https://bücher.example/") === "xn--bcher-kva.example", "International hostnames use browser-standard ASCII normalization");
  check(siteHost("https://[::1]:8443/path") === "[::1]:8443", "IPv6 hosts keep unambiguous brackets and non-default ports");
  check(["chrome://settings", "edge://newtab", "file:///private.txt", "data:text/html,test", "not a URL"].every(url => siteHost(url) === ""), "Non-web and malformed URLs are never treated as website hosts");

  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "manifest.json"), "utf8"));
  const id = [...crypto.createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest().subarray(0, 16)]
    .map(byte => String.fromCharCode(97 + (byte >> 4), 97 + (byte & 15))).join("");
  check(id === "mgalafjodgnoeponalohbmdopkkbnfok", "The public manifest key derives the fixed native-host extension ID");
  check(manifest.manifest_version === 3 && manifest.background.service_worker === "background.js", "The companion is an MV3 service worker");
  check(JSON.stringify(manifest.permissions.slice().sort()) === JSON.stringify(["nativeMessaging", "tabGroups", "tabs", "webNavigation"].sort()), "Only the four required metadata/IPC permissions are requested");
  check(!manifest.host_permissions && !manifest.content_scripts && !manifest.externally_connectable, "No page host access, injected page scripts, or page-to-extension messaging is declared");

  const base = fixture(); base.companion.start(); await settle();
  const initial = base.messages("snapshot")[0];
  check(base.messages("hello").length === 1 && initial.version === 1 && initial.browser === "chrome", "Connection sends a versioned hello and full initial snapshot");
  check(initial.tabs[0].siteHost === "example.org" && initial.groups[0].title === "Synthetic group", "Snapshots include canonical sites and browser tab-group labels");
  check(initial.currentWindowId === 1 && initial.tabs[0].active && initial.windows[0].focused, "Current browser window and selected tab metadata survive when available");
  check(!JSON.stringify(initial).includes("private-path") && !JSON.stringify(initial).includes("secret=value") && !Object.hasOwn(initial.tabs[0], "url"), "Neither full URL nor path/query/fragment is sent on the wire");
  check(base.messages("navigation").length === 0 && initial.tabs[0].openerTabId === 99, "Existing opener metadata never creates synthetic link provenance at startup");
  base.data.windows[0].focused = false; base.data.currentWindowId = 1; await base.companion.capture();
  check(base.messages("snapshot").at(-1).currentWindowId === 1 && !base.messages("snapshot").at(-1).windows[0].focused, "Last-focused browser selection remains available while a desktop chooser is foreground");

  base.api.webNavigation.onCreatedNavigationTarget.fire({ sourceTabId: 11, sourceFrameId: 0, tabId: 12, url: "https://external.example/private?token=hidden" });
  const created = base.messages("navigation").at(-1);
  check(created.kind === "created" && created.sourceTabId === 11 && created.tabId === 12 && created.siteHost === "external.example", "Cross-site new tabs carry actual source-tab provenance immediately");
  check(created.previousSiteHost === "example.org" && !JSON.stringify(created).includes("token=hidden"), "Source host is preserved without leaking the outgoing URL");
  base.api.webNavigation.onCommitted.fire({ tabId: 12, frameId: 0, url: "https://external.example/private", transitionType: "link", transitionQualifiers: [] });
  check(base.messages("navigation").at(-1).previousSiteHost === "external.example",
    "A new tab's first commit preserves its created-target evidence instead of discarding opener lineage");
  base.api.webNavigation.onCommitted.fire({ tabId: 12, frameId: 0, url: "https://redirected.example/", transitionType: "link", transitionQualifiers: ["server_redirect"] });
  check(base.messages("navigation").at(-1).previousSiteHost === "external.example" && base.messages("navigation").at(-1).siteHost === "redirected.example",
    "A new tab's first redirect remains connected to its browser-proven initial target");
  const beforeSubframe = base.messages("navigation").length;
  base.api.webNavigation.onCommitted.fire({ tabId: 11, frameId: 2, url: "https://iframe.example/", transitionType: "link", transitionQualifiers: [] });
  check(base.messages("navigation").length === beforeSubframe, "Subframe navigation never authorizes another site");
  base.api.webNavigation.onCommitted.fire({ tabId: 11, frameId: 0, url: "https://second.example/private", transitionType: "link", transitionQualifiers: ["server_redirect"] });
  const linked = base.messages("navigation").at(-1);
  check(linked.kind === "committed" && linked.previousSiteHost === "example.org" && linked.siteHost === "second.example" && linked.transitionType === "link", "Same-tab outbound links retain the previous committed site for desktop policy");
  check(linked.qualifiers[0] === "server_redirect", "Redirect qualifiers are preserved");
  base.api.webNavigation.onCommitted.fire({ tabId: 11, frameId: 0, url: "https://independent.example/", transitionType: "typed", transitionQualifiers: ["from_address_bar", "unknown"] });
  const typed = base.messages("navigation").at(-1);
  check(typed.previousSiteHost === "second.example" && typed.transitionType === "typed" && typed.qualifiers.join() === "from_address_bar", "Manual address-bar navigation can be distinguished from inherited links");
  base.api.tabs.onReplaced.fire(20, 11);
  check(base.messages("navigation").at(-1).kind === "replaced" && base.messages("navigation").at(-1).replacedTabId === 11
    && base.messages("navigation").at(-1).previousSiteHost === "independent.example" && base.messages("navigation").at(-1).siteHost === "",
    "Tab replacement carries identities without fabricating a committed URL for the new document");
  base.api.tabs.onRemoved.fire(20);
  check(base.messages("navigation").at(-1).kind === "removed" && base.messages("navigation").at(-1).previousSiteHost === "", "Closed tabs are explicitly removed without guessing unavailable site metadata");
  base.api.webNavigation.onCommitted.fire({ tabId: 21, frameId: 0, url: "https://prerendered.example/", transitionType: "typed", transitionQualifiers: ["from_address_bar"] });
  base.api.tabs.onReplaced.fire(21, 12);
  check(base.messages("navigation").at(-1).siteHost === "prerendered.example" && base.messages("navigation").at(-1).previousSiteHost === "redirected.example",
    "A prerendered replacement retains its own known committed host instead of the removed tab's host");
  const sequences = base.ports[0].messages.map(message => message.seq);
  check(sequences.every((seq, index) => index === 0 || seq > sequences[index - 1]), "Hello, snapshots and navigation share strictly ordered sequence numbers");
  check(base.timeouts.size === 1, "A burst of metadata events coalesces to one scheduled snapshot");
  base.companion.stop();
  check(base.timeouts.size === 0 && base.intervals.size === 0 && base.api.tabs.onRemoved.size() === 0, "Stopping removes listeners, timers and the native connection");

  const stale = fixture(); stale.companion.start(); await settle();
  const hello = stale.messages("hello")[0];
  stale.ports[0].onMessage.fire({ version: 1, type: "ack", epoch: hello.epoch, seq: hello.seq });
  stale.now(14000); stale.ports[0].onMessage.fire({ version: 1, type: "ack", epoch: hello.epoch, seq: hello.seq });
  stale.now(16001); stale.tick();
  check(stale.ports[0].disconnected && stale.timeouts.size === 1, "Replayed old ACKs cannot keep a stale connection alive");
  stale.flush(); await settle();
  check(stale.ports.length === 2 && stale.messages("hello").at(-1).epoch !== hello.epoch, "Reconnect uses a fresh epoch so missing navigation cannot be guessed or replayed");
  check(stale.messages("navigation").length === 0 && stale.messages("snapshot").length === 2, "Reconnect sends a fresh full snapshot without fabricating opener events");
  stale.companion.stop();

  const alive = fixture(); alive.companion.start(); await settle();
  const snapshot = alive.messages("snapshot")[0];
  alive.now(14500); alive.ports[0].onMessage.fire({ version: 1, type: "ack", epoch: snapshot.epoch, seq: snapshot.seq });
  alive.now(16001); alive.tick(); await settle();
  check(!alive.ports[0].disconnected && alive.messages("snapshot").length === 2, "A fresh acknowledgement keeps periodic snapshots running");
  alive.companion.stop();

  const unavailable = fixture(); unavailable.api.tabs.query = async () => { throw new Error("Permission denied"); };
  unavailable.companion.start(); await settle();
  check(unavailable.messages("snapshot").length === 0 && unavailable.ports[0].disconnected, "Browser API failures disconnect rather than publishing a misleading empty target list");
  unavailable.companion.stop();

  const bounded = fixture();
  bounded.data.tabs = Array.from({ length: LIMITS.tabs + 1 }, (_, index) => ({ ...bounded.data.tabs[0], id: index + 1, index, title: "T".repeat(300) }));
  bounded.companion.start(); await settle();
  const limited = bounded.messages("snapshot")[0];
  check(limited.tabs.length === LIMITS.tabs && limited.overflow && limited.tabs.every(tab => tab.title.length === LIMITS.title), "Snapshot counts and titles are bounded and truncation is explicit");
  bounded.companion.stop();

  const raced = fixture(); let resolveQuery;
  raced.api.tabs.query = () => new Promise(resolve => { resolveQuery = resolve; });
  raced.companion.start();
  raced.api.tabs.onCreated.fire({ id: 99 });
  resolveQuery(structuredClone(raced.data.tabs)); await settle();
  check(raced.messages("snapshot").length === 0 && raced.timeouts.size === 1, "A snapshot racing browser changes is discarded and rescheduled");
  raced.api.tabs.query = async () => structuredClone(raced.data.tabs);
  raced.flush(); await settle();
  check(raced.messages("snapshot").length === 1, "The stable retry publishes a full snapshot after the race");
  raced.companion.stop();

  const replacedConnection = fixture(); let finishOldQuery;
  replacedConnection.api.tabs.query = () => new Promise(resolve => { finishOldQuery = resolve; });
  replacedConnection.companion.start();
  replacedConnection.ports[0].disconnect();
  replacedConnection.flush();
  finishOldQuery(structuredClone(replacedConnection.data.tabs)); await settle();
  check(replacedConnection.ports.length === 2 && replacedConnection.ports[1].messages.every(message => message.type === "hello"),
    "A snapshot started on a disconnected native port cannot publish into a replacement connection");
  replacedConnection.api.tabs.query = async () => structuredClone(replacedConnection.data.tabs);
  await replacedConnection.companion.capture();
  check(replacedConnection.ports[1].messages.filter(message => message.type === "snapshot").length === 1,
    "A replacement connection receives only metadata freshly collected for that connection");
  replacedConnection.companion.stop();

  const flood = fixture(); flood.companion.start(); await settle();
  const firstEpoch = flood.messages("hello")[0].epoch;
  for (let index = 0; index < 101; index++) flood.api.webNavigation.onCreatedNavigationTarget.fire({ sourceTabId: 11, tabId: 100 + index, url: "https://external.example/" });
  check(flood.messages("hello").at(-1).epoch !== firstEpoch && flood.messages("navigation").length === 100, "Navigation floods reset inheritance instead of silently dropping provenance inside an unchanged epoch");
  flood.companion.stop();
  console.log(`${checks} browser companion checks passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
