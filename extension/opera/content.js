// PDM content script: a small "Download with PDM" tab attached to the top-right corner of a video
// (just above it, like IDM's panel), and link collection on request. Runs in every frame so that
// embedded players work too.
// Light by design: no timers while idle, mouse moves are handled at most once per frame, and the
// page's videos are only looked at when the page has any.
// The button lives in a closed shadow root so the page cannot read or restyle it, and only real
// (isTrusted) clicks count.
(() => {
  'use strict';
  if (window.__pdmLoaded) return;
  window.__pdmLoaded = true;

  const MIN_W = 200;
  const MIN_H = 120;
  const videos = document.getElementsByTagName('video'); // live list, costs nothing to keep
  let enabled = true;
  let current = null;
  let hideTimer = 0;
  let framePending = false;
  let mouseX = 0;
  let mouseY = 0;

  chrome.storage.local.get({ videoButton: true }, (s) => { enabled = !!s.videoButton; });
  chrome.storage.onChanged.addListener((c, area) => {
    if (area === 'local' && c.videoButton) { enabled = !!c.videoButton.newValue; if (!enabled) hide(); }
  });

  // Videos whose button the user closed (x): hidden until the page reloads or the video shows another page.
  const dismissed = new WeakMap(); // video -> location.href at the time
  const isDismissed = (v) => dismissed.has(v) && dismissed.get(v) === location.href;

  // ---- button (created on first use) ----
  let host = null;
  let bar = null;
  let button = null;

  function build() {
    host = document.createElement('pdm-video-button');
    const root = host.attachShadow({ mode: 'closed' });
    const style = document.createElement('style');
    style.textContent = `
      :host { all: initial; }
      .bar {
        all: initial; position: fixed; z-index: 2147483647; display: none; box-sizing: border-box; overflow: hidden;
        font: 600 12px/18px "Segoe UI", system-ui, sans-serif; white-space: nowrap; user-select: none;
      }
      .bar.above { border-radius: 5px 5px 0 0; }
      .bar.inside { border-radius: 0 0 0 5px; }
      button { all: initial; cursor: pointer; font: inherit; color: #fff; background: #1a73e8; padding: 2px 8px 2px 10px; display: inline-block; }
      button.close { padding: 2px 7px; border-left: 1px solid rgba(255,255,255,.35); font-weight: 400; }
      button:hover { background: #1558b0; }
      button.sent { background: #188038; }`;
    bar = document.createElement('div');
    bar.className = 'bar';
    button = document.createElement('button');
    button.textContent = '\u2B07 ' + (chrome.i18n.getMessage('videoButton') || 'PDM');
    const close = document.createElement('button');
    close.className = 'close';
    close.textContent = '\u00D7';
    close.title = chrome.i18n.getMessage('videoButtonClose') || '';
    bar.append(button, close);
    root.append(style, bar);
    bar.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    bar.addEventListener('mouseleave', scheduleHide);
    bar.addEventListener('mousedown', (e) => e.stopPropagation(), true);
    button.addEventListener('click', onClick, true);
    close.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (!e.isTrusted) return;
      if (current) dismissed.set(current, location.href);
      hide();
    }, true);
  }

  function attach() {
    if (!host) build();
    const parent = document.fullscreenElement && document.fullscreenElement !== document.documentElement
      ? document.fullscreenElement
      : (document.documentElement || document.body);
    if (host.parentNode !== parent) parent.appendChild(host);
  }

  function place(video) {
    const r = video.getBoundingClientRect();
    if (r.width < MIN_W || r.height < MIN_H || r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) { hide(); return; }
    attach();
    bar.style.display = 'block';
    const bw = bar.offsetWidth;
    const bh = bar.offsetHeight;
    // Sits on top of the video's top edge, right-aligned; inside the corner only when there is no room above.
    const above = r.top - bh >= 0;
    bar.className = 'bar ' + (above ? 'above' : 'inside');
    bar.style.top = (above ? r.top - bh : Math.max(0, r.top)) + 'px';
    bar.style.left = Math.max(0, Math.min(innerWidth - bw, Math.min(r.right, innerWidth) - bw)) + 'px';
  }

  function show(video) {
    if (isDismissed(video)) { if (current === video) hide(); return; }
    clearTimeout(hideTimer);
    if (current !== video) current = video;
    place(video);
  }

  function hide() {
    current = null;
    if (bar) bar.style.display = 'none';
  }

  function scheduleHide() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => { if (!bar || !bar.matches(':hover')) hide(); }, 1200);
  }

  function videoAt(x, y) {
    for (let i = 0; i < videos.length; i++) {
      const r = videos[i].getBoundingClientRect();
      if (r.width >= MIN_W && r.height >= MIN_H && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) return videos[i];
    }
    return null;
  }

  function onFrame() {
    framePending = false;
    const v = videoAt(mouseX, mouseY);
    if (v) show(v);
    else if (current) scheduleHide();
  }

  // Overlays often sit above the <video>, so test by position rather than by event target.
  document.addEventListener('mousemove', (e) => {
    if (!enabled || (videos.length === 0 && !current)) return;
    mouseX = e.clientX;
    mouseY = e.clientY;
    if (!framePending) { framePending = true; requestAnimationFrame(onFrame); }
  }, { passive: true, capture: true });

  const follow = () => { if (current && !framePending) { framePending = true; requestAnimationFrame(() => { framePending = false; if (current) place(current); }); } };
  addEventListener('scroll', follow, { passive: true, capture: true });
  addEventListener('resize', follow, { passive: true });
  document.addEventListener('fullscreenchange', () => { if (host) { attach(); follow(); } });

  function onClick(e) {
    e.preventDefault();
    e.stopPropagation();
    if (!e.isTrusted) return;
    const src = current && (current.currentSrc || current.src);
    chrome.runtime.sendMessage({
      type: 'videoButton',
      src: /^https?:/i.test(src || '') ? src : null,
      permalink: current ? permalinkFor(current) : null,
    }).catch(() => {});
    button.classList.add('sent');
    setTimeout(() => button.classList.remove('sent'), 1200);
  }

  // ---- the page that belongs to one video ----
  // In feeds (X, Instagram, Facebook, Reddit, TikTok, YouTube home) the tab shows many videos; yt-dlp needs the
  // post of this one. Returns null when the current page is already the right one.
  function permalinkFor(v) {
    const host = location.hostname.replace(/^www\./, '');
    const abs = (a) => { try { return a ? new URL(a.getAttribute('href'), location.href).href : null; } catch { return null; } };
    const inSite = (re) => re.test(host);
    try {
      if (inSite(/(^|\.)(x|twitter)\.com$/)) {
        // A quoted post sits in a role="link" box inside the outer post.
        const quoted = v.closest('[role="link"]');
        const scope = quoted && quoted.querySelector('a[href*="/status/"]') ? quoted : v.closest('article');
        if (!scope) return null;
        const time = scope.querySelector('a[href*="/status/"] time');
        const url = abs(time ? time.closest('a') : scope.querySelector('a[href*="/status/"]'));
        return url ? url.replace(/\/(photo|video|analytics)(\/\d+)?\/?$/, '') : null;
      }
      if (inSite(/(^|\.)instagram\.com$/)) {
        if (/^\/(p|reel|reels|tv)\//.test(location.pathname)) return null;
        const art = v.closest('article');
        return abs(art && art.querySelector('a[href*="/p/"], a[href*="/reel/"]'));
      }
      if (inSite(/(^|\.)facebook\.com$/)) {
        const post = v.closest('[role="article"]');
        return abs(post && post.querySelector('a[href*="/videos/"], a[href*="/reel/"], a[href*="/watch/?v="], a[href*="/watch?v="]'));
      }
      if (inSite(/(^|\.)reddit\.com$/)) {
        const post = v.closest('shreddit-post');
        const p = post && post.getAttribute('permalink');
        return p ? new URL(p, location.href).href : null;
      }
      if (inSite(/(^|\.)tiktok\.com$/)) {
        if (/\/video\/\d+/.test(location.pathname)) return null;
        const item = v.closest('article, [data-e2e="recommend-list-item-container"]');
        return abs(item && item.querySelector('a[href*="/video/"]'));
      }
      if (inSite(/(^|\.)youtube\.com$/)) {
        // Hover previews play in a separate ytd-video-preview element that links to the video.
        const card = v.closest('ytd-video-preview, ytd-rich-item-renderer, ytd-video-renderer, ytd-compact-video-renderer, ytd-reel-item-renderer, ytd-grid-video-renderer');
        return abs(card && card.querySelector('a[href*="/watch?v="], a[href*="/shorts/"]'));
      }
    } catch { /* page structure changed: fall back to the page URL */ }
    return null;
  }

  // ---- link collection (asked by the context menu, only in the frame that was clicked) ----
  function linkInfo(a) {
    const url = a.href;
    if (!/^https?:/i.test(url)) return null;
    return { url, text: (a.textContent || a.title || '').trim().slice(0, 200) };
  }

  function allLinks() {
    const seen = new Set();
    const out = [];
    const add = (info) => { if (info && !seen.has(info.url)) { seen.add(info.url); out.push(info); } };
    document.querySelectorAll('a[href]').forEach((a) => add(linkInfo(a)));
    document.querySelectorAll('video[src], audio[src], source[src], img[src]').forEach((el) => {
      const url = el.src;
      if (/^https?:/i.test(url)) add({ url, text: el.alt || el.title || '' });
    });
    return out.slice(0, 2000);
  }

  function selectionLinks() {
    const sel = getSelection();
    const out = [];
    const seen = new Set();
    if (!sel || sel.rangeCount === 0) return out;
    for (let i = 0; i < sel.rangeCount; i++) {
      const range = sel.getRangeAt(i);
      const container = range.commonAncestorContainer.nodeType === 1 ? range.commonAncestorContainer : range.commonAncestorContainer.parentElement;
      if (!container) continue;
      const anchors = container.tagName === 'A' ? [container] : [...container.querySelectorAll('a[href]')];
      const parentA = container.closest && container.closest('a[href]');
      if (parentA) anchors.push(parentA);
      for (const a of anchors) {
        if (!sel.containsNode(a, true)) continue;
        const info = linkInfo(a);
        if (info && !seen.has(info.url)) { seen.add(info.url); out.push(info); }
      }
    }
    for (const m of String(sel).matchAll(/https?:\/\/[^\s"'<>]+/gi)) {
      const url = m[0].replace(/[.,);]+$/, '');
      if (!seen.has(url)) { seen.add(url); out.push({ url, text: '' }); }
    }
    return out.slice(0, 500);
  }

  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (sender.id !== chrome.runtime.id || !msg) return false;
    if (msg.type === 'collectLinks') sendResponse(allLinks());
    else if (msg.type === 'selectionLinks') sendResponse(selectionLinks());
    return false;
  });
})();
