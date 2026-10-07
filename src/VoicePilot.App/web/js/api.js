/* Bridge to .NET inside WebView2. Opened in a normal browser, it falls back to the mock (sample data). */
'use strict';

const Api = (() => {
  const wv = window.chrome && window.chrome.webview;
  const pending = new Map();
  const listeners = new Map();
  let seq = 0;

  function dispatch(event, data) {
    (listeners.get(event) || []).forEach(fn => { try { fn(data); } catch (err) { console.error(err); } });
  }

  if (wv) {
    wv.addEventListener('message', e => {
      const m = e.data;
      if (!m) return;
      if (m.id != null && pending.has(m.id)) {
        const { resolve, reject } = pending.get(m.id);
        pending.delete(m.id);
        if (m.ok) resolve(m.result);
        else reject(new Error(m.error || 'Unknown error'));
      } else if (m.event) {
        dispatch(m.event, m.data);
      }
    });
  } else {
    window.MockApi.attach(dispatch);
  }

  function call(method, params = {}) {
    if (!wv) return window.MockApi.call(method, params);
    return new Promise((resolve, reject) => {
      const id = ++seq;
      pending.set(id, { resolve, reject });
      wv.postMessage({ id, method, params });
    });
  }

  function on(event, fn) {
    if (!listeners.has(event)) listeners.set(event, []);
    listeners.get(event).push(fn);
  }

  return { call, on, isMock: !wv };
})();
