(async (opts) => {
  window.__matchResult = undefined;
  const post = (msg) => {
    window.__matchResult = msg;
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg);
  };
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const waitFor = async (fn, ms) => {
    const end = Date.now() + ms;
    while (Date.now() < end) {
      const v = fn();
      if (v) return v;
      await sleep(150);
    }
    return null;
  };
  const norm = (s) => String(s || '').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');

  try {
    const wait = opts && opts.wait > 0 ? opts.wait : 8000;
    const ready = () => {
      if (document.querySelector('.result .game-name a')) return 'results';
      const list = document.querySelector('#search-results');
      if (list && !list.querySelector('.result') && !document.querySelector('turbo-frame#pagination')) return 'empty';
      return null;
    };
    const state = await waitFor(ready, wait);
    if (state !== 'results') {
      post({ type: 'match', slug: null, empty: state === 'empty' });
      return;
    }

    const results = [...document.querySelectorAll('.result .game-name a')].map((a) => {
      const h3 = a.querySelector('h3');
      const textNode = h3 && [...h3.childNodes].find((n) => n.nodeType === Node.TEXT_NODE && n.textContent.trim());
      const title = (textNode ? textNode.textContent : (h3 ? h3.textContent : a.textContent)).trim();
      const year = ((h3 && h3.querySelector('.subtitle-text')) || { textContent: '' }).textContent.trim();
      const m = /^\/games\/([^/]+)\/?$/.exec(a.getAttribute('href') || '');
      return m ? { slug: m[1], title, year } : null;
    }).filter(Boolean);

    if (results.length === 0) {
      post({ type: 'match', slug: null });
      return;
    }

    const want = norm(opts && opts.name);
    const exact = results.find((r) => norm(r.title) === want);
    if (opts && opts.exact && !exact) {
      post({ type: 'match', slug: null });
      return;
    }
    const pick = exact || results[0];
    post({ type: 'match', slug: pick.slug, title: pick.title, year: pick.year });
  } catch (e) {
    post({ type: 'match', slug: null, error: e && e.message ? e.message : String(e) });
  }
})(__OPTS__);
