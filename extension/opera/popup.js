'use strict';
const msg = (k, ...a) => chrome.i18n.getMessage(k, a.map(String)) || k;
const $ = (id) => document.getElementById(id);
const send = (m) => chrome.runtime.sendMessage(m);

function sizeText(n) {
  if (!(n > 0)) return '';
  const u = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return n.toFixed(i ? 1 : 0) + ' ' + u[i];
}

function showStatus(resp) {
  const el = $('status');
  if (resp && resp.ok) {
    el.textContent = msg('popupConnected') + (resp.version ? ' · v' + resp.version : '');
    el.className = 'good';
    const mine = chrome.runtime.getManifest().version;
    if (typeof resp.extVersion === 'string' && resp.extVersion !== mine) {
      // The background reloads once per new version; if we are still old, the extension was loaded from elsewhere.
      chrome.storage.local.get('reloadTried', ({ reloadTried }) => {
        if (reloadTried !== resp.extVersion) return;
        const warn = document.createElement('div');
        warn.className = 'bad';
        warn.style.marginTop = '6px';
        warn.textContent = msg('popupExtOutdated', mine, resp.extVersion);
        el.appendChild(warn);
      });
    }
  } else {
    const err = resp && resp.error;
    const notInstalled = /not found|not-installed|Specified native messaging host/i.test(err || '');
    el.textContent = notInstalled ? msg('popupNotInstalled') : msg('popupNotRunning');
    el.className = notInstalled ? 'bad' : 'muted';
  }
}

async function init() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const s = await chrome.storage.local.get({ capture: true, skipNext: false });
  $('capture').checked = s.capture;
  const updateSkip = (on) => { $('skip').textContent = on ? msg('popupSkipNextOn') : msg('popupSkipNext'); $('skip').disabled = on; };
  updateSkip(s.skipNext);
  $('capture').addEventListener('change', async () => {
    await chrome.storage.local.set({ capture: $('capture').checked });
    send({ type: 'settingsChanged' });
  });
  $('skip').addEventListener('click', async () => {
    await chrome.storage.local.set({ skipNext: true });
    updateSkip(true);
    send({ type: 'settingsChanged' });
  });
  $('open').addEventListener('click', async () => { await send({ type: 'show' }).catch(() => {}); window.close(); });
  $('options').addEventListener('click', () => chrome.runtime.openOptionsPage());

  const web = tab && /^https?:/i.test(tab.url || '');
  $('videoPage').disabled = !web;
  $('videoPage').addEventListener('click', async () => {
    const r = await send({ type: 'videoPage', tabId: tab.id }).catch(() => null);
    if (r && r.ok) window.close(); else showStatus(r);
  });

  const media = tab ? (await send({ type: 'getMedia', tabId: tab.id }).catch(() => [])) || [] : [];
  $('nomedia').style.display = media.length ? 'none' : 'block';
  for (const m of media) {
    const li = document.createElement('li');
    const name = document.createElement('div');
    name.className = 'grow ellipsis';
    name.textContent = m.label || m.url;
    name.title = m.url.split('?')[0];
    const info = document.createElement('span');
    info.className = 'muted';
    info.textContent = [m.mime, sizeText(m.size)].filter(Boolean).join(' · ');
    const b = document.createElement('button');
    b.textContent = '⬇';
    b.title = msg('menuLink');
    b.addEventListener('click', async () => {
      const r = await send({ type: 'downloadMedia', tabId: tab.id, url: m.url, size: m.size, mime: m.mime }).catch(() => null);
      if (r && r.ok) { b.textContent = '✓'; b.disabled = true; } else showStatus(r);
    });
    li.append(name, info, b);
    $('media').appendChild(li);
  }

  showStatus(await send({ type: 'ping' }).catch((e) => ({ ok: false, error: e && e.message })));
}

init();
