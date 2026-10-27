'use strict';
const $ = (id) => document.getElementById(id);
// Keep in sync with DEFAULTS in background.js.
const DEFAULTS = {
  capture: true,
  extensions: 'zip rar 7z tar gz tgz bz2 xz zst iso img exe msi msix apk dmg deb rpm pdf epub doc docx xls xlsx ppt pptx odt mp3 m4a flac wav ogg opus aac mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts torrent bin',
  minSizeKB: 0,
  excludedSites: '',
  videoButton: true,
  sendCookies: true,
};

function fill(s) {
  $('capture').checked = s.capture;
  $('extensions').value = s.extensions;
  $('minSizeKB').value = s.minSizeKB;
  $('excludedSites').value = s.excludedSites;
  $('videoButton').checked = s.videoButton;
  $('sendCookies').checked = s.sendCookies;
}

chrome.storage.local.get(DEFAULTS, fill);

$('save').addEventListener('click', async () => {
  const exts = $('extensions').value.toLowerCase().split(/[\s,;]+/).map((e) => e.replace(/^\./, '')).filter((e) => /^[a-z0-9]{1,10}$/.test(e));
  const sites = $('excludedSites').value.split(/\s+/).map((s) => s.trim().toLowerCase()).filter((s) => /^[a-z0-9.*-]+$/.test(s));
  await chrome.storage.local.set({
    capture: $('capture').checked,
    extensions: [...new Set(exts)].join(' '),
    minSizeKB: Math.max(0, Math.floor(Number($('minSizeKB').value) || 0)),
    excludedSites: sites.join('\n'),
    videoButton: $('videoButton').checked,
    sendCookies: $('sendCookies').checked,
  });
  chrome.storage.local.get(DEFAULTS, fill);
  chrome.runtime.sendMessage({ type: 'settingsChanged' }).catch(() => {});
  $('saved').textContent = chrome.i18n.getMessage('optSaved');
  setTimeout(() => { $('saved').textContent = ''; }, 2000);
});

$('reset').addEventListener('click', () => fill(DEFAULTS));
