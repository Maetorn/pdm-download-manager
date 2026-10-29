'use strict';
const $ = (id) => document.getElementById(id);
const msg = (k, ...a) => chrome.i18n.getMessage(k, a.map(String)) || k;
let data = { links: [] };
const rows = [];

function fileName(url) {
  try { return decodeURIComponent(new URL(url).pathname.split('/').pop() || '') || url; } catch { return url; }
}

function updateCount() {
  const visible = rows.filter((r) => r.li.style.display !== 'none');
  $('count').textContent = msg('grabCount', visible.filter((r) => r.cb.checked).length, rows.length);
}

function applyFilter() {
  const terms = $('filter').value.toLowerCase().split(/\s+/).filter(Boolean);
  for (const r of rows) {
    const name = fileName(r.url).toLowerCase();
    const ok = !terms.length || terms.some((t) => (t.startsWith('.') ? name.endsWith(t) : (r.url.toLowerCase().includes(t) || r.text.toLowerCase().includes(t))));
    r.li.style.display = ok ? '' : 'none';
    r.cb.checked = ok && terms.length > 0;
  }
  updateCount();
}

async function init() {
  const key = 'grabber:' + location.hash.slice(1);
  data = (await chrome.storage.session.get(key))[key] || { links: [] };
  $('page').textContent = (data.title ? data.title + ' — ' : '') + (data.pageUrl || '');
  if (!data.links.length) { $('count').textContent = msg('grabNoLinks'); $('send').disabled = true; return; }
  for (const l of data.links) {
    const li = document.createElement('li');
    const label = document.createElement('label');
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.addEventListener('change', updateCount);
    const box = document.createElement('div');
    box.className = 'grow';
    const name = document.createElement('div');
    name.className = 'ellipsis';
    name.textContent = fileName(l.url) + (l.text ? '  —  ' + l.text : '');
    const url = document.createElement('div');
    url.className = 'url ellipsis';
    url.textContent = l.url.split('?')[0];
    box.append(name, url);
    label.append(cb, box);
    li.appendChild(label);
    $('list').appendChild(li);
    rows.push({ li, cb, url: l.url, text: l.text || '' });
  }
  updateCount();
}

$('filter').addEventListener('input', applyFilter);
$('all').addEventListener('click', () => { rows.forEach((r) => { if (r.li.style.display !== 'none') r.cb.checked = true; }); updateCount(); });
$('none').addEventListener('click', () => { rows.forEach((r) => { r.cb.checked = false; }); updateCount(); });
$('send').addEventListener('click', async () => {
  const chosen = rows.filter((r) => r.cb.checked && r.li.style.display !== 'none');
  if (!chosen.length) return;
  $('send').disabled = true;
  const resp = await chrome.runtime.sendMessage({ type: 'sendLinks', urls: chosen.map((r) => r.url), pageUrl: data.pageUrl, incognito: data.incognito, storeId: data.storeId }).catch((e) => ({ ok: false, error: e && e.message }));
  $('send').disabled = false;
  if (resp && resp.ok) {
    // At most 500 links go in one send. What was really handed over is shown, and the rest stays ticked for the next press.
    const sent = typeof resp.sent === 'number' ? resp.sent : chosen.length;
    chosen.slice(0, sent).forEach((r) => { r.cb.checked = false; });
    updateCount();
    const left = chosen.length - sent;
    $('result').textContent = left > 0 ? msg('grabSentPartial', sent, left) : msg('grabSent', sent);
    $('result').className = 'good';
  } else {
    $('result').textContent = msg('errorPrefix') + ((resp && resp.error) || '?');
    $('result').className = 'bad';
  }
});

init();
