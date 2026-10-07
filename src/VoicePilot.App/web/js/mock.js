/* Sample data and a fake assistant, used only when the UI is opened in a normal browser (design preview). */
'use strict';

window.MockApi = (() => {
  let emit = () => {};
  const now = Date.now();
  const iso = ms => { const d = new Date(ms); return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 19); };
  const params = new URLSearchParams(location.search);
  const lang = params.get('lang') === 'en' ? 'en' : 'tr';

  const settings = {
    hotkey: 'Ctrl+Alt+Space', micDevice: null, wakeWord: false, wakeWords: ['pilot', 'hey pilot'], silenceMs: 900,
    language: 'auto', model: 'large-v3-turbo', useGpu: true, brain: 'auto', claudeModel: 'claude-opus-5-5', cliModel: 'sonnet',
    speak: 'minimal', sounds: true, overlay: true, confirmRisky: true, allowPowerShell: true, uiLanguage: lang, theme: params.get('theme') || 'dark',
    minimizeToTray: true, startWithWindows: false, apiKeySet: false, envKeySet: false,
    commands: [
      { phrase: 'çalışma modu', steps: ['Visual Studio Code\'u aç', 'Chrome\'u aç', 'sesi yüzde 30 yap'] },
      { phrase: 'good night', steps: ['mute', 'lock the computer'] },
    ],
  };

  const status = {
    state: params.get('state') || 'idle', detail: '', progress: null, model: 'large-v3-turbo', sttStatus: 'ready', backend: 'Vulkan',
    brain: { mode: 'cli', label: 'Claude Code', ready: true, detail: 'sonnet' }, cliFound: true, apps: 261,
    hotkey: 'Ctrl+Alt+Space', hotkeyError: null, wake: false, restartNeeded: false,
  };

  const history = [
    { id: 'a1', time: iso(now - 40e3), text: 'Not Defteri\'ni aç ve alışveriş listesi yaz: süt, yumurta, ekmek', input: 'voice', lang: 'tr', source: 'claude-cli', status: 'ok',
      reply: 'Not Defteri\'ni açıp listeyi yazıyorum.', sttSeconds: 0.92, thinkSeconds: 4.8, totalSeconds: 7.1,
      steps: [
        { text: 'open_app "Not Defteri"', kind: 'open_app', status: 'ok', message: 'Not Defteri açılıyor.' },
        { text: 'wait 1500', kind: 'wait', status: 'ok', message: '1500 ms' },
        { text: 'type_text "Alışveriş listesi:\\n- süt\\n- yumurta\\n- ekmek"', kind: 'type_text', status: 'ok', message: 'Yazıldı.' },
      ] },
    { id: 'a2', time: iso(now - 3 * 60e3), text: 'Sesi yüzde 40 yap', input: 'voice', lang: 'tr', source: 'local', status: 'ok', reply: '', sttSeconds: 0.88, thinkSeconds: 0, totalSeconds: 0.95,
      steps: [{ text: 'volume set 40', kind: 'volume', status: 'ok', message: 'Ses yüzde 40.' }] },
    { id: 'a3', time: iso(now - 9 * 60e3), text: 'YouTube\'da lofi müzik ara', input: 'wake', lang: 'tr', source: 'local', status: 'ok', reply: '', sttSeconds: 0.9, thinkSeconds: 0, totalSeconds: 1.0,
      steps: [{ text: 'search "lofi müzik" youtube', kind: 'search', status: 'ok', message: 'lofi müzik aranıyor.' }] },
    { id: 'a4', time: iso(now - 26 * 60e3), text: 'Discord\'u kapat', input: 'voice', lang: 'tr', source: 'local', status: 'error', reply: '', sttSeconds: 0.9, thinkSeconds: 0, totalSeconds: 0.97,
      steps: [{ text: 'close_app "Discord"', kind: 'close_app', status: 'error', message: 'Discord açık değil.' }] },
    { id: 'a5', time: iso(now - 50 * 60e3), text: 'What time is it?', input: 'typed', lang: 'en', source: 'local', status: 'ok', reply: '', sttSeconds: 0, thinkSeconds: 0, totalSeconds: 0.01,
      steps: [{ text: 'say "It\'s 8:16 PM."', kind: 'say', status: 'ok', message: 'It\'s 8:16 PM.' }] },
  ];

  let levelTimer = null;

  function setState(state, detail = '') {
    status.state = state;
    status.detail = detail;
    emit('state', { state, detail, progress: status.progress });
  }

  function fakeRun(text, input) {
    const entry = { id: 'm' + Date.now(), time: iso(Date.now()), text, input, lang, source: 'local', status: 'running', reply: '', steps: [], sttSeconds: input === 'voice' ? 0.9 : 0, thinkSeconds: 0, totalSeconds: 0 };
    history.unshift(entry);
    emit('entry', { ...entry });
    setState('thinking', text);
    setTimeout(() => {
      entry.steps = [{ text: 'open_app "Not Defteri"', kind: 'open_app', status: 'running', message: '' }];
      emit('entry', JSON.parse(JSON.stringify(entry)));
      setState('executing', 'open_app "Not Defteri"');
      setTimeout(() => {
        entry.steps[0].status = 'ok';
        entry.steps[0].message = 'Not Defteri açılıyor.';
        entry.status = 'ok';
        entry.totalSeconds = 1.2;
        emit('entry', JSON.parse(JSON.stringify(entry)));
        setState('idle');
      }, 700);
    }, 600);
  }

  function toggle() {
    if (status.state === 'listening') { stopListening(); return; }
    setState('listening');
    let t = 0;
    levelTimer = setInterval(() => { t += 1; emit('level', 0.2 + 0.4 * Math.abs(Math.sin(t / 4)) + Math.random() * 0.3); }, 50);
    setTimeout(() => { if (status.state === 'listening') stopListening(); }, 2600);
  }

  function stopListening() {
    clearInterval(levelTimer);
    emit('level', 0);
    setState('transcribing');
    setTimeout(() => fakeRun(lang === 'tr' ? 'Not Defteri\'ni aç' : 'Open Notepad', 'voice'), 700);
  }

  const handlers = {
    init: () => ({
      version: '1.0.0', settings, status, history, dark: settings.theme !== 'light', uiLang: lang, dataDir: 'C:\\Users\\you\\AppData\\Roaming\\VoicePilot',
      devices: ['Mikrofon (Realtek(R) Audio)', 'Headset Microphone (HyperX Cloud II)'],
      models: [
        { id: 'base', sizeMb: 148, note: 'fastest, weak Turkish', downloaded: false },
        { id: 'small', sizeMb: 190, note: 'fast, decent', downloaded: false },
        { id: 'medium', sizeMb: 539, note: 'accurate, slower on CPU', downloaded: false },
        { id: 'large-v3-turbo', sizeMb: 574, note: 'best accuracy, needs a GPU to be quick', downloaded: true },
      ],
      voices: ['Microsoft Tolga (tr-TR)', 'Microsoft Zira Desktop (en-US)'],
    }),
    status: () => status,
    toggle: () => { toggle(); return null; },
    cancel: () => { setState('idle'); return null; },
    run: p => { fakeRun(p.text, 'typed'); return null; },
    confirm: () => { emit('confirm', null); setState('idle'); return null; },
    history: () => history,
    clearHistory: () => { history.length = 0; return null; },
    saveSettings: p => { Object.assign(settings, p); return handlers.init(); },
    saveCommands: p => { settings.commands = p.commands; return handlers.init(); },
    setApiKey: p => { settings.apiKeySet = !!p.key; return status; },
    testBrain: () => new Promise(r => setTimeout(() => r({ ok: true, message: lang === 'tr' ? 'Evet, seni duyuyorum.' : 'Yes, I can hear you.', seconds: 3.4 }), 900)),
    reloadModel: () => null,
    refreshApps: () => status,
    speak: () => null,
    open: () => null,
    hide: () => null,
    exit: () => null,
  };

  function call(method, params) {
    const h = handlers[method];
    if (!h) return Promise.reject(new Error('mock: ' + method));
    return new Promise(resolve => setTimeout(() => resolve(h(params || {})), 40));
  }

  function attach(dispatch) {
    emit = dispatch;
    if (params.get('state') === 'confirming') {
      setTimeout(() => emit('confirm', { text: lang === 'tr' ? 'Bir PowerShell komutu çalıştıracağım' : 'I\'ll run a PowerShell command', detail: '(Get-ChildItem ([Environment]::GetFolderPath("Desktop"))).Count', lang }), 300);
    }
    if (params.get('state') === 'loading') {
      status.state = 'loading';
      status.detail = 'downloading';
      let p = 0.12;
      setInterval(() => { p = Math.min(0.99, p + 0.01); status.progress = p; emit('state', { state: 'loading', detail: 'downloading', progress: p }); }, 400);
    }
  }

  return { call, attach };
})();
