// Adds opts.add minutes to the journal session of opts.date, in two steps so the app can write the total down first:
// this script sets the session's time and posts one message; the app then calls window.__journalCommit() to press Save.
(async (opts) => {
  window.__journalStage = 'start';
  let posted = false;
  const t0 = performance.now();
  const timing = {};
  const mark = (step) => { timing[step] = Math.round(performance.now() - t0); };
  let gameId = null;
  const post = (msg) => {
    if (posted) return;
    posted = true;
    mark('done');
    msg.type = 'journal';
    msg.gameId = gameId;
    msg.timing = timing;
    window.__journalResult = msg;
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg);
  };
  const fail = (step, extra) => post(Object.assign({ ok: false, step }, extra || {}));
  const unsupported = (step) => post({ ok: false, unsupported: true, step });
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const visible = (el) => !!el && (el.offsetParent !== null || el.getClientRects().length > 0);
  const waitFor = async (fn, ms = 6000) => {
    const end = Date.now() + ms;
    for (;;) {
      const v = fn();
      if (v || Date.now() >= end) return v || null;
      await sleep(50);
    }
  };
  // Waits until sig() gives the same answer for quietMs in a row; null when it never settles.
  const settle = async (sig, quietMs, maxMs) => {
    const end = Date.now() + maxMs;
    let last = sig();
    let since = Date.now();
    for (;;) {
      await sleep(100);
      const now = sig();
      if (now !== last) { last = now; since = Date.now(); }
      if (Date.now() - since >= quietMs) return last;
      if (Date.now() >= end) return null;
    }
  };
  const setValue = (el, value) => {
    el.focus();
    el.value = value;
    for (const type of ['input', 'change', 'keyup']) el.dispatchEvent(new Event(type, { bubbles: true }));
    el.blur();
  };
  // '' reads as 0 (a session with hours and no minutes); anything not a whole number is NaN.
  const whole = (v) => {
    const s = String(v == null ? '' : v).trim();
    if (s === '') return 0;
    return /^\d+$/.test(s) ? parseInt(s, 10) : NaN;
  };
  const MONTHS = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];
  const fieldKey = (el, seen) => {
    const name = el.id || el.name || el.tagName;
    seen[name] = (seen[name] || 0) + 1;
    return name + '#' + seen[name];
  };
  const fieldValue = (el) => (el.type === 'checkbox' || el.type === 'radio' ? el.checked : el.value);
  const skipType = (el) => el.type === 'file' || el.type === 'password';

  try {
    const DATE = /^\d{4}-\d{2}-\d{2}$/;
    // Status mode (a Mark as choice): a played status and/or toggles, at least one, nothing else.
    const STATUSES = ['played', 'completed', 'retired', 'shelved', 'abandoned'];
    const statusMode = !!opts && opts.mode === 'status';
    const pick = statusMode ? opts.pick : null;
    const pickOk = !!pick && (pick.status == null || STATUSES.indexOf(pick.status) >= 0)
      && ['playing', 'backlog', 'wishlist'].every((k) => pick[k] == null || typeof pick[k] === 'boolean')
      && (pick.status != null || ['playing', 'backlog', 'wishlist'].some((k) => typeof pick[k] === 'boolean'));
    if (statusMode ? !pickOk : (!opts || typeof opts.date !== 'string' || !DATE.test(opts.date) || !Number.isInteger(opts.add) || opts.add <= 0 || opts.add > 1440
      || (opts.already != null && !Number.isInteger(opts.already)) || (opts.told != null && !(Number.isInteger(opts.told) && opts.told >= 0)))) {
      fail('options');
      return;
    }

    // A service worker could send the page's requests on its own; the app checks only what it sees.
    if (navigator.serviceWorker && navigator.serviceWorker.controller) { fail('service worker'); return; }

    // A draft the page kept for an unsaved log could be put back into the log box and saved with it.
    try {
      for (let i = 0; i < localStorage.length; i++) {
        const k = localStorage.key(i);
        if (k && k.indexOf('editor-draft') === 0) { fail('draft'); return; }
      }
    } catch (e) { /* storage blocked: nothing to restore either */ }

    const safeOpener = (el) => !el.closest('form') && el.id !== 'game-status-selector' && el.getAttribute('type') !== 'submit';
    // Whether the game has a log. The button holds both labels and hides one, so its text says nothing: 'edit' is a
    // log, 'new' none, null can't tell. A button without the two parts is read by its text.
    const logState = (b) => {
      const shown = (el) => !!el && visible(el) && !el.classList.contains('d-none');
      const fresh = b.querySelector('.log-editor-btn-new');
      const edit = b.querySelector('.log-editor-btn-edit');
      if (fresh || edit) return shown(edit) && !shown(fresh) ? 'edit' : shown(fresh) && !shown(edit) ? 'new' : null;
      return /edit/i.test(b.textContent) ? 'edit' : /log or review/i.test(b.textContent) ? 'new' : null;
    };
    const findOpener = () => [...document.querySelectorAll('button.log-editor-btn')]
      .find((b) => visible(b) && safeOpener(b) && logState(b) !== null) || null;
    const anyOpener = () => [...document.querySelectorAll('button.log-editor-btn, #open-new-game-log-modal-btn')].find(visible);
    const findModal = () =>
      [...document.querySelectorAll('#journal-game-modal.show')]
        .find((m) => m.querySelector('form[action*="/log/"]') && m.querySelector('input#status'));
    const modalOpening = () => document.body.classList.contains('modal-open') || !!document.querySelector('.modal-backdrop');

    const signedIn = await waitFor(() => document.querySelector('a[href^="/users/sign_out"]') || anyOpener(), 6000);
    if (!signedIn || !document.querySelector('a[href^="/users/sign_out"]')) { fail('signed out'); return; }
    mark('signedIn');

    // Only a game that already has a log ("Edit your logs"): saving a game without one would make one.
    const opener = await waitFor(findOpener, 6000);
    if (!opener) { fail(anyOpener() ? 'log button state' : 'log button'); return; }
    // Journal time is only ever added to a game that has a log; a Mark as choice may create one.
    const newLog = logState(opener) === 'new';
    if (newLog && !statusMode) { fail('no log'); return; }

    let modal = null;
    for (let attempt = 0; attempt < 3 && !modal; attempt++) {
      if (attempt > 0 && modalOpening()) {
        modal = await waitFor(findModal, 8000);
        break;
      }
      (findOpener() || opener).click();
      modal = await waitFor(findModal, attempt < 2 ? 3000 : 6000);
    }
    if (!modal) { fail('log box'); return; }
    await waitFor(() => getComputedStyle(modal).opacity === '1', 1500);
    mark('modal');
    const form = modal.querySelector('form[action*="/log/"]');
    const ids = form ? (form.getAttribute('action') || '').match(/\d+/g) : null;
    gameId = ids && ids.length ? ids[ids.length - 1] : null;
    if (!gameId) { fail('log form'); return; }

    // The log's values as the box shows them, named as the save request names them; the app lets the save out only
    // if it carries exactly these. null: not found on the page, and the app refuses the save.
    const inForm = (sel) => [...modal.querySelectorAll(sel)].filter((el) => !el.closest('#playthrough-modal'));
    const one = (sel) => inForm(sel)[0] || null;
    const flag = (el) => (el ? String(el.type === 'checkbox' || el.type === 'radio' ? el.checked : el.value) : null);
    // Read live 2026-11-09: hidden #status ("completed"), the rating_modal radios (0 is no rating),
    // #log_liked_checkbox (name liked), one textarea#review, and the play/playing/backlog/wishlist toggles.
    const readLog = () => {
      const status = one('input#status');
      const ratingRadio = inForm('input[type=radio][name="rating_modal"]');
      let rating = null;
      if (ratingRadio.length) {
        const on = ratingRadio.find((r) => r.checked);
        rating = !on || on.value === '0' ? '' : on.value;
      }
      const liked = one('#log_liked_checkbox') || inForm('input[type=checkbox]').find((x) => /(^|[_\-\[])(game_)?liked?($|[_\-\]])/i.test(x.name || x.id));
      const reviews = inForm('textarea#review');
      return {
        status: status ? status.value : null,
        rating,
        is_play: flag(one('#play_toggle_checkbox')),
        is_playing: flag(one('#playing_toggle_checkbox')),
        is_backlog: flag(one('#backlog_toggle_checkbox')),
        is_wishlist: flag(one('#wishlist_toggle_checkbox')),
        game_liked: flag(liked),
        review: reviews.length === 1 ? reviews[0].value : null,
      };
    };
    // Every field of the log box keeps its value, except the totals the sync may work out again and the calendar's
    // month and year pickers. Fields the Journal tab adds later are not compared.
    const RECALC = /^(playthroughs\[\d+\]\[(hours_played|mins_played)\]|log\[(total_hours|total_minutes)\])$/;
    const UNGUARDED = ['month-selector', 'year-selector', 'playthrough-time-played-hours', 'playthrough-time-played-minutes'];
    const skipField = (el) => el.closest('#playthrough-modal') || skipType(el) || RECALC.test(el.name || '') || UNGUARDED.indexOf(el.id) >= 0;
    const guarded = () => {
      const out = {};
      const seen = {};
      modal.querySelectorAll('input, textarea, select').forEach((el) => {
        if (skipField(el)) return;
        out[fieldKey(el, seen)] = fieldValue(el);
      });
      return out;
    };
    // The box is filled from two answers the app counts (its editor and its data). Then it must show a status and
    // stop changing, so the values compared later are the log Backloggd has, not the box's empty defaults.
    if (!(await waitFor(() => (window.__logBoxRendered || 0) > 0 && (window.__logBoxData || 0) > 0, 10000))) { fail('log box load'); return; }
    // The app sets the quick editor for the run; any other editor is not one these checks know.
    const editorType = one('input[name="editor_type"]') || one('input#editor_type');
    if (!editorType || editorType.value !== 'quick') { fail('editor mode'); return; }
    const boxSig = () => JSON.stringify([readLog(), guarded()]);
    if ((await settle(boxSig, 1000, 10000)) === null) { fail('log box moving'); return; }
    const log = readLog();
    const before = guarded();
    // The playthrough's "Sync sessions" box, which the run may tick itself (see below).
    const SYNC = 'playthrough-use-sessions-time';
    let syncTurnedOn = false;
    const moved = () => {
      const now = guarded();
      return Object.keys(before).filter((k) => !(k in now) || now[k] !== before[k])
        .filter((k) => !(syncTurnedOn && k === SYNC + '#1' && before[k] === false && now[k] === true));
    };

    const findSaves = () => [...modal.querySelectorAll('button.save-log')].filter((b) => visible(b) && b.type === 'button' && !b.disabled);
    // Step 2, called by the app once it has written down what the save is for. One press at most.
    const installCommit = (stillGood) => {
      let committed = false;
      const commit = () => {
        if (committed) return { pressed: false, step: 'commit twice' };
        committed = true;
        if (window.__journalStop) return { pressed: false, step: 'stopped' };
        const bad = stillGood();
        if (bad) return Object.assign({ pressed: false }, bad);
        const now = findSaves();
        if (now.length !== 1) return { pressed: false, step: 'save button', count: now.length };
        window.__journalStage = 'saving';
        now[0].click();
        return { pressed: true };
      };
      Object.defineProperty(window, '__journalCommit', { value: commit, writable: false, configurable: false });
      window.__journalStage = 'ready';
    };
    // The page sends the checked cover choice as the log's cover, which the log data may leave null.
    const coverChoice = () => {
      const cover = inForm('input[type=radio][name="editor_override_cover_id"]').find((r) => r.checked);
      return cover ? cover.value : null;
    };

    // A Mark as choice: picked in the box the way a person would, then the same Save. Only the status, the picked
    // toggles and, after a status, the "played" toggle may change.
    if (statusMode) {
      // The box must be in the state its log button says: a new log shows playthrough -1, no log id
      // and a "Create Log" button; an existing log none of those.
      const playthroughBox = one('input#current_playthrough');
      const logIdBox = one('input#log_id');
      const saveLabels = findSaves().map((b) => b.textContent.trim());
      const boxNew = !!playthroughBox && playthroughBox.value === '-1' && !!logIdBox && logIdBox.value === '' && saveLabels.length === 1 && saveLabels[0] === 'Create Log';
      const boxExisting = !!playthroughBox && /^[1-9]\d*$/.test(playthroughBox.value) && saveLabels.every((t) => t !== 'Create Log');
      if (newLog ? !boxNew : !boxExisting) { fail(newLog ? 'new log box' : 'log box state', { labels: saveLabels }); return; }
      const allowed = {};
      if (pick.status) {
        const option = modal.querySelector(`.play-type-option#${pick.status}`);
        if (!option || !visible(option)) { fail('status option'); return; }
        option.click();
        const st = one('input#status');
        if (!st || !(await waitFor(() => st.value === pick.status, 1500))) { fail('status not set'); return; }
        allowed['status#1'] = () => true;
        allowed['play_toggle_checkbox#1'] = (was, now) => was === false && now === true;
      }
      for (const [key, id] of [['playing', 'playing_toggle_checkbox'], ['backlog', 'backlog_toggle_checkbox'], ['wishlist', 'wishlist_toggle_checkbox']]) {
        if (typeof pick[key] !== 'boolean') continue;
        const cb = one('input#' + id);
        if (!cb) { fail(`${key} toggle`); return; }
        if (cb.checked !== pick[key]) cb.click();
        if (!(await waitFor(() => cb.checked === pick[key], 1000))) { fail(`${key} not set`); return; }
        allowed[id + '#1'] = (was, now) => now === pick[key];
      }
      await sleep(300);
      const statusMoved = () => {
        const now = guarded();
        return Object.keys(before).filter((k) => {
          if (!(k in now)) return true;
          if (now[k] === before[k]) return false;
          return !(allowed[k] && allowed[k](before[k], now[k]));
        });
      };
      const changed = statusMoved();
      if (changed.length) { fail('guard', { fields: changed.slice(0, 8) }); return; }
      const saves = findSaves();
      if (saves.length !== 1) { fail('save button', { count: saves.length }); return; }
      if (window.__journalStop) { fail('stopped'); return; }
      installCommit(() => {
        const again = statusMoved();
        return again.length ? { step: 'guard', fields: again.slice(0, 8) } : null;
      });
      post({
        ok: true, statusReady: true, log, after: readLog(), coverId: coverChoice(), newLog,
        playthroughId: playthroughBox.value, logId: logIdBox ? logIdBox.value : null, saveLabel: saveLabels[0] || null,
      });
      return;
    }

    const tab = () => {
      const el = modal.querySelector('div#journal-nav');
      return el && visible(el) && el.textContent.trim() === 'Journal' ? el : null;
    };
    const journalTab = await waitFor(tab, 4000);
    if (!journalTab) { fail('journal tab'); return; }
    journalTab.click();
    const anyDay = () => modal.querySelector('.fc td[data-date]');
    if (!(await waitFor(anyDay, 10000))) { fail('calendar'); return; }
    mark('calendar');

    // The month on show, from the calendar's own pickers (the month one counts from 0).
    const shownMonth = () => {
      const m = modal.querySelector('select#month-selector');
      const y = modal.querySelector('select#year-selector');
      if (!m || !y || !/^\d{1,2}$/.test(m.value) || !/^\d{4}$/.test(y.value)) return null;
      return `${y.value}-${String(Number(m.value) + 1).padStart(2, '0')}`;
    };
    const target = opts.date.slice(0, 7);
    const dayCell = () => modal.querySelector(`.fc td[data-date="${opts.date}"]`);
    for (let i = 0; i < 36; i++) {
      const shown = shownMonth();
      if (!shown || shown === target) break;
      const btn = modal.querySelector(shown > target ? 'button#month-prev' : 'button#month-next');
      if (!btn || !visible(btn)) { fail('month buttons'); return; }
      btn.click();
      if (!(await waitFor(() => shownMonth() !== shown, 4000))) { fail('month'); return; }
    }

    // Events can be drawn after the cells and the calendar shows no loading sign, so it must stop
    // changing first, and an empty day is looked at twice.
    const calSig = () => `${shownMonth()}|${modal.querySelectorAll('.fc .fc-event').length}|${modal.querySelectorAll('.fc .fc-daygrid-more-link').length}`;
    const calm = async (quiet) => {
      const s = await settle(calSig, quiet, 15000);
      return s !== null && shownMonth() === target;
    };
    if (!(await calm(1500))) { fail('calendar loading'); return; }
    const sessions = () => {
      const cell = dayCell();
      if (!cell) return null;
      return { events: [...cell.querySelectorAll('.fc-event')].filter(visible), more: !!cell.querySelector('.fc-daygrid-more-link') };
    };
    let found = sessions();
    if (!found) { fail('day'); return; }
    if (found.events.length === 0 && !found.more) {
      await sleep(2000);
      if (!(await calm(1000))) { fail('calendar loading'); return; }
      found = sessions();
      if (!found) { fail('day'); return; }
    }
    mark('day');

    // A "+N more" link hides some of the day's events: its popover lists them all (opening it sends
    // nothing). If it shows none, the day stays unsupported.
    if (found.more) {
      const link = dayCell().querySelector('.fc-daygrid-more-link');
      link.click();
      const popoverEvents = () => {
        const pop = [...modal.querySelectorAll('.fc-popover')].find(visible);
        return pop ? [...pop.querySelectorAll('.fc-event')].filter(visible) : [];
      };
      if (!(await waitFor(() => popoverEvents().length > 0, 3000))) { unsupported('several sessions'); return; }
      found = { events: popoverEvents(), more: false };
    }

    // A day with no session gets one the way the page makes it: a press and release on the day (with a small move
    // inside the cell) adds a page-made event with a negative id. Nothing is sent until the log's Save.
    let newSession = false;
    if (found.events.length === 0) {
      const allEvents = () => modal.querySelectorAll('.fc .fc-event').length;
      const before = allEvents();
      const cell = dayCell();
      const frame = cell.querySelector('.fc-daygrid-day-frame') || cell;
      const r = frame.getBoundingClientRect();
      const x = r.left + r.width / 2;
      const y = r.top + r.height / 2;
      const at = (dx, buttons) => ({ bubbles: true, cancelable: true, composed: true, view: window, clientX: x + dx, clientY: y, screenX: x + dx, screenY: y, button: 0, buttons });
      const pointer = (o) => Object.assign({ pointerId: 1, pointerType: 'mouse', isPrimary: true }, o);
      frame.dispatchEvent(new PointerEvent('pointerdown', pointer(at(0, 1))));
      frame.dispatchEvent(new MouseEvent('mousedown', at(0, 1)));
      await sleep(30);
      frame.dispatchEvent(new PointerEvent('pointermove', pointer(at(2, 1))));
      frame.dispatchEvent(new MouseEvent('mousemove', at(2, 1)));
      await sleep(30);
      frame.dispatchEvent(new PointerEvent('pointerup', pointer(at(2, 0))));
      frame.dispatchEvent(new MouseEvent('mouseup', at(2, 0)));
      // Within 3 s exactly one new event, in this day's cell.
      if (!(await waitFor(() => allEvents() > before, 3000))) { unsupported('new session'); return; }
      if ((await settle(calSig, 500, 3000)) === null) { fail('calendar loading'); return; }
      found = sessions();
      if (!found || (found.events.length === 0 && !found.more)) { fail('new session on another day'); return; }
      if (found.more || found.events.length !== 1 || allEvents() - before !== 1) { fail('new session twice'); return; }
      newSession = true;
    }
    // Several sessions: the first one that starts on the day, in the calendar's order. None starts on
    // the day: the session from an earlier day that runs over it (a continuation piece here).
    const starting = found.events.filter((e) => e.classList.contains('fc-event-start'));
    const event = starting[0] || found.events[0];
    const continued = !newSession && starting.length === 0;

    // The dialog is filled from /open-date-modal/; the app counts its answers in __dateModalLoaded.
    const dialog = () => document.querySelector('#playthrough-modal.is-open');
    const q = (sel) => document.querySelector('#playthrough-modal ' + sel);
    const loadedBefore = window.__dateModalLoaded || 0;
    window.__journalStage = 'session';
    event.click();
    if (!(await waitFor(() => (window.__dateModalLoaded || 0) > loadedBefore && dialog() && q('#play_date_hours') && q('#play_date_minutes'), 10000))) {
      fail('session dialog');
      return;
    }
    const dialogFields = () => [...document.querySelectorAll('#playthrough-modal input, #playthrough-modal textarea, #playthrough-modal select')]
      .filter((el) => !skipType(el));
    const dialogSig = () => JSON.stringify(dialogFields().map((el) => [el.id || el.name, fieldValue(el)]));
    if ((await settle(dialogSig, 1000, 8000)) === null || !dialog()) { fail('session dialog moving'); return; }
    if ((window.__dateModalLoaded || 0) - loadedBefore !== 1) { fail('session dialog loaded twice'); return; }
    const hours = q('#play_date_hours');
    const minutes = q('#play_date_minutes');
    if (!hours || !minutes) { fail('session minutes'); return; }

    // The dialog must be the target day's. It has no date field: the header shows the day ("Nov 8", no year), read with the
    // calendar's year, and the id is #play-date-id[date_id]. A December session seen from January is the year before.
    const dialogDay = () => {
      const el = q('.modal-header .subtitle-text');
      const m = el ? el.textContent.trim().match(/^([A-Za-z]{3})[a-z]*\.? (\d{1,2})\b/) : null;
      const shown = shownMonth();
      if (!m || !shown || MONTHS.indexOf(m[1].toLowerCase()) < 0) return null;
      const month = MONTHS.indexOf(m[1].toLowerCase()) + 1;
      const year = Number(shown.slice(0, 4)) - (month > Number(shown.slice(5, 7)) ? 1 : 0);
      return `${year}-${String(month).padStart(2, '0')}-${m[2].padStart(2, '0')}`;
    };
    // A saved session has a positive id; the one the page just made, a negative one.
    const sessionIdOf = () => {
      const el = q('#play-date-id');
      const id = el ? (el.getAttribute('date_id') || '').trim() : '';
      return (newSession ? /^-[1-9]\d*$/ : /^[1-9]\d*$/).test(id) ? id : null;
    };
    const day = dialogDay();
    if (!day) { fail('session day unreadable'); return; }
    // The app checks a continuation's days against Backloggd's own data; here it must start earlier.
    if (continued ? !(day < opts.date) : day !== opts.date) { fail('session date', { dialogDate: day }); return; }
    const sessionId = sessionIdOf();
    if (!sessionId) { fail(newSession ? 'new session id' : 'session id'); return; }
    mark('session');

    // Everything else in the dialog (start and finish boxes, note, tags, privacy, its day and id)
    // must keep the value it loaded with.
    const others = () => {
      const out = {};
      const seen = {};
      dialogFields().forEach((el) => {
        if (el === hours || el === minutes) return;
        out[fieldKey(el, seen)] = fieldValue(el);
      });
      const privacy = q('#note-privacy-dropdown .nav-link.current-sort');
      out['privacy'] = privacy ? privacy.getAttribute('privacy') : null;
      out['tags'] = (q('#play-session-tags') || { textContent: '' }).textContent.trim();
      out['day'] = dialogDay();
      out['id'] = sessionIdOf();
      return out;
    };
    const othersBefore = others();
    const othersMoved = () => {
      const now = others();
      const keys = new Set([...Object.keys(othersBefore), ...Object.keys(now)]);
      return [...keys].filter((k) => now[k] !== othersBefore[k]);
    };

    const current = whole(hours.value) * 60 + whole(minutes.value);
    if (Number.isNaN(current)) { fail('session time'); return; }
    if (newSession && current !== 0) { fail('new session time'); return; }
    if (!newSession && opts.already != null && current === opts.already) {
      post({ ok: true, already: true, current, sessionId, continued });
      return;
    }
    // After an "add it yourself" notice: a session holding at least the told minutes has them (the
    // user typed them in), so only play since the notice goes in; none since means nothing to save.
    let add = opts.add;
    const toldThere = !newSession && opts.told > 0 && current >= opts.told;
    if (toldThere) {
      add = opts.add - opts.told;
      if (add <= 0) {
        post({ ok: true, covered: true, current, sessionId, continued });
        return;
      }
    }
    const total = current + add;
    const newHours = Math.floor(total / 60);
    const max = hours.getAttribute('max');
    if ((max !== null && max !== '' && !(newHours <= Number(max))) || (hours.maxLength > 0 && String(newHours).length > hours.maxLength)) {
      unsupported('hours max');
      return;
    }
    setValue(hours, String(newHours));
    setValue(minutes, String(total % 60));
    await sleep(300);
    if (whole(hours.value) * 60 + whole(minutes.value) !== total) { fail('session time set'); return; }
    const changedInDialog = othersMoved();
    if (changedInDialog.length) { fail('session fields', { fields: changedInDialog.slice(0, 8) }); return; }
    if (window.__journalStop) { fail('stopped'); return; }

    // The dialog's Save fills an empty session status in only when neither box is ticked; the app
    // checks the save's status with these, read before the dialog closes.
    const box = (sel) => { const el = q(sel); return !!(el && el.checked); };
    const sessionBoxes = [box('#is_start_session'), box('#is_finish_session')];
    const update = q('#play-date-update');
    if (!update) { fail('session save button'); return; }
    update.click();
    if (!(await waitFor(() => !dialog(), 5000))) { fail('session save'); return; }
    mark('sessionSaved');

    // Sync sessions on, so Time Played follows the calendar: a real click, so the page's own handler
    // runs. The app checks it was off in Backloggd's data and that it is this playthrough's box.
    const syncBox = one('input#' + SYNC);
    if (syncBox && syncBox.type === 'checkbox' && !syncBox.checked) {
      syncBox.click();
      if (!(await waitFor(() => syncBox.checked, 2000))) { fail('sync sessions'); return; }
      syncTurnedOn = true;
      await sleep(300);
    }

    const changed = moved();
    if (changed.length) { fail('guard', { fields: changed.slice(0, 8) }); return; }
    const saves = findSaves();
    if (saves.length !== 1) { fail('save button', { count: saves.length }); return; }
    if (window.__journalStop) { fail('stopped'); return; }

    installCommit(() => {
      if (dialog()) return { step: 'session dialog open' };
      const again = moved();
      return again.length ? { step: 'guard', fields: again.slice(0, 8) } : null;
    });
    const playthrough = one('input#current_playthrough');
    post({
      ok: true, ready: true, target: total, current, log, sessionId, sessionBoxes, coverId: coverChoice(),
      newSession, playthroughId: playthrough ? playthrough.value : null, syncTurnedOn, continued, toldThere,
    });
  } catch (e) {
    fail('script', { error: e && e.message ? e.message : String(e) });
  }
})(__OPTS__);
