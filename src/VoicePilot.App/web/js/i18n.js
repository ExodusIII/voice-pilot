/* UI strings (Turkish / English) and the example commands. */
'use strict';

const I18n = (() => {
  const S = {
    tagline: ['Bilgisayarını sesle yönet', 'Control your PC by voice'],
    nav_home: ['Komuta merkezi', 'Command center'],
    nav_commands: ['Komutlar', 'Commands'],
    nav_settings: ['Ayarlar', 'Settings'],
    yes: ['Evet, yap', 'Yes, do it'],
    no: ['Vazgeç', 'Cancel'],
    confirm_hint: ['"Evet" ya da "hayır" diyerek de cevap verebilirsin.', 'You can also answer by saying "yes" or "no".'],
    ask_placeholder: ['Ya da komutu buraya yaz…  ör. "Chrome\'u aç ve YouTube\'a git"', 'Or type a command…  e.g. "open Chrome and go to YouTube"'],
    recent: ['Son komutlar', 'Recent commands'],
    clear: ['Temizle', 'Clear'],
    empty_feed: ['Henüz komut yok. Kısayola bas ve bir şey söyle.', 'No commands yet. Press the shortcut and say something.'],
    save: ['Kaydet', 'Save'],
    saved: ['Kaydedildi', 'Saved'],
    unsaved: ['Kaydedilmemiş değişiklikler var', 'Unsaved changes'],

    st_loading: ['Ses modeli hazırlanıyor…', 'Preparing the speech model…'],
    st_downloading: ['Ses modeli indiriliyor', 'Downloading the speech model'],
    st_download_note: ['Sadece ilk açılışta, bir kez.', 'First launch only, just once.'],
    st_idle: ['Hazır', 'Ready'],
    st_idle_hint: ['Konuşmak için {0} tuşlarına bas ya da düğmeye tıkla', 'Press {0} or click the button to talk'],
    st_idle_wake: ['ya da "{0}" diye seslen', 'or just say "{0}"'],
    st_listening: ['Dinliyorum…', 'Listening…'],
    st_listening_hint: ['Bitince sus, ya da tekrar bas', 'Pause when you\'re done, or press again'],
    st_transcribing: ['Anlıyorum…', 'Recognizing…'],
    st_thinking: ['Düşünüyorum…', 'Thinking…'],
    st_executing: ['Yapıyorum…', 'Working…'],
    st_confirming: ['Onay bekliyorum', 'Waiting for confirmation'],
    st_error: ['Ses modeli yüklenemedi', 'The speech model failed to load'],
    retry: ['Tekrar dene', 'Try again'],
    hotkey_problem: ['{0} Ayarlar\'dan başka bir kısayol seç — şimdilik düğmeye tıklayarak konuşabilirsin.', '{0} Pick another shortcut in Settings — for now, click the button to talk.'],

    chip_model: ['Model', 'Model'],
    chip_brain: ['Anlama', 'Understanding'],
    chip_apps: ['{0} uygulama', '{0} apps'],
    brain_api: ['Claude API', 'Claude API'],
    brain_cli: ['Claude Code', 'Claude Code'],
    brain_local: ['Sadece yerel', 'Local only'],
    gpu: ['GPU', 'GPU'],
    cpu: ['CPU', 'CPU'],

    src_local: ['Yerel', 'Local'],
    src_claude: ['Claude', 'Claude'],
    'src_claude-cli': ['Claude Code', 'Claude Code'],
    src_macro: ['Özel komut', 'Custom'],
    in_voice: ['Ses', 'Voice'],
    in_typed: ['Yazı', 'Typed'],
    in_wake: ['Uyandırma', 'Wake word'],
    in_reminder: ['Hatırlatma', 'Reminder'],
    status_ok: ['Tamam', 'Done'],
    status_error: ['Hata', 'Failed'],
    status_unknown: ['Anlaşılmadı', 'Not understood'],
    status_cancelled: ['İptal', 'Cancelled'],
    status_running: ['Çalışıyor', 'Running'],
    rerun: ['Tekrar çalıştır', 'Run again'],
    no_steps: ['Yapılacak bir işlem yok', 'Nothing to do'],

    commands_intro: ['Kendi sesli komutlarını tanımla ve neler söyleyebileceğine göz at.', 'Define your own voice commands and see what you can say.'],
    custom_title: ['Özel komutlar', 'Custom commands'],
    custom_desc: ['Bir cümle söylediğinde sırayla birkaç komut çalışsın. Her satır, normalde söyleyeceğin bir komut.', 'Say one phrase, run several commands in order. Each line is a command as you would say it.'],
    add_command: ['Komut ekle', 'Add command'],
    phrase: ['Söyleyeceğin cümle', 'Phrase you say'],
    phrase_ph: ['ör. çalışma modu', 'e.g. work mode'],
    steps: ['Yapılacaklar (her satıra bir komut)', 'Steps (one command per line)'],
    steps_ph: ['Visual Studio Code\'u aç\nChrome\'u aç\nsesi yüzde 30 yap', 'open Visual Studio Code\nopen Chrome\nset volume to 30'],
    no_custom: ['Henüz özel komut yok.', 'No custom commands yet.'],
    try_it: ['Dene', 'Try'],
    remove: ['Sil', 'Remove'],
    cheatsheet_title: ['Neler söyleyebilirim?', 'What can I say?'],
    cheatsheet_desc: ['Tıkla, komut kutusuna gelsin. Claude bağlıysa bunların dışındaki serbest cümleleri de anlar.', 'Click one to put it in the command box. With Claude connected, free-form requests work too.'],

    // Settings
    sec_listening: ['Dinleme', 'Listening'],
    sec_recognition: ['Konuşma tanıma', 'Speech recognition'],
    sec_brain: ['Anlama (Claude)', 'Understanding (Claude)'],
    sec_feedback: ['Geri bildirim', 'Feedback'],
    sec_safety: ['Güvenlik', 'Safety'],
    sec_app: ['Uygulama', 'App'],

    set_hotkey: ['Kısayol tuşu', 'Shortcut'],
    set_hotkey_d: ['Her yerden dinlemeyi başlatır. Değiştirmek için kutuya tıkla ve yeni tuşlara bas.', 'Starts listening from anywhere. Click the box and press new keys to change it.'],
    set_hotkey_capture: ['Tuşlara bas…', 'Press keys…'],
    set_mic: ['Mikrofon', 'Microphone'],
    set_mic_d: ['Hangi mikrofonun dinleneceği.', 'Which microphone to listen to.'],
    mic_default: ['Windows varsayılanı', 'Windows default'],
    set_wake: ['Uyandırma kelimesi', 'Wake word'],
    set_wake_d: ['Kısayola basmadan, cümleye bu kelimeyle başlayarak komut ver: "Pilot, Spotify\'ı aç". Mikrofon sürekli açık kalır; ses bilgisayardan çıkmaz.', 'Give commands hands-free by starting with this word: "Pilot, open Spotify". The mic stays on; audio never leaves your PC.'],
    set_wake_words: ['Kelimeler (virgülle ayır)', 'Words (comma separated)'],
    set_silence: ['Bitirme sessizliği', 'End-of-command pause'],
    set_silence_d: ['Bu kadar sustuğunda komutun bittiği anlaşılır.', 'How long a pause ends the command.'],

    set_language: ['Konuştuğun dil', 'Spoken language'],
    set_language_d: ['Otomatik: Türkçe ve İngilizce arasında her seferinde seçer.', 'Auto picks Turkish or English each time.'],
    lang_auto: ['Otomatik', 'Auto'],
    set_model: ['Ses modeli', 'Speech model'],
    set_model_d: ['Whisper, tamamen bilgisayarında çalışır. Büyük model daha isabetli.', 'Whisper runs entirely on your PC. Larger models are more accurate.'],
    downloaded: ['indirildi', 'downloaded'],
    not_downloaded: ['seçilince indirilir', 'downloads when selected'],
    set_gpu: ['Ekran kartını kullan', 'Use the GPU'],
    set_gpu_d: ['Vulkan ile çok daha hızlı. Değişiklik yeniden başlatınca geçerli olur.', 'Much faster via Vulkan. Takes effect after a restart.'],
    restart_needed: ['Yeniden başlatınca geçerli olacak', 'Takes effect after restart'],
    running_on: ['Şu an: {0}', 'Currently: {0}'],

    set_brain: ['Anlama modu', 'Understanding mode'],
    set_brain_d: ['Basit komutlar her zaman anında, yerelde anlaşılır. Daha karmaşık istekler Claude\'a gider.', 'Simple commands are always understood instantly on your PC. More complex requests go to Claude.'],
    brain_auto: ['Otomatik', 'Auto'],
    brain_mode_api: ['API anahtarı', 'API key'],
    brain_mode_cli: ['Claude Code', 'Claude Code'],
    brain_mode_local: ['Kapalı', 'Off'],
    set_apikey: ['Anthropic API anahtarı', 'Anthropic API key'],
    set_apikey_d: ['Windows hesabına şifreli olarak saklanır. Yoksa yüklü Claude Code oturumun kullanılır.', 'Stored encrypted for your Windows account. Without one, your installed Claude Code login is used.'],
    apikey_set: ['Kayıtlı', 'Saved'],
    apikey_env: ['ANTHROPIC_API_KEY ortam değişkeninden', 'From the ANTHROPIC_API_KEY environment variable'],
    apikey_none: ['Kayıtlı değil', 'Not set'],
    set_claude_model: ['Claude modeli', 'Claude model'],
    set_claude_model_d: ['API ile kullanılacak model.', 'Model used with the API.'],
    set_cli_model: ['Claude Code modeli', 'Claude Code model'],
    set_cli_model_d: ['Claude Code ile kullanılacak model.', 'Model used through Claude Code.'],
    test: ['Bağlantıyı test et', 'Test connection'],
    testing: ['Deneniyor…', 'Testing…'],
    cli_missing: ['Claude Code bulunamadı', 'Claude Code not found'],
    cli_found: ['Claude Code bulundu', 'Claude Code found'],

    set_speak: ['Sesli yanıt', 'Spoken replies'],
    set_speak_d: ['Az: sadece cevaplar ve hatalar konuşulur, diğerlerinde kısa bir ses çalar.', 'Minimal: only answers and errors are spoken; otherwise a short chime.'],
    speak_full: ['Hepsi', 'All'],
    speak_minimal: ['Az', 'Minimal'],
    speak_off: ['Kapalı', 'Off'],
    set_sounds: ['Bildirim sesleri', 'Sound cues'],
    set_sounds_d: ['Dinleme başlarken ve biterken kısa sesler.', 'Short tones when listening starts and stops.'],
    set_overlay: ['Ekran göstergesi', 'On-screen indicator'],
    set_overlay_d: ['Pencere kapalıyken ekranın altında ne yaptığını gösterir.', 'Shows what\'s happening at the bottom of the screen when this window is hidden.'],
    test_voice: ['Sesi dene', 'Test voice'],
    voice_sample: ['Merhaba, ben VoicePilot. Ne yapmamı istersin?', 'Hi, I\'m VoicePilot. What can I do for you?'],

    set_confirm: ['Riskli işlemlerde onay iste', 'Confirm risky actions'],
    set_confirm_d: ['Kapatma, yeniden başlatma, zorla kapatma ve PowerShell komutlarından önce sorar.', 'Asks before shutdown, restart, force-closing apps and PowerShell commands.'],
    set_powershell: ['PowerShell komutlarına izin ver', 'Allow PowerShell commands'],
    set_powershell_d: ['Claude, başka yolu olmayan işler için PowerShell komutu önerebilir.', 'Claude may suggest a PowerShell command for things nothing else can do.'],

    set_ui_lang: ['Arayüz dili', 'Interface language'],
    set_theme: ['Tema', 'Theme'],
    theme_dark: ['Koyu', 'Dark'],
    theme_light: ['Açık', 'Light'],
    theme_system: ['Sistem', 'System'],
    set_tray: ['Kapatınca tepsiye küçült', 'Close to tray'],
    set_tray_d: ['Pencereyi kapatınca kısayol çalışmaya devam eder.', 'The shortcut keeps working after you close the window.'],
    set_startup: ['Windows ile başlat', 'Start with Windows'],
    set_startup_d: ['Oturum açınca tepside sessizce başlar.', 'Starts quietly in the tray when you sign in.'],
    data: ['Veriler', 'Data'],
    data_d: ['Ayarlar, geçmiş, modeller ve kayıt dosyası.', 'Settings, history, models and the log file.'],
    open_data: ['Ayar klasörü', 'Settings folder'],
    open_models: ['Modeller', 'Models'],
    open_log: ['Kayıt dosyası', 'Log file'],
    refresh_apps: ['Uygulama listesini yenile', 'Refresh app list'],
    apps_found: ['{0} uygulama bulundu', '{0} apps found'],
    exit_app: ['Uygulamadan çık', 'Quit app'],
  };

  // Examples for the cheat sheet: [category icon, title, examples]
  const examples = {
    tr: [
      ['app', 'Uygulamalar', ['Chrome\'u aç', 'Spotify\'ı kapat', 'Not Defteri\'ne geç', 'Hesap makinesini açar mısın', 'Discord\'u zorla kapat']],
      ['globe', 'Web', ['YouTube\'da lofi müzik ara', 'Google\'da hava durumu ara', 'github.com\'a git', 'YouTube\'u aç ve kedi videoları ara', 'Haritalar\'da yakındaki kafeleri ara']],
      ['volume', 'Ses ve medya', ['Sesi kıs', 'Sesi biraz aç', 'Sesi yüzde 40 yap', 'Sessize al', 'Müziği durdur', 'Sonraki şarkı']],
      ['window', 'Pencereler', ['Pencereyi küçült', 'Pencereyi büyüt', 'Masaüstünü göster', 'Pencereyi sola yasla', 'Diğer pencereye geç']],
      ['keyboard', 'Klavye ve yazma', ['Merhaba dünya diye yaz', 'Kontrol C\'ye bas', 'Yeni sekme aç', 'Sekmeyi kapat', 'Aşağı kaydır', 'Kaydet']],
      ['folder', 'Klasörler', ['İndirilenler klasörünü aç', 'Belgeleri aç', 'Masaüstü klasörünü aç', 'D diskini aç']],
      ['power', 'Sistem', ['Ekran görüntüsü al', 'Bilgisayarı kilitle', 'Uyku moduna al', 'Bilgisayarı yeniden başlat', 'Kapatmayı iptal et']],
      ['bell', 'Zaman ve hatırlatma', ['Saat kaç', 'Bugün ayın kaçı', '10 dakika sonra çamaşırı hatırlat', 'Yarım saat sonra hatırlat']],
      ['sparkle', 'Claude ile (serbest)', ['Not Defteri\'ni aç ve alışveriş listesi yaz: süt, yumurta, ekmek', 'Masaüstündeki dosyaları say', 'Bu pencereyi kapat ve Spotify\'da rahatlatıcı müzik aç', 'Yarın sabah toplantı için hatırlatma notu yaz']],
    ],
    en: [
      ['app', 'Apps', ['Open Chrome', 'Close Spotify', 'Switch to Notepad', 'Launch the calculator', 'Force close Discord']],
      ['globe', 'Web', ['Play lofi beats on YouTube', 'Search for pizza on Google Maps', 'Go to github.com', 'Open YouTube and search for cats', 'Look up the weather in Istanbul']],
      ['volume', 'Volume & media', ['Turn the volume down', 'Volume up a bit', 'Set volume to 40', 'Mute', 'Pause the music', 'Next song']],
      ['window', 'Windows', ['Minimize this window', 'Maximize', 'Show desktop', 'Snap the window left', 'Switch windows']],
      ['keyboard', 'Keyboard & typing', ['Type hello world', 'Press ctrl c', 'New tab', 'Close tab', 'Scroll down', 'Save']],
      ['folder', 'Folders', ['Open downloads', 'Open documents', 'Open the desktop folder', 'Open D drive']],
      ['power', 'System', ['Take a screenshot', 'Lock the computer', 'Go to sleep', 'Restart the computer', 'Cancel the shutdown']],
      ['bell', 'Time & reminders', ['What time is it', 'What\'s the date today', 'Remind me in 10 minutes to check the oven', 'Set a timer for 25 minutes']],
      ['sparkle', 'With Claude (free-form)', ['Open Notepad and write a shopping list: milk, eggs, bread', 'How many files are on my desktop?', 'Close this window and play relaxing music on Spotify', 'Write a short thank-you email in the open window']],
    ],
  };

  const quick = {
    tr: ['Saat kaç', 'Not Defteri\'ni aç', 'Sesi biraz kıs', 'YouTube\'da lofi müzik ara', 'Ekran görüntüsü al'],
    en: ['What time is it', 'Open Notepad', 'Volume down a bit', 'Play lofi beats on YouTube', 'Take a screenshot'],
  };

  let lang = 'tr';

  function t(key, ...args) {
    const v = S[key];
    let s = v ? v[lang === 'tr' ? 0 : 1] : key;
    args.forEach((a, i) => { s = s.split(`{${i}}`).join(a); });
    return s;
  }

  function apply(root = document) {
    root.querySelectorAll('[data-t]').forEach(el => { el.textContent = t(el.dataset.t); });
    root.querySelectorAll('[data-t-placeholder]').forEach(el => { el.placeholder = t(el.dataset.tPlaceholder); });
    document.documentElement.lang = lang;
  }

  return {
    t, apply, examples, quick,
    get lang() { return lang; },
    set lang(v) { lang = v === 'en' ? 'en' : 'tr'; },
  };
})();

const t = I18n.t;
