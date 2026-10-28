// PDM extension background (service worker in Opera/Chrome/Edge, event page in Firefox).
// Talks to PDM only through native messaging ("com.pdm.host"); there is no network port.
// Rule: a download is cancelled in the browser only AFTER PDM confirmed it took it over.
'use strict';

const HOST = 'com.pdm.host';
const IS_FIREFOX = typeof browser !== 'undefined' && !!(browser.runtime && browser.runtime.getBrowserInfo);
const t = (k, ...a) => chrome.i18n.getMessage(k, a.map(String)) || k;

const DEFAULTS = {
  capture: true,
  extensions: 'zip rar 7z tar gz tgz bz2 xz zst iso img exe msi msix apk dmg deb rpm pdf epub doc docx xls xlsx ppt pptx odt mp3 m4a flac wav ogg opus aac mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts torrent bin',
  minSizeKB: 0,
  excludedSites: '',
  videoButton: true,
  sendCookies: true,
  skipNext: false,
};

async function getSettings() {
  const s = await chrome.storage.local.get(DEFAULTS);
  return { ...DEFAULTS, ...s };
}

// ---------------- native messaging ----------------

function sendNative(msg, timeoutMs = 40000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('timeout')), timeoutMs);
    try {
      chrome.runtime.sendNativeMessage(HOST, msg, (resp) => {
        clearTimeout(timer);
        const err = chrome.runtime.lastError;
        if (err) reject(new Error(err.message || 'native'));
        else { resolve(resp); reloadIfUpdated(resp); }
      });
    } catch (e) {
      clearTimeout(timer);
      reject(e);
    }
  });
}

// ---------------- self-update ----------------
// PDM keeps this unpacked extension's folder up to date and reports that version in every reply. When the files
// on disk are newer than the running code, reload once so the browser picks them up (same as its reload button).
// Tried once per version: if the extension was loaded from another folder, reloading would not help.

function compareVersions(a, b) {
  const pa = String(a).split('.').map(Number);
  const pb = String(b).split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d > 0 ? 1 : -1;
  }
  return 0;
}

async function reloadIfUpdated(resp) {
  try {
    const available = resp && resp.ok && typeof resp.extVersion === 'string' ? resp.extVersion : null;
    if (!available || compareVersions(available, chrome.runtime.getManifest().version) <= 0) return;
    const { reloadTried } = await chrome.storage.local.get('reloadTried');
    if (reloadTried === available) return;
    await chrome.storage.local.set({ reloadTried: available });
    if (IS_FIREFOX) {
      // An installed (signed) Firefox add-on cannot reload itself into a new version: PDM opens the new .xpi in
      // Firefox, which asks to update it. A temporary add-on (about:debugging) reloads from its file like below.
      const self = await chrome.management.getSelf().catch(() => null);
      if (!self || self.installType !== 'development') {
        sendNative({ type: 'updateExtension' }, 5000).catch(() => {});
        return;
      }
    }
    setTimeout(() => chrome.runtime.reload(), 2000); // let the current hand-over finish first
  } catch { /* never break a download over this */ }
}

// ---------------- helpers ----------------

function isHttp(url) {
  return typeof url === 'string' && /^https?:\/\//i.test(url);
}

function baseName(path) {
  if (!path) return '';
  const parts = String(path).split(/[\\/]/);
  return parts[parts.length - 1] || '';
}

function extOf(nameOrUrl) {
  let s = String(nameOrUrl || '');
  try { if (isHttp(s)) s = new URL(s).pathname; } catch { /* keep */ }
  s = baseName(s);
  const i = s.lastIndexOf('.');
  return i > 0 ? s.slice(i + 1).toLowerCase() : '';
}

function hostOf(url) {
  try { return new URL(url).hostname.toLowerCase(); } catch { return ''; }
}

function isExcluded(url, excludedSites) {
  const host = hostOf(url);
  if (!host) return false;
  return excludedSites.split(/\s+/).map((s) => s.trim().toLowerCase().replace(/^\*?\./, '')).filter(Boolean)
    .some((d) => host === d || host.endsWith('.' + d));
}

async function cookiesFor(urls, settings) {
  if (!settings.sendCookies) return undefined;
  const seen = new Map();
  for (const url of urls) {
    if (!isHttp(url)) continue;
    try {
      const list = await chrome.cookies.getAll({ url });
      for (const c of list) {
        const key = c.name + '|' + c.domain + '|' + c.path;
        if (!seen.has(key)) {
          seen.set(key, { name: c.name, value: c.value, domain: c.domain, path: c.path, secure: c.secure, hostOnly: c.hostOnly, httpOnly: c.httpOnly });
        }
      }
    } catch { /* no access to this host */ }
  }
  const out = [...seen.values()].slice(0, 300);
  return out.length ? out : undefined;
}

async function buildItem(url, extra, settings) {
  const item = { url };
  if (extra.filename) item.filename = extra.filename;
  if (isHttp(extra.referrer)) item.referrer = extra.referrer;
  if (isHttp(extra.pageUrl)) item.pageUrl = extra.pageUrl;
  if (extra.size > 0) item.size = extra.size;
  if (extra.mime) item.mime = String(extra.mime).slice(0, 200);
  const cookies = extra.incognito ? undefined : await cookiesFor([url, ...(extra.cookieUrls || [])], settings);
  if (cookies) item.cookies = cookies;
  return item;
}

async function sendDownloads(items, interactive, source) {
  return sendNative({ type: 'download', items, interactive, source, userAgent: navigator.userAgent });
}

// ---------------- download capture ----------------

async function shouldCapture(item, settings) {
  if (!settings.capture) return false;
  if (item.incognito) return false;
  if (item.byExtensionId) return false; // started by an extension (including us)
  const url = item.finalUrl || item.url;
  if (!isHttp(url)) return false; // blob:, data:, file:, ftp: stay in the browser
  if (isExcluded(url, settings.excludedSites) || isExcluded(item.referrer || '', settings.excludedSites)) return false;
  const exts = settings.extensions.toLowerCase().split(/[\s,;]+/).map((e) => e.replace(/^\./, '')).filter(Boolean);
  const ext = extOf(item.filename) || extOf(url);
  if (exts.length && !exts.includes(ext)) return false;
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes;
  if (settings.minSizeKB > 0 && size > 0 && size < settings.minSizeKB * 1024) return false;
  return true;
}

async function handleDetermining(item, suggest) {
  let settled = false;
  const keepInBrowser = () => {
    if (settled) return;
    settled = true;
    try { suggest(); } catch { /* already decided */ }
  };
  try {
    const settings = await getSettings();
    if (settings.skipNext) {
      await chrome.storage.local.set({ skipNext: false });
      updateIcon();
      return keepInBrowser();
    }
    if (!(await shouldCapture(item, settings))) return keepInBrowser();

    const url = item.finalUrl || item.url;
    const entry = await buildItem(url, {
      filename: baseName(item.filename),
      referrer: item.referrer,
      size: item.fileSize > 0 ? item.fileSize : item.totalBytes,
      mime: item.mime,
      cookieUrls: item.url !== url ? [item.url] : [],
      incognito: item.incognito,
    }, settings);
    const resp = await sendDownloads([entry], false, 'capture');
    if (resp && resp.ok === true) {
      settled = true;
      try { await chrome.downloads.cancel(item.id); } catch { /* finished already */ }
      try { await chrome.downloads.erase({ id: item.id }); } catch { /* ignore */ }
      return;
    }
    keepInBrowser();
  } catch {
    keepInBrowser();
  }
}

// Firefox has no onDeterminingFilename: there the download has already started when we hear of it. It is paused
// while PDM is asked, then cancelled and removed from the list only after PDM took it; otherwise it continues.
async function handleCreated(item) {
  let paused = false;
  try {
    if (item.state && item.state !== 'in_progress') return;
    const settings = await getSettings();
    if (settings.skipNext) {
      await chrome.storage.local.set({ skipNext: false });
      updateIcon();
      return;
    }
    if (!(await shouldCapture(item, settings))) return;
    try { await chrome.downloads.pause(item.id); paused = true; } catch { /* not pausable: it keeps going meanwhile */ }
    const url = item.finalUrl || item.url;
    const entry = await buildItem(url, {
      filename: baseName(item.filename),
      referrer: item.referrer,
      size: item.fileSize > 0 ? item.fileSize : item.totalBytes,
      mime: item.mime,
      cookieUrls: item.url !== url ? [item.url] : [],
      incognito: item.incognito,
    }, settings);
    const resp = await sendDownloads([entry], false, 'capture');
    if (resp && resp.ok === true) {
      paused = false;
      try { await chrome.downloads.cancel(item.id); } catch { /* finished already */ }
      try { await chrome.downloads.erase({ id: item.id }); } catch { /* ignore */ }
    }
  } catch { /* keep it in the browser */ } finally {
    if (paused) { try { await chrome.downloads.resume(item.id); } catch { /* ignore */ } }
  }
}

if (chrome.downloads.onDeterminingFilename) {
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    handleDetermining(item, suggest);
    return true; // we call suggest() asynchronously (or cancel the download)
  });
} else {
  chrome.downloads.onCreated.addListener((item) => { handleCreated(item); });
}

// ---------------- media detection ----------------

const MEDIA_TYPES = /^(video\/|audio\/|application\/(vnd\.apple\.mpegurl|x-mpegurl|dash\+xml))/i;
const MEDIA_EXT = new Set(['mp4', 'webm', 'mkv', 'mov', 'm4v', 'flv', 'mp3', 'm4a', 'ogg', 'opus', 'wav', 'flac', 'aac', 'm3u8', 'mpd']);
const IGNORE_EXT = new Set(['ts', 'm4s']); // stream fragments
const IGNORE_HOSTS = [/(^|\.)googlevideo\.com$/i]; // YouTube chunks: handled by yt-dlp via the page URL

function header(headers, name) {
  const h = (headers || []).find((x) => x.name.toLowerCase() === name);
  return h ? h.value : '';
}

// In-memory copy of what was already stored, so repeated requests (players re-fetch the same file
// many times) never touch storage. Lost when the service worker sleeps; storage dedupes then.
const seenMedia = new Map(); // tabId -> Set(url)

// Each tab's list is read, changed and written back; two media requests arriving together would each write
// their own copy and one would be lost, so the updates of a tab run one after another.
const mediaQueue = new Map(); // tabId -> Promise of the last update

function addMedia(tabId, media) {
  let seen = seenMedia.get(tabId);
  if (!seen) seenMedia.set(tabId, (seen = new Set()));
  if (seen.has(media.url)) return Promise.resolve();
  seen.add(media.url);
  const next = (mediaQueue.get(tabId) || Promise.resolve()).then(() => storeMedia(tabId, media)).catch(() => {});
  mediaQueue.set(tabId, next);
  next.then(() => { if (mediaQueue.get(tabId) === next) mediaQueue.delete(tabId); });
  return next;
}

async function storeMedia(tabId, media) {
  const key = 'media:' + tabId;
  const cur = (await chrome.storage.session.get(key))[key] || [];
  if (cur.some((m) => m.url === media.url)) return;
  cur.push(media);
  while (cur.length > 50) cur.shift();
  await chrome.storage.session.set({ [key]: cur });
  chrome.action.setBadgeBackgroundColor({ tabId, color: '#1A73E8' }).catch(() => {});
  chrome.action.setBadgeText({ tabId, text: String(cur.length) }).catch(() => {});
}

async function getMedia(tabId) {
  const key = 'media:' + tabId;
  return (await chrome.storage.session.get(key))[key] || [];
}

async function clearMedia(tabId) {
  seenMedia.delete(tabId);
  await chrome.storage.session.remove('media:' + tabId);
  chrome.action.setBadgeText({ tabId, text: '' }).catch(() => {});
}

function onMediaHeaders(d) {
  // Cheapest checks first: this runs for every media load (and media-looking XHR) in every tab.
  if (d.tabId < 0 || d.statusCode >= 400 || !isHttp(d.url)) return;
  const seen = seenMedia.get(d.tabId);
  if (seen && seen.has(d.url)) return;
  const ext = extOf(d.url);
  if (IGNORE_EXT.has(ext)) return;
  const host = hostOf(d.url);
  if (IGNORE_HOSTS.some((r) => r.test(host))) return;
  const type = header(d.responseHeaders, 'content-type').split(';')[0].trim();
  if (!MEDIA_TYPES.test(type) && !MEDIA_EXT.has(ext)) return;
  if (type.startsWith('text/') || type.startsWith('image/')) return;
  let size = -1;
  const range = header(d.responseHeaders, 'content-range');
  const m = /\/(\d+)\s*$/.exec(range);
  if (m) size = Number(m[1]);
  else if (d.statusCode === 200) size = Number(header(d.responseHeaders, 'content-length')) || -1;
  if (size > 0 && size < 100 * 1024 && ext !== 'm3u8' && ext !== 'mpd') return; // tiny: ads, previews
  addMedia(d.tabId, { url: d.url, mime: type || undefined, size, label: baseName(new URL(d.url).pathname) || undefined }).catch(() => {});
}

// <video>/<audio>/<object> loads are always looked at. XHR/fetch only when the address looks like media
// (players fetch .m3u8/.mp4 that way), so ordinary page and API requests never wake this background script.
const MEDIA_URL_PATTERNS = ['m3u8', 'mpd', 'mp4', 'm4v', 'webm', 'mkv', 'mov', 'flv', 'mp3', 'm4a', 'ogg', 'opus', 'wav', 'flac', 'aac']
  .map((e) => `*://*/*.${e}*`);
chrome.webRequest.onHeadersReceived.addListener(onMediaHeaders, { urls: ['<all_urls>'], types: ['media', 'object'] }, ['responseHeaders']);
chrome.webRequest.onHeadersReceived.addListener(onMediaHeaders, { urls: MEDIA_URL_PATTERNS, types: ['xmlhttprequest', 'other'] }, ['responseHeaders']);

chrome.tabs.onUpdated.addListener((tabId, info) => {
  if (info.status === 'loading' && info.url) clearMedia(tabId).catch(() => {});
});
chrome.tabs.onRemoved.addListener((tabId) => { seenMedia.delete(tabId); chrome.storage.session.remove('media:' + tabId).catch(() => {}); });

// Rough "same site" check (last two host labels), enough to keep a page from pointing us at another site.
function sameSite(a, b) {
  const site = (u) => hostOf(u).split('.').slice(-2).join('.');
  return !!site(a) && site(a) === site(b);
}

// frameUrl: URL of an embedded player frame (from the browser's sender info, never from the page).
// permalink: the post a feed video belongs to (found by the content script; accepted only on the same site).
async function sendVideo(tab, src, frameUrl, permalink) {
  const settings = await getSettings();
  const media = (await getMedia(tab.id)).slice();
  if (isHttp(src) && !media.some((m) => m.url === src)) media.unshift({ url: src, size: -1, label: baseName(new URL(src).pathname) || undefined });
  const embedded = isHttp(frameUrl) && frameUrl !== tab.url;
  // yt-dlp gets the player's own page (it knows most embed URLs); the top page is sent as Referer.
  const msg = { type: 'video', pageUrl: embedded ? frameUrl : tab.url, title: tab.title, media, userAgent: navigator.userAgent };
  if (embedded && isHttp(tab.url)) msg.referrer = tab.url;
  if (isHttp(permalink) && permalink !== msg.pageUrl && sameSite(permalink, msg.pageUrl)) {
    if (!msg.referrer && isHttp(tab.url)) msg.referrer = tab.url;
    msg.pageUrl = permalink;
    delete msg.title; // the tab title is the feed's; yt-dlp supplies the video's own title
  }
  if (!tab.incognito) {
    const cookies = await cookiesFor([msg.pageUrl, ...media.map((m) => m.url).slice(0, 5)], settings);
    if (cookies) msg.cookies = cookies;
  }
  return sendNative(msg);
}

// ---------------- context menus ----------------

function createMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'pdm-link', title: t('menuLink'), contexts: ['link'] });
    chrome.contextMenus.create({ id: 'pdm-media', title: t('menuMedia'), contexts: ['image', 'video', 'audio'] });
    chrome.contextMenus.create({ id: 'pdm-selection', title: t('menuSelection'), contexts: ['selection'] });
    chrome.contextMenus.create({ id: 'pdm-all', title: t('menuAll'), contexts: ['page'] });
    chrome.contextMenus.create({ id: 'pdm-video', title: t('menuVideo'), contexts: ['page', 'video'] });
  });
}

// On browser start, ask a running PDM once (does not start PDM) so an update is picked up without a download.
const pingOnce = () => sendNative({ type: 'ping' }, 5000).catch(() => {});
chrome.runtime.onInstalled.addListener(() => { createMenus(); updateIcon(); });
chrome.runtime.onStartup.addListener(() => { createMenus(); updateIcon(); pingOnce(); });

// Asks one frame only (content scripts run in every frame).
// Link lists of the "all links" page live in session storage (gone when the browser closes); keep the newest few
// so a reloaded list page still finds its links without filling the storage.
async function pruneGrabberLists() {
  const all = await chrome.storage.session.get(null);
  const old = Object.keys(all).filter((k) => k.startsWith('grabber:')).sort((a, b) => (all[b].at || 0) - (all[a].at || 0)).slice(4);
  if (old.length) await chrome.storage.session.remove(old);
}

async function askTab(tabId, message, frameId) {
  try { return await chrome.tabs.sendMessage(tabId, message, { frameId: frameId || 0 }); } catch { return null; }
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (!tab) return;
  const settings = await getSettings();
  const incognito = !!tab.incognito;
  try {
    if (info.menuItemId === 'pdm-link' || info.menuItemId === 'pdm-media') {
      const url = info.menuItemId === 'pdm-link' ? info.linkUrl : info.srcUrl;
      if (!isHttp(url)) return;
      const item = await buildItem(url, { referrer: info.pageUrl, pageUrl: info.pageUrl, incognito }, settings);
      await sendDownloads([item], true, 'menu');
    } else if (info.menuItemId === 'pdm-selection') {
      const links = (await askTab(tab.id, { type: 'selectionLinks' }, info.frameId)) || [];
      const urls = [...new Set(links.map((l) => l.url).filter(isHttp))].slice(0, 500);
      if (!urls.length) return;
      const items = [];
      for (const u of urls) items.push(await buildItem(u, { referrer: info.pageUrl, pageUrl: info.pageUrl, incognito }, settings));
      await sendDownloads(items, true, 'selection');
    } else if (info.menuItemId === 'pdm-all') {
      const links = (await askTab(tab.id, { type: 'collectLinks' }, info.frameId)) || [];
      // One entry per list page, so two lists opened quickly one after another each keep their own links.
      const id = crypto.randomUUID();
      await pruneGrabberLists();
      await chrome.storage.session.set({ ['grabber:' + id]: { at: Date.now(), pageUrl: tab.url, title: tab.title, incognito, links: links.slice(0, 2000) } });
      await chrome.tabs.create({ url: chrome.runtime.getURL('grabber.html') + '#' + id, index: tab.index + 1 });
    } else if (info.menuItemId === 'pdm-video') {
      await sendVideo(tab, info.srcUrl, info.frameId ? info.frameUrl : null);
    }
  } catch (e) {
    console.warn('PDM:', e && e.message);
  }
});

// ---------------- messages from our pages / content script ----------------

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || !msg || typeof msg.type !== 'string') return false;
  // Content scripts (web pages) may only press the video button; everything else comes from our own pages.
  const fromOwnPage = typeof sender.url === 'string' && sender.url.startsWith(chrome.runtime.getURL(''));
  if (!fromOwnPage && msg.type !== 'videoButton') return false;
  (async () => {
    switch (msg.type) {
      case 'videoButton': {
        if (!sender.tab) return { ok: false };
        return await sendVideo(sender.tab, msg.src, sender.frameId ? sender.url : null, typeof msg.permalink === 'string' ? msg.permalink : null);
      }
      case 'ping':
        return await sendNative({ type: 'ping' }, 5000);
      case 'show':
        return await sendNative({ type: 'show' });
      case 'getMedia':
        return await getMedia(msg.tabId);
      case 'downloadMedia': {
        const settings = await getSettings();
        const tab = await chrome.tabs.get(msg.tabId);
        const item = await buildItem(msg.url, { referrer: tab.url, pageUrl: tab.url, size: msg.size, mime: msg.mime, incognito: tab.incognito }, settings);
        return await sendDownloads([item], true, 'popup');
      }
      case 'videoPage': {
        const tab = await chrome.tabs.get(msg.tabId);
        return await sendVideo(tab, null);
      }
      case 'sendLinks': {
        const settings = await getSettings();
        const urls = (Array.isArray(msg.urls) ? msg.urls : []).filter(isHttp).slice(0, 500);
        if (!urls.length) return { ok: false };
        const items = [];
        for (const u of urls) items.push(await buildItem(u, { referrer: msg.pageUrl, pageUrl: msg.pageUrl, incognito: !!msg.incognito }, settings));
        return await sendDownloads(items, true, 'grabber');
      }
      case 'settingsChanged':
        updateIcon();
        return { ok: true };
      default:
        return { ok: false };
    }
  })().then(sendResponse, (e) => sendResponse({ ok: false, error: (e && e.message) || 'error' }));
  return true;
});

// ---------------- icon ----------------

async function updateIcon() {
  const s = await getSettings();
  const on = s.capture && !s.skipNext;
  const sfx = on ? '' : '-off';
  chrome.action.setIcon({ path: { 16: `icons/icon16${sfx}.png`, 32: `icons/icon32${sfx}.png`, 48: `icons/icon48${sfx}.png` } }).catch(() => {});
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && (changes.capture || changes.skipNext)) updateIcon();
});
