(async (opts) => {
  window.__prefillResult = undefined;
  const t0 = performance.now();
  const timing = {};
  const mark = (step) => { timing[step] = Math.round(performance.now() - t0); };
  const pageTiming = () => {
    const nav = performance.getEntriesByType('navigation')[0];
    return nav ? { dom: Math.round(nav.domContentLoadedEventEnd), load: Math.round(nav.loadEventEnd) } : null;
  };
  const post = (msg) => {
    mark('done');
    msg.timing = { script: timing, page: pageTiming() };
    window.__prefillResult = msg;
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg);
  };
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const visible = (el) => !!el && (el.offsetParent !== null || el.getClientRects().length > 0);
  // Polls every 50 ms: each step goes on as soon as the page is ready for it.
  const waitFor = async (fn, ms = 6000) => {
    const end = Date.now() + ms;
    for (;;) {
      const v = fn();
      if (v || Date.now() >= end) return v || null;
      await sleep(50);
    }
  };

  try {
    const STATUSES = { played: 'Played', completed: 'Completed', retired: 'Retired', shelved: 'Shelved', abandoned: 'Abandoned' };
    if (!opts || (opts.status != null && !Object.prototype.hasOwnProperty.call(STATUSES, opts.status))) {
      post({ type: 'prefill', missing: ['status'] });
      return;
    }

    const safeOpener = (el) => !el.closest('form') && el.id !== 'game-status-selector';
    const findOpener = () => {
      for (const s of ['button.log-editor-btn', '#open-new-game-log-modal-btn']) {
        const el = [...document.querySelectorAll(s)].find((b) => visible(b) && safeOpener(b));
        if (el) return el;
      }
      return [...document.querySelectorAll('button')].find((b) =>
        visible(b) && safeOpener(b) && /log or review/i.test(b.textContent)) || null;
    };
    const findModal = () =>
      [...document.querySelectorAll('#journal-game-modal.show, .modal.show')]
        .find((m) => m.querySelector('input#status') && m.querySelector('button.save-log'));
    // Backloggd has three log boxes (cookie game-log-editor-mode, or "mobile" under 950 px). Only the quick one has
    // a form; the others keep the same field ids and Save button and carry the game id on .game-id-container.
    const gameIdOf = (modal, opener) => {
      const form = modal.querySelector('form[action*="/log/"]');
      const ids = form ? (form.getAttribute('action') || '').match(/\d+/g) : null;
      if (ids && ids.length) return ids[ids.length - 1];
      const holder = modal.querySelector('.game-id-container[game_id]');
      const id = (holder && holder.getAttribute('game_id')) || (opener && opener.getAttribute('game_id')) || '';
      return /^\d+$/.test(id) ? id : null;
    };
    const modalOpening = () => document.body.classList.contains('modal-open') || !!document.querySelector('.modal-backdrop');

    const signedIn = await waitFor(() => document.querySelector('a[href^="/users/sign_out"]') || findOpener(), 4000);
    if (!signedIn) {
      post({ type: 'needLogin' });
      return;
    }
    mark('signedIn');

    const missing = [];

    const opener = await waitFor(findOpener, 6000);
    if (!opener) {
      post({ type: 'prefill', missing: ['log button'] });
      return;
    }
    mark('button');

    // The page's scripts may not have bound the button yet, so a click that opened nothing is repeated. A box
    // that is opening (backdrop shown) is never clicked again: a second click would close it.
    let modal = null;
    for (let attempt = 0; attempt < 3 && !modal; attempt++) {
      if (attempt > 0 && modalOpening()) {
        modal = await waitFor(findModal, 8000);
        break;
      }
      (findOpener() || opener).click();
      modal = await waitFor(findModal, attempt < 2 ? 2500 : 6000);
    }
    if (!modal) {
      post({ type: 'prefill', missing: ['log box'] });
      return;
    }
    // The box's own handlers are ready once its fade-in has finished (opacity 1).
    await waitFor(() => getComputedStyle(modal).opacity === '1', 1000);
    mark('modal');
    const gameId = gameIdOf(modal, opener);
    if (gameId && window.chrome && window.chrome.webview)
      window.chrome.webview.postMessage({ type: 'logform', gameId });

    if (opts.status) {
      const label = STATUSES[opts.status];
      const option = modal.querySelector(`.play-type-option#${opts.status}`) ||
        modal.querySelector(`.play-type-option[title="${label}"]`);
      if (!option) missing.push(`status ${label}`);
      else {
        option.click();
        const status = modal.querySelector('input#status');
        if (!status || !(await waitFor(() => status.value === opts.status, 1500))) missing.push(`status ${label}`);
      }
    }

    const toggles = [
      ['playing', '#playing_toggle_checkbox', 'Playing'],
      ['backlog', '#backlog_toggle_checkbox', 'Backlog'],
      ['wishlist', '#wishlist_toggle_checkbox', 'Wishlist'],
    ];
    for (const [key, selector, name] of toggles) {
      if (typeof opts[key] !== 'boolean') continue;
      const cb = modal.querySelector(selector);
      if (!cb) { missing.push(name); continue; }
      if (cb.checked !== opts[key]) cb.click();
      if (!(await waitFor(() => cb.checked === opts[key], 1000))) missing.push(name);
    }

    if (opts.started || opts.finished) {
      const datesBox = modal.querySelector('#log-dates-section-control');
      // The full box has no such switch: its date fields are always shown.
      if (!datesBox) {
        if (!modal.querySelector('#started-on-datepicker, #finished-on-datepicker')) missing.push('Mark your log dates');
      } else {
        if (!datesBox.checked) datesBox.click();
        await waitFor(() => datesBox.checked, 1000);
      }
    }

    const setDate = (selector, value, what, overwrite) => {
      const el = modal.querySelector(selector);
      if (!el) { missing.push(`${what} ${value}`); return; }
      if (el.value && !overwrite) return;
      el.focus();
      el.value = value;
      for (const type of ['input', 'change', 'keyup']) el.dispatchEvent(new Event(type, { bubbles: true }));
      el.blur();
      if (el.value !== value) missing.push(`${what} ${value}`);
    };
    if (opts.started) setDate('#started-on-datepicker', opts.started, 'started', false);
    if (opts.finished) setDate('#finished-on-datepicker', opts.finished, 'finished', true);

    post({ type: 'prefill', missing, gameId });
  } catch (e) {
    post({ type: 'prefill', missing: ['script error: ' + (e && e.message ? e.message : String(e))] });
  }
})(__OPTS__);
