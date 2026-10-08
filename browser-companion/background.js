/* Live browser metadata only: no page scripts, history, cookies, or network calls. */
"use strict";

(function () {
  const HOST_NAME = "com.reflectiontimer.focus";
  const LIMITS = Object.freeze({ windows: 128, tabs: 2048, groups: 512, title: 256 });

  function siteHost(value) {
    try {
      const parsed = new URL(value);
      if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return "";
      const host = parsed.hostname.toLowerCase().replace(/\.+$/, "");
      return host ? host + (parsed.port ? ":" + parsed.port : "") : "";
    } catch { return ""; }
  }

  const validId = value => Number.isSafeInteger(value) && value >= 0;
  const title = value => typeof value === "string" ? value.slice(0, LIMITS.title) : "";
  const number = value => Number.isFinite(value) ? Math.trunc(value) : 0;

  function createCompanion(api, options = {}) {
    const schedule = options.setTimeout || setTimeout;
    const cancel = options.clearTimeout || clearTimeout;
    const every = options.setInterval || setInterval;
    const cancelEvery = options.clearInterval || clearInterval;
    const now = options.now || Date.now;
    const uuid = options.uuid || (() => crypto.randomUUID());
    const browser = options.browser || (/Edg\//.test(navigator.userAgent) ? "msedge" : "chrome");
    let epoch = uuid(), seq = 0, generation = 0, port = null, running = false;
    let retry = null, snapshotTimer = null, heartbeat = null, collecting = false;
    let lastAck = 0, lastAckSeq = 0, rateStart = 0, navigationCount = 0, seeded = false, retryDelay = 2000;
    const committedHosts = new Map();
    const listeners = [];

    function listen(event, callback) {
      if (!event) return;
      event.addListener(callback);
      listeners.push([event, callback]);
    }

    function packet(type, payload) {
      return { version: 1, type, browser, epoch, seq: ++seq, ...payload };
    }

    function send(type, payload = {}) {
      if (!port) return false;
      try {
        const message = packet(type, payload);
        if (new TextEncoder().encode(JSON.stringify(message)).byteLength > 1024 * 1024)
          throw new Error("Snapshot exceeds the metadata transport bound.");
        port.postMessage(message); return true;
      }
      catch { disconnect(); return false; }
    }

    function disconnect() {
      const prior = port; port = null; generation++;
      if (prior) { try { prior.disconnect(); } catch {} }
      if (running && retry === null) {
        retry = schedule(() => { retry = null; connect(); }, retryDelay);
        retryDelay = Math.min(30000, retryDelay * 2);
      }
    }

    function changed() {
      generation++;
      if (port && snapshotTimer === null) snapshotTimer = schedule(() => {
        snapshotTimer = null;
        void capture();
      }, 150);
    }

    function navigation(payload) {
      generation++;
      if (!port) return;
      const time = now();
      if (time - rateStart >= 1000) { rateStart = time; navigationCount = 0; }
      if (++navigationCount > 100) {
        // A partial event stream must never authorize unrelated tabs. A new epoch
        // conservatively discards inherited links instead of guessing provenance.
        epoch = uuid(); seq = 0; lastAckSeq = 0; lastAck = now(); navigationCount = 0; seeded = false;
        committedHosts.clear();
        send("hello"); changed(); return;
      }
      send("navigation", { transitionType: "", qualifiers: [], previousSiteHost: "", siteHost: "", ...payload });
      changed();
    }

    async function capture() {
      if (!port || collecting) return false;
      collecting = true;
      const began = generation, connection = port;
      try {
        const [allWindows, allTabs, allGroups, currentWindow] = await Promise.all([
          api.windows.getAll({ windowTypes: ["normal", "popup"] }),
          api.tabs.query({}),
          api.tabGroups.query({}),
          api.windows.getLastFocused({ populate: false })
        ]);
        if (port !== connection) return false;
        if (began !== generation) { changed(); return false; }
        const windows = allWindows.filter(window => validId(window.id)).slice(0, LIMITS.windows).map(window => ({
          id: window.id, focused: window.focused === true,
          state: ["normal", "minimized", "maximized", "fullscreen", "locked-fullscreen"].includes(window.state) ? window.state : "normal",
          left: number(window.left), top: number(window.top), width: number(window.width), height: number(window.height)
        }));
        const windowIds = new Set(windows.map(window => window.id));
        const tabs = allTabs.filter(tab => validId(tab.id) && windowIds.has(tab.windowId)).slice(0, LIMITS.tabs).map(tab => {
          const host = siteHost(tab.url);
          if (!committedHosts.has(tab.id)) committedHosts.set(tab.id, host);
          return {
            id: tab.id, windowId: tab.windowId, index: Math.max(0, number(tab.index)), active: tab.active === true,
            title: title(tab.title), siteHost: host, groupId: validId(tab.groupId) ? tab.groupId : -1,
            openerTabId: validId(tab.openerTabId) ? tab.openerTabId : -1
          };
        });
        const currentIds = new Set(tabs.map(tab => tab.id));
        for (const id of committedHosts.keys()) if (!currentIds.has(id)) committedHosts.delete(id);
        const groups = allGroups.filter(group => validId(group.id) && windowIds.has(group.windowId)).slice(0, LIMITS.groups)
          .map(group => ({ id: group.id, windowId: group.windowId, title: title(group.title) }));
        seeded = true;
        return send("snapshot", {
          windows, tabs, groups, currentWindowId: validId(currentWindow.id) ? currentWindow.id : -1,
          overflow: allWindows.length > LIMITS.windows || allTabs.length > LIMITS.tabs || allGroups.length > LIMITS.groups
        });
      } catch {
        // Missing permissions or browser API failures are unavailable data, never
        // an empty snapshot that could accidentally change the selected target.
        disconnect(); return false;
      } finally { collecting = false; }
    }

    function connect() {
      if (!running || port) return;
      try {
        const connected = api.runtime.connectNative(HOST_NAME);
        port = connected; lastAck = now(); generation++;
        // Events are not replayed across a transport gap. Reset inheritance
        // because links followed while disconnected cannot be proven here.
        epoch = uuid(); seq = 0; lastAckSeq = 0; seeded = false; committedHosts.clear();
        connected.onMessage.addListener(message => {
          if (port !== connected) return;
          if (message && message.version === 1 && message.type === "ack" && message.epoch === epoch &&
            Number.isSafeInteger(message.seq) && message.seq > lastAckSeq && message.seq <= seq) {
            lastAck = now(); lastAckSeq = message.seq; retryDelay = 2000;
          }
        });
        connected.onDisconnect.addListener(() => {
          // Reading lastError prevents Chrome's unrelated console warning; no
          // error strings, tab URLs, or other browser data are logged.
          void api.runtime.lastError;
          if (port === connected) disconnect();
        });
        send("hello");
        void capture();
      } catch { disconnect(); }
    }

    function start() {
      if (running) return;
      running = true;
      for (const event of [api.tabs.onCreated, api.tabs.onUpdated, api.tabs.onActivated, api.tabs.onMoved, api.tabs.onAttached,
        api.tabs.onDetached, api.windows.onCreated, api.windows.onRemoved, api.windows.onFocusChanged,
        api.tabGroups.onCreated, api.tabGroups.onUpdated, api.tabGroups.onRemoved, api.tabGroups.onMoved]) listen(event, changed);
      listen(api.webNavigation.onCreatedNavigationTarget, details => {
        if (!validId(details.sourceTabId) || !validId(details.tabId)) return;
        const previous = committedHosts.get(details.sourceTabId) || "";
        const destination = siteHost(details.url);
        // The created event establishes the child's initial navigation target.
        // Carry it into the first commit/redirect so that it cannot erase the
        // actual opener evidence merely because this tab has no old document.
        committedHosts.set(details.tabId, destination);
        navigation({ kind: "created", sourceTabId: details.sourceTabId, tabId: details.tabId,
          previousSiteHost: previous, siteHost: destination });
      });
      listen(api.webNavigation.onCommitted, details => {
        if (details.frameId !== 0 || !validId(details.tabId)) return;
        const previous = committedHosts.get(details.tabId) || "";
        const host = siteHost(details.url);
        committedHosts.set(details.tabId, host);
        navigation({ kind: "committed", tabId: details.tabId, previousSiteHost: previous, siteHost: host,
          transitionType: title(details.transitionType).slice(0, 32),
          qualifiers: Array.isArray(details.transitionQualifiers) ? details.transitionQualifiers.filter(value =>
            ["client_redirect", "server_redirect", "forward_back", "from_address_bar"].includes(value)).slice(0, 4) : [] });
      });
      listen(api.tabs.onRemoved, tabId => {
        if (!validId(tabId)) return;
        const previous = committedHosts.get(tabId) || "";
        committedHosts.delete(tabId);
        navigation({ kind: "removed", tabId, previousSiteHost: previous });
      });
      listen(api.tabs.onReplaced, (addedTabId, removedTabId) => {
        if (!validId(addedTabId) || !validId(removedTabId)) return;
        const previous = committedHosts.get(removedTabId) || "";
        const replacement = committedHosts.get(addedTabId) || "";
        committedHosts.delete(removedTabId);
        // A prerendered replacement may already have its own committed URL.
        // Do not label an unknown/new document with the removed tab's old site.
        if (replacement) committedHosts.set(addedTabId, replacement);
        else committedHosts.delete(addedTabId);
        navigation({ kind: "replaced", tabId: addedTabId, replacedTabId: removedTabId, previousSiteHost: previous, siteHost: replacement });
      });
      heartbeat = every(() => {
        if (!port) return;
        if (now() - lastAck > 5000) { disconnect(); return; }
        void capture();
      }, 1000);
      connect();
    }

    function stop() {
      running = false;
      for (const [event, callback] of listeners) event.removeListener(callback);
      listeners.length = 0;
      if (retry !== null) cancel(retry);
      if (snapshotTimer !== null) cancel(snapshotTimer);
      if (heartbeat !== null) cancelEvery(heartbeat);
      retry = snapshotTimer = heartbeat = null;
      disconnect();
    }

    return { start, stop, capture, isSeeded: () => seeded };
  }

  if (typeof module !== "undefined" && module.exports) module.exports = { siteHost, createCompanion, HOST_NAME, LIMITS };
  else createCompanion(chrome).start();
})();
