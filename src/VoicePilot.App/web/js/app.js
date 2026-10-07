/* VoicePilot UI */
'use strict';

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const App = {
  settings: {},
  status: {},
  history: [],
  devices: [],
  models: [],
  voices: [],
  confirm: null,
  view: 'home',
  draft: null,          // custom commands being edited
  draftDirty: false,
  cheatLang: 'tr',
  idleNote: '',
  idleNoteTimer: null,
};

// ---------------------------------------------------------------- boot

async function boot() {
  try {
    applyInit(await Api.call('init'));
  } catch (err) {
    document.body.innerHTML = `<pre style="padding:24px;color:#f88">${esc(err.message)}</pre>`;
    return;
  }
  bindEvents();
  const v = new URLSearchParams(location.search).get('view');
  showView(['home', 'commands', 'settings'].includes(v) ? v : 'home');
  requestAnimationFrame(() => document.body.classList.add('ready'));
}

function applyInit(init) {
  App.settings = init.settings;
  App.status = init.status;
  App.history = init.history || [];
  App.devices = init.devices || [];
  App.models = init.models || [];
  App.voices = init.voices || [];
  App.version = init.version;
  I18n.lang = init.uiLang;
  App.cheatLang = App.cheatLang || I18n.lang;
  setTheme(init.dark);
  if (!App.draftDirty) App.draft = JSON.parse(JSON.stringify(App.settings.commands || []));
  renderAll();
}

function renderAll() {
  I18n.apply();
  renderSidebarStatus();
  renderStage();
  renderExamples();
  renderFeed();
  renderCommands();
  renderCheats();
  renderSettings();
  Icons.hydrate();
}

function setTheme(dark) {
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
}

// ---------------------------------------------------------------- events

function bindEvents() {
  $$('.nav-item').forEach(b => b.addEventListener('click', () => showView(b.dataset.view)));
  $('#orb').addEventListener('click', () => {
    if (App.status.state === 'error') Api.call('reloadModel');
    else Api.call('toggle');
  });
  $('#askForm').addEventListener('submit', e => {
    e.preventDefault();
    const input = $('#askInput');
    const text = input.value.trim();
    if (!text) return;
    Api.call('run', { text }).catch(toastError);
    input.value = '';
  });
  $('#clearHistory').addEventListener('click', async () => {
    await Api.call('clearHistory');
    App.history = [];
    renderFeed();
  });
  $('#confirmYes').addEventListener('click', () => Api.call('confirm', { yes: true }));
  $('#confirmNo').addEventListener('click', () => Api.call('confirm', { yes: false }));
  $('#addCommand').addEventListener('click', () => {
    App.draft.push({ phrase: '', steps: [] });
    markDraftDirty();
    renderCommands();
    const inputs = $$('.custom-phrase');
    inputs[inputs.length - 1]?.focus();
  });
  $('#saveCommands').addEventListener('click', saveCommands);
  $$('#cheatLang button').forEach(b => b.addEventListener('click', () => { App.cheatLang = b.dataset.v; renderCheats(); }));

  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && ['listening', 'thinking', 'transcribing'].includes(App.status.state)) Api.call('cancel');
  });

  Api.on('state', s => {
    App.status.state = s.state;
    App.status.detail = s.detail;
    App.status.progress = s.progress;
    if (s.state === 'idle' && s.detail) {
      App.idleNote = s.detail;
      clearTimeout(App.idleNoteTimer);
      App.idleNoteTimer = setTimeout(() => { App.idleNote = ''; renderStage(); }, 4000);
    } else if (s.state !== 'idle') {
      App.idleNote = '';
    }
    if (s.state !== 'listening') setLevel(0);
    renderStage();
    renderSidebarStatus();
  });
  Api.on('level', setLevel);
  Api.on('entry', e => {
    const i = App.history.findIndex(h => h.id === e.id);
    if (i >= 0) App.history[i] = e;
    else App.history.unshift(e);
    renderFeed(e.id);
  });
  Api.on('confirm', c => { App.confirm = c; renderConfirm(); });
  Api.on('status', s => {
    App.status = { ...App.status, ...s };
    renderSidebarStatus();
    renderStage();
    if (App.view === 'settings') renderSettings();
  });
  Api.on('theme', t => setTheme(t.dark));
  Api.on('settings', s => { App.settings = s; renderStage(); if (App.view === 'settings') renderSettings(); });
}

function showView(view) {
  App.view = view;
  $$('.view').forEach(v => { v.hidden = v.id !== 'view-' + view; });
  $$('.nav-item').forEach(b => b.classList.toggle('active', b.dataset.view === view));
  if (view === 'settings') renderSettings();
  if (view === 'home') setTimeout(() => $('#askInput').focus({ preventScroll: true }), 50);
}

function setLevel(l) {
  $('#orb').style.setProperty('--level', Math.min(1, Math.max(0, l || 0)).toFixed(3));
}

// ---------------------------------------------------------------- sidebar & stage

function brainLabel() {
  const b = App.status.brain || {};
  return b.mode === 'api' ? t('brain_api') : b.mode === 'cli' ? t('brain_cli') : t('brain_local');
}

function renderSidebarStatus() {
  const st = App.status;
  const gpu = (st.backend || '').toLowerCase().includes('vulkan') || (st.backend || '').toLowerCase().includes('cuda');
  const ready = !['loading', 'error'].includes(st.state);
  $('#sidebarStatus').innerHTML = `
    <div class="sb-row" title="${esc(t('chip_model'))}">
      <span class="dot ${ready ? 'ok' : st.state === 'error' ? 'err' : 'warn'}"></span>
      <span>${esc(st.model || '')}</span>
      <span class="sb-tag">${ready ? (gpu ? t('gpu') : t('cpu')) : '…'}</span>
    </div>
    <div class="sb-row" title="${esc(t('chip_brain'))}">
      <span class="dot ${st.brain?.mode === 'local' ? 'idle' : st.brain?.ready ? 'ok' : 'warn'}"></span>
      <span>${esc(brainLabel())}</span>
    </div>
    <div class="sb-row sb-hotkey"><kbd class="${st.hotkeyError ? 'kbd-err' : ''}" title="${esc(st.hotkeyError || '')}">${esc(st.hotkey || App.settings.hotkey)}</kbd></div>
    <div class="sb-version">v${esc(App.version || '')}</div>`;
}

function renderStage() {
  const st = App.status;
  const orb = $('#orb');
  orb.dataset.state = st.state;
  orb.style.setProperty('--progress', st.progress != null ? st.progress : 0);
  const icon = { listening: 'mic', transcribing: 'wave', thinking: 'sparkle', executing: 'bolt', confirming: 'hand', loading: 'download', error: 'alert' }[st.state] || 'mic';
  const iconEl = $('.orb-icon', orb);
  iconEl.dataset.icon = icon;
  Icons.hydrate(orb);

  let title = '', detail = '', cls = '';
  switch (st.state) {
    case 'loading':
      if (st.detail === 'downloading') {
        const pct = Math.round((st.progress || 0) * 100);
        const m = App.models.find(x => x.id === st.model);
        title = `${t('st_downloading')} · ${pct}%`;
        detail = `${st.model}${m ? ` (${m.sizeMb} MB)` : ''} — ${t('st_download_note')}`;
      } else {
        title = t('st_loading');
        detail = st.model || '';
      }
      break;
    case 'listening': title = t('st_listening'); detail = t('st_listening_hint'); break;
    case 'transcribing': title = t('st_transcribing'); break;
    case 'thinking': title = t('st_thinking'); detail = st.detail ? `“${st.detail}”` : ''; break;
    case 'executing': title = t('st_executing'); detail = st.detail || ''; cls = 'mono'; break;
    case 'confirming': title = t('st_confirming'); detail = st.detail || ''; break;
    case 'error': title = t('st_error'); detail = (st.detail || '') + ' — ' + t('retry'); cls = 'err'; break;
    default: {
      title = t('st_idle');
      if (App.idleNote) { detail = App.idleNote; cls = 'warn'; }
      else if (st.hotkeyError) { detail = t('hotkey_problem', st.hotkeyError); cls = 'warn'; }
      else {
        detail = t('st_idle_hint', st.hotkey || App.settings.hotkey);
        if (App.settings.wakeWord && App.settings.wakeWords?.length) detail += ' ' + t('st_idle_wake', capitalize(App.settings.wakeWords[0]));
      }
    }
  }
  $('#stageTitle').textContent = title;
  const d = $('#stageDetail');
  d.textContent = detail;
  d.className = 'stage-detail ' + cls;

  const gpu = /vulkan|cuda/i.test(st.backend || '');
  $('#stageChips').innerHTML = `
    <span class="chip">${Icons.svg('cpu')}${esc(st.model || '')}${st.backend ? ` · ${gpu ? t('gpu') : t('cpu')}` : ''}</span>
    <span class="chip">${Icons.svg('brain')}${esc(brainLabel())}</span>
    ${st.apps ? `<span class="chip">${Icons.svg('app')}${esc(t('chip_apps', st.apps))}</span>` : ''}
    ${App.settings.wakeWord ? `<span class="chip chip-accent">${Icons.svg('ear')}“${esc(capitalize(App.settings.wakeWords?.[0] || ''))}”</span>` : ''}`;
  renderConfirm();
}

function renderConfirm() {
  const card = $('#confirmCard');
  const c = App.confirm;
  card.hidden = !c;
  if (!c) return;
  $('#confirmText').textContent = c.text;
  const d = $('#confirmDetail');
  d.hidden = !c.detail;
  d.textContent = c.detail || '';
  Icons.hydrate(card);
}

function renderExamples() {
  const list = I18n.quick[I18n.lang] || [];
  $('#examples').innerHTML = list.map(x => `<button class="example" type="button">${esc(x)}</button>`).join('');
  $$('#examples .example').forEach(b => b.addEventListener('click', () => {
    const input = $('#askInput');
    input.value = b.textContent;
    input.focus();
  }));
}

const capitalize = s => s ? s[0].toLocaleUpperCase(I18n.lang === 'tr' ? 'tr-TR' : 'en-US') + s.slice(1) : s;

// ---------------------------------------------------------------- feed

const stepIcon = {
  open_app: 'app', close_app: 'x', switch_to: 'window', open_url: 'globe', search: 'globe', open_folder: 'folder',
  type_text: 'keyboard', press_keys: 'keyboard', volume: 'volume', media: 'play', window: 'window', system: 'power',
  screenshot: 'window', scroll: 'list', say: 'sparkle', wait: 'clock', reminder: 'bell', run_command: 'terminal', command: 'rerun',
};
const inputIcon = { voice: 'mic', typed: 'keyboard', wake: 'ear', reminder: 'bell', macro: 'rerun' };

function fmtTime(iso) {
  const d = new Date(iso);
  if (isNaN(d)) return '';
  const today = new Date();
  const time = d.toLocaleTimeString(I18n.lang === 'tr' ? 'tr-TR' : 'en-GB', { hour: '2-digit', minute: '2-digit' });
  if (d.toDateString() === today.toDateString()) return time;
  return d.toLocaleDateString(I18n.lang === 'tr' ? 'tr-TR' : 'en-GB', { day: 'numeric', month: 'short' }) + ' ' + time;
}

function entryHtml(e) {
  const timing = [];
  if (e.sttSeconds) timing.push(`${t('in_voice').toLowerCase()} ${e.sttSeconds.toFixed(1)}s`);
  if (e.thinkSeconds) timing.push(`Claude ${e.thinkSeconds.toFixed(1)}s`);
  if (e.totalSeconds && e.status !== 'running') timing.push(`${e.totalSeconds.toFixed(1)}s`);
  const steps = (e.steps || []).map(s => `
    <li class="step s-${esc(s.status)}">
      <span class="step-state">${s.status === 'ok' ? Icons.svg('check') : s.status === 'error' ? Icons.svg('x') : s.status === 'running' ? '<span class="spin"></span>' : s.status === 'skipped' ? Icons.svg('x') : Icons.svg(stepIcon[s.kind] || 'sparkle')}</span>
      <code>${esc(s.text)}</code>
      ${s.message ? `<span class="step-msg">${esc(s.message)}</span>` : ''}
    </li>`).join('');
  const src = e.source ? `<span class="badge src-${esc(e.source)}">${esc(t('src_' + e.source))}</span>` : '';
  const statusBadge = e.status !== 'ok' ? `<span class="badge st-${esc(e.status)}">${esc(t('status_' + e.status))}</span>` : '';
  return `
    <article class="entry" data-id="${esc(e.id)}" data-status="${esc(e.status)}">
      <div class="entry-head">
        <span class="entry-input" title="${esc(t('in_' + e.input))}">${Icons.svg(inputIcon[e.input] || 'mic')}</span>
        <div class="entry-text">${esc(e.text)}</div>
        <div class="entry-badges">${statusBadge}${src}</div>
      </div>
      ${e.reply ? `<div class="entry-reply">${esc(e.reply)}</div>` : ''}
      ${steps ? `<ol class="steps">${steps}</ol>` : (e.status === 'ok' && !e.reply ? `<div class="entry-reply muted">${esc(t('no_steps'))}</div>` : '')}
      <div class="entry-foot">
        <span>${esc(fmtTime(e.time))}</span>
        ${timing.length ? `<span>· ${esc(timing.join(' · '))}</span>` : ''}
        ${e.input !== 'reminder' ? `<button class="link rerun" title="${esc(t('rerun'))}">${Icons.svg('rerun')}<span>${esc(t('rerun'))}</span></button>` : ''}
      </div>
    </article>`;
}

function renderFeed(changedId) {
  const feed = $('#feed');
  if (!App.history.length) {
    feed.innerHTML = `<div class="empty">${Icons.svg('wave', 'empty-ic')}<p>${esc(t('empty_feed'))}</p></div>`;
    return;
  }
  if (changedId) {
    const e = App.history.find(h => h.id === changedId);
    const existing = feed.querySelector(`[data-id="${CSS.escape(changedId)}"]`);
    if (e && existing) {
      existing.outerHTML = entryHtml(e);
      bindFeed();
      return;
    }
    if (e && feed.querySelector('.entry')) {
      feed.insertAdjacentHTML('afterbegin', entryHtml(e));
      feed.firstElementChild.classList.add('enter');
      bindFeed();
      return;
    }
  }
  feed.innerHTML = App.history.slice(0, 120).map(entryHtml).join('');
  bindFeed();
}

function bindFeed() {
  $$('#feed .rerun').forEach(b => {
    if (b.dataset.bound) return;
    b.dataset.bound = '1';
    b.addEventListener('click', () => {
      const id = b.closest('.entry').dataset.id;
      const e = App.history.find(h => h.id === id);
      if (e) Api.call('run', { text: e.text }).catch(toastError);
    });
  });
}

// ---------------------------------------------------------------- custom commands

function markDraftDirty() {
  App.draftDirty = true;
  const s = $('#commandsSaved');
  s.textContent = t('unsaved');
  s.className = 'save-state dirty';
}

function renderCommands() {
  const list = $('#customList');
  if (!App.draft.length) {
    list.innerHTML = `<div class="empty small"><p>${esc(t('no_custom'))}</p></div>`;
    return;
  }
  list.innerHTML = App.draft.map((c, i) => `
    <div class="custom" data-i="${i}">
      <div class="custom-main">
        <label class="field">
          <span>${esc(t('phrase'))}</span>
          <input class="custom-phrase" value="${esc(c.phrase)}" placeholder="${esc(t('phrase_ph'))}" spellcheck="false">
        </label>
        <label class="field">
          <span>${esc(t('steps'))}</span>
          <textarea class="custom-steps" rows="${Math.max(3, c.steps.length + 1)}" placeholder="${esc(t('steps_ph'))}" spellcheck="false">${esc(c.steps.join('\n'))}</textarea>
        </label>
      </div>
      <div class="custom-actions">
        <button class="btn btn-ghost btn-sm custom-try" title="${esc(t('try_it'))}">${Icons.svg('play')}<span>${esc(t('try_it'))}</span></button>
        <button class="btn btn-ghost btn-sm btn-danger custom-remove" title="${esc(t('remove'))}">${Icons.svg('trash')}</button>
      </div>
    </div>`).join('');
  $$('.custom', list).forEach(row => {
    const i = +row.dataset.i;
    $('.custom-phrase', row).addEventListener('input', e => { App.draft[i].phrase = e.target.value; markDraftDirty(); });
    $('.custom-steps', row).addEventListener('input', e => { App.draft[i].steps = e.target.value.split('\n'); markDraftDirty(); });
    $('.custom-remove', row).addEventListener('click', () => { App.draft.splice(i, 1); markDraftDirty(); renderCommands(); });
    $('.custom-try', row).addEventListener('click', async () => {
      if (App.draftDirty) await saveCommands();
      const phrase = App.draft[i]?.phrase?.trim();
      if (phrase) { Api.call('run', { text: phrase }); showView('home'); }
    });
  });
}

async function saveCommands() {
  const commands = App.draft
    .map(c => ({ phrase: c.phrase.trim(), steps: c.steps.map(s => s.trim()).filter(Boolean) }))
    .filter(c => c.phrase && c.steps.length);
  try {
    const init = await Api.call('saveCommands', { commands });
    App.draftDirty = false;
    App.settings = init.settings;
    App.draft = JSON.parse(JSON.stringify(App.settings.commands || []));
    renderCommands();
    const s = $('#commandsSaved');
    s.textContent = t('saved');
    s.className = 'save-state ok';
  } catch (err) { toastError(err); }
}

function renderCheats() {
  $$('#cheatLang button').forEach(b => b.classList.toggle('on', b.dataset.v === App.cheatLang));
  const groups = I18n.examples[App.cheatLang] || [];
  $('#cheats').innerHTML = groups.map(([icon, title, items]) => `
    <div class="cheat ${icon === 'sparkle' ? 'cheat-claude' : ''}">
      <div class="cheat-title">${Icons.svg(icon)}<span>${esc(title)}</span></div>
      <div class="cheat-items">${items.map(x => `<button class="example">${esc(x)}</button>`).join('')}</div>
    </div>`).join('');
  $$('#cheats .example').forEach(b => b.addEventListener('click', () => {
    showView('home');
    const input = $('#askInput');
    input.value = b.textContent;
    input.focus();
  }));
}

// ---------------------------------------------------------------- settings

function settingsSchema() {
  const s = App.settings;
  const st = App.status;
  const gpuNote = st.restartNeeded ? t('restart_needed') : (st.backend ? t('running_on', st.backend) : '');
  return [
    { title: 'sec_listening', icon: 'ear', rows: [
      { key: 'hotkey', type: 'hotkey', label: 'set_hotkey', desc: 'set_hotkey_d', error: st.hotkeyError },
      { key: 'micDevice', type: 'select', label: 'set_mic', desc: 'set_mic_d', options: [['', t('mic_default')], ...App.devices.map(d => [d, d])] },
      { key: 'wakeWord', type: 'toggle', label: 'set_wake', desc: 'set_wake_d' },
      { key: 'wakeWords', type: 'words', label: 'set_wake_words', hidden: !s.wakeWord },
      { key: 'silenceMs', type: 'range', label: 'set_silence', desc: 'set_silence_d', min: 400, max: 2500, step: 100, fmt: v => (v / 1000).toFixed(1) + ' s' },
    ] },
    { title: 'sec_recognition', icon: 'wave', rows: [
      { key: 'language', type: 'seg', label: 'set_language', desc: 'set_language_d', options: [['auto', t('lang_auto')], ['tr', 'Türkçe'], ['en', 'English']] },
      { key: 'model', type: 'select', label: 'set_model', desc: 'set_model_d',
        options: App.models.map(m => [m.id, `${m.id} — ${m.sizeMb} MB · ${m.downloaded ? t('downloaded') : t('not_downloaded')}`]) },
      { key: 'useGpu', type: 'toggle', label: 'set_gpu', desc: 'set_gpu_d', note: gpuNote, noteWarn: st.restartNeeded },
    ] },
    { title: 'sec_brain', icon: 'brain', rows: [
      { key: 'brain', type: 'seg', label: 'set_brain', desc: 'set_brain_d',
        options: [['auto', t('brain_auto')], ['api', t('brain_mode_api')], ['cli', t('brain_mode_cli')], ['local', t('brain_mode_local')]],
        note: st.brain ? `${brainLabel()} · ${st.brain.detail}` : '', noteWarn: st.brain && !st.brain.ready },
      { key: 'apiKey', type: 'apikey', label: 'set_apikey', desc: 'set_apikey_d' },
      { key: 'claudeModel', type: 'select', label: 'set_claude_model', desc: 'set_claude_model_d',
        options: [['claude-opus-5-5', 'Claude Opus 5.5'], ['claude-sonnet-5-5', 'Claude Sonnet 5.5'], ['claude-haiku-4-5', 'Claude Haiku 4.5']] },
      { key: 'cliModel', type: 'select', label: 'set_cli_model', desc: 'set_cli_model_d',
        options: [['sonnet', 'Sonnet'], ['opus', 'Opus'], ['haiku', 'Haiku']], note: st.cliFound ? t('cli_found') : t('cli_missing'), noteWarn: !st.cliFound },
      { type: 'test' },
    ] },
    { title: 'sec_feedback', icon: 'volume', rows: [
      { key: 'speak', type: 'seg', label: 'set_speak', desc: 'set_speak_d', options: [['full', t('speak_full')], ['minimal', t('speak_minimal')], ['off', t('speak_off')]] },
      { key: 'sounds', type: 'toggle', label: 'set_sounds', desc: 'set_sounds_d' },
      { key: 'overlay', type: 'toggle', label: 'set_overlay', desc: 'set_overlay_d' },
      { type: 'voiceTest' },
    ] },
    { title: 'sec_safety', icon: 'shield', rows: [
      { key: 'confirmRisky', type: 'toggle', label: 'set_confirm', desc: 'set_confirm_d' },
      { key: 'allowPowerShell', type: 'toggle', label: 'set_powershell', desc: 'set_powershell_d' },
    ] },
    { title: 'sec_app', icon: 'palette', rows: [
      { key: 'uiLanguage', type: 'seg', label: 'set_ui_lang', options: [['auto', t('lang_auto')], ['tr', 'Türkçe'], ['en', 'English']] },
      { key: 'theme', type: 'seg', label: 'set_theme', options: [['dark', t('theme_dark')], ['light', t('theme_light')], ['system', t('theme_system')]] },
      { key: 'minimizeToTray', type: 'toggle', label: 'set_tray', desc: 'set_tray_d' },
      { key: 'startWithWindows', type: 'toggle', label: 'set_startup', desc: 'set_startup_d' },
      { type: 'data' },
    ] },
  ];
}

function controlHtml(r) {
  const s = App.settings;
  const v = s[r.key];
  switch (r.type) {
    case 'toggle':
      return `<label class="switch"><input type="checkbox" data-key="${r.key}" ${v ? 'checked' : ''}><span></span></label>`;
    case 'seg':
      return `<div class="seg" data-key="${r.key}">${r.options.map(([k, l]) => `<button data-v="${esc(k)}" class="${v === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div>`;
    case 'select':
      return `<select data-key="${r.key}">${r.options.map(([k, l]) => `<option value="${esc(k)}" ${(v ?? '') === k ? 'selected' : ''}>${esc(l)}</option>`).join('')}</select>`;
    case 'range':
      return `<div class="range"><input type="range" data-key="${r.key}" min="${r.min}" max="${r.max}" step="${r.step}" value="${v}"><output>${esc(r.fmt(v))}</output></div>`;
    case 'hotkey':
      return `<input class="hotkey-input" data-key="hotkey" readonly value="${esc(v)}">`;
    case 'words':
      return `<input class="text-input" data-key="wakeWords" value="${esc((v || []).join(', '))}" spellcheck="false">`;
    case 'apikey': {
      const state = s.apiKeySet ? t('apikey_set') : s.envKeySet ? t('apikey_env') : t('apikey_none');
      return `<div class="apikey">
        <input class="text-input" id="apiKeyInput" type="password" placeholder="${s.apiKeySet ? '••••••••••••' : 'sk-ant-…'}" spellcheck="false" autocomplete="off">
        <button class="btn btn-sm" id="apiKeySave">${esc(t('save'))}</button>
        ${s.apiKeySet ? `<button class="btn btn-ghost btn-sm" id="apiKeyClear">${Icons.svg('trash')}</button>` : ''}
        <span class="note ${s.apiKeySet || s.envKeySet ? 'ok' : ''}">${esc(state)}</span>
      </div>`;
    }
    default: return '';
  }
}

function renderSettings() {
  const page = $('#settingsPage');
  if (!page) return;
  const scroll = page.parentElement.scrollTop;
  const sections = settingsSchema().map(sec => `
    <div class="card settings-card">
      <div class="settings-title">${Icons.svg(sec.icon)}<h3>${esc(t(sec.title))}</h3></div>
      ${sec.rows.filter(r => !r.hidden).map(r => {
        if (r.type === 'test') return `<div class="row row-action"><div></div><div class="row-control">
          <span class="note" id="testResult"></span>
          <button class="btn btn-sm" id="testBrain">${Icons.svg('bolt')}<span>${esc(t('test'))}</span></button></div></div>`;
        if (r.type === 'voiceTest') return `<div class="row row-action"><div class="row-label"><div class="row-desc">${esc(App.voices.join(' · '))}</div></div><div class="row-control">
          <button class="btn btn-sm" id="testVoice">${Icons.svg('volume')}<span>${esc(t('test_voice'))}</span></button></div></div>`;
        if (r.type === 'data') return `<div class="row"><div class="row-label"><div class="row-name">${esc(t('data'))}</div><div class="row-desc">${esc(t('data_d'))}</div></div>
          <div class="row-control wrap">
            <button class="btn btn-ghost btn-sm" data-open="data">${Icons.svg('folder')}<span>${esc(t('open_data'))}</span></button>
            <button class="btn btn-ghost btn-sm" data-open="models">${Icons.svg('download')}<span>${esc(t('open_models'))}</span></button>
            <button class="btn btn-ghost btn-sm" data-open="log">${Icons.svg('list')}<span>${esc(t('open_log'))}</span></button>
            <button class="btn btn-ghost btn-sm" id="refreshApps">${Icons.svg('refresh')}<span>${esc(t('refresh_apps'))}</span></button>
            <button class="btn btn-ghost btn-sm btn-danger" id="exitApp">${Icons.svg('power')}<span>${esc(t('exit_app'))}</span></button>
          </div></div>`;
        return `<div class="row ${r.type === 'words' ? 'row-sub' : ''}">
          <div class="row-label">
            <div class="row-name">${esc(t(r.label))}</div>
            ${r.desc ? `<div class="row-desc">${esc(t(r.desc))}</div>` : ''}
            ${r.error ? `<div class="row-desc err">${esc(r.error)}</div>` : ''}
            ${r.note ? `<div class="row-desc ${r.noteWarn ? 'warn' : 'subtle'}">${esc(r.note)}</div>` : ''}
          </div>
          <div class="row-control">${controlHtml(r)}</div>
        </div>`;
      }).join('')}
    </div>`).join('');
  page.innerHTML = `<header class="page-head"><h1>${esc(t('nav_settings'))}</h1></header>${sections}`;
  page.parentElement.scrollTop = scroll;
  bindSettings(page);
}

async function save(patch) {
  try {
    const init = await Api.call('saveSettings', patch);
    const langBefore = I18n.lang;
    App.settings = init.settings;
    App.status = init.status;
    App.models = init.models;
    App.devices = init.devices;
    I18n.lang = init.uiLang;
    setTheme(init.dark);
    if (I18n.lang !== langBefore) renderAll();
    else { renderSettings(); renderStage(); renderSidebarStatus(); }
  } catch (err) {
    toastError(err);
    renderSettings();
  }
}

function bindSettings(page) {
  $$('input[type=checkbox][data-key]', page).forEach(el => el.addEventListener('change', () => save({ [el.dataset.key]: el.checked })));
  $$('.seg[data-key]', page).forEach(seg => $$('button', seg).forEach(b => b.addEventListener('click', () => save({ [seg.dataset.key]: b.dataset.v }))));
  $$('select[data-key]', page).forEach(el => el.addEventListener('change', () => save({ [el.dataset.key]: el.value || null })));
  $$('input[type=range][data-key]', page).forEach(el => {
    const out = el.parentElement.querySelector('output');
    el.addEventListener('input', () => { out.textContent = (el.value / 1000).toFixed(1) + ' s'; });
    el.addEventListener('change', () => save({ [el.dataset.key]: +el.value }));
  });
  const words = $('input[data-key=wakeWords]', page);
  if (words) {
    const commit = () => save({ wakeWords: words.value.split(',').map(w => w.trim()).filter(Boolean) });
    words.addEventListener('change', commit);
  }

  const hk = $('.hotkey-input', page);
  if (hk) {
    hk.addEventListener('focus', () => { hk.value = ''; hk.placeholder = t('set_hotkey_capture'); hk.classList.add('capturing'); });
    hk.addEventListener('blur', () => { hk.classList.remove('capturing'); hk.value = App.settings.hotkey; });
    hk.addEventListener('keydown', e => {
      e.preventDefault();
      if (e.key === 'Escape') { hk.blur(); return; }
      const key = codeToKey(e.code);
      const mods = [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.metaKey && 'Win'].filter(Boolean);
      hk.value = [...mods, key || ''].filter(Boolean).join('+');
      if (!key) return;
      const combo = [...mods, key].join('+');
      hk.blur();
      save({ hotkey: combo });
    });
  }

  $('#apiKeySave', page)?.addEventListener('click', async () => {
    const key = $('#apiKeyInput').value.trim();
    if (!key) return;
    await Api.call('setApiKey', { key }).catch(toastError);
    App.settings.apiKeySet = true;
    await refreshInit();
  });
  $('#apiKeyClear', page)?.addEventListener('click', async () => {
    await Api.call('setApiKey', { key: null }).catch(toastError);
    await refreshInit();
  });
  $('#testBrain', page)?.addEventListener('click', async e => {
    const btn = e.currentTarget;
    const out = $('#testResult');
    btn.disabled = true;
    out.className = 'note';
    out.textContent = t('testing');
    const r = await Api.call('testBrain').catch(err => ({ ok: false, message: err.message }));
    btn.disabled = false;
    out.className = 'note ' + (r.ok ? 'ok' : 'err');
    out.textContent = (r.ok ? '✓ ' : '✗ ') + r.message + (r.seconds ? ` (${r.seconds}s)` : '');
  });
  $('#testVoice', page)?.addEventListener('click', () => Api.call('speak', { text: t('voice_sample'), lang: I18n.lang }));
  $$('[data-open]', page).forEach(b => b.addEventListener('click', () => Api.call('open', { what: b.dataset.open })));
  $('#refreshApps', page)?.addEventListener('click', async () => {
    const st = await Api.call('refreshApps');
    App.status = { ...App.status, ...st };
    toast(t('apps_found', st.apps));
    renderSidebarStatus();
  });
  $('#exitApp', page)?.addEventListener('click', () => Api.call('exit'));
}

async function refreshInit() {
  const init = await Api.call('init');
  App.settings = init.settings;
  App.status = init.status;
  renderSettings();
  renderSidebarStatus();
  renderStage();
}

function codeToKey(code) {
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit\d$/.test(code)) return code.slice(5);
  if (/^F\d{1,2}$/.test(code)) return code;
  if (/^Numpad\d$/.test(code)) return 'NumPad' + code.slice(6);
  const map = {
    Space: 'Space', Enter: 'Enter', Insert: 'Insert', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown',
    Pause: 'Pause', ScrollLock: 'Scroll', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
    Backquote: 'OemTilde', Minus: 'OemMinus', Equal: 'OemPlus', Comma: 'OemComma', Period: 'OemPeriod',
    Slash: 'OemQuestion', Semicolon: 'OemSemicolon', Quote: 'OemQuotes', BracketLeft: 'OemOpenBrackets',
    BracketRight: 'OemCloseBrackets', Backslash: 'OemPipe', Tab: 'Tab', Backspace: 'Back', Delete: 'Delete',
  };
  return map[code] || null;
}

// ---------------------------------------------------------------- toast

let toastTimer;
function toast(msg, kind = '') {
  const el = $('#toast');
  el.textContent = msg;
  el.className = 'toast ' + kind;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 4200);
}
const toastError = err => toast(err?.message || String(err), 'err');

boot();
