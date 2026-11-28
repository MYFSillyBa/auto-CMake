import { $, button, el, modal, t } from './ui.mjs';

export function pluginDialog(state, engines, trusted, save) {
  const body = el('div', 'form-grid');
  body.append(el('p', 'muted', t('协议扩展通过 VS Code 扩展 API 注册。已加载：') + ' ' + (engines.join(' · ') || t('仅内建引擎'))));
  const name = el('input');
  name.placeholder = t('自定义仪表名称');
  name.setAttribute('aria-label', 'Custom widget name');
  const source = el('textarea');
  source.value = 'onSamples(({latest}) => {\n  output.textContent = "I0 = " + (latest[0] ?? "—");\n});';
  source.rows = 7;
  source.setAttribute('aria-label', 'Custom widget JavaScript');
  source.spellcheck = false;
  body.append(name, source, el('p', 'muted', t('仪表代码在隔离的 iframe 内运行。可使用 output、onSamples(fn) 和 send(params)。发送仅在控件绑定命令后可用。')));
  for (const plugin of state.plugins) {
    const item = el('div', 'command-tools');
    item.append(el('span', '', plugin.name + (plugin.enabled ? '' : ' · 已停用')), button('编辑', () => {
      name.value = plugin.name;
      source.value = plugin.source;
      name.dataset.id = plugin.id;
    }), button('删除', () => {
      state.plugins = state.plugins.filter(entry => entry.id !== plugin.id);
      save();
      pluginDialog(state, engines, trusted, save);
    }));
    body.append(item);
  }
  const add = button('保存并启用', () => {
    if (!name.value.trim() || !source.value.trim() || source.value.length > 50000) return;
    const plugin = { id: name.dataset.id || crypto.randomUUID(), name: name.value.trim(), source: source.value, enabled: true };
    state.plugins = [...state.plugins.filter(item => item.id !== plugin.id), plugin];
    save();
    $('#dialog').close();
  }, 'primary');
  add.disabled = !trusted;
  if (!trusted) body.append(el('p', 'hint', '信任工作区后可以启用自定义仪表。'));
  modal('协议和控件扩展', body, [add]);
}

export function mountPlugin(container, plugin, config, api) {
  const frame = el('iframe', 'custom-instrument');
  const token = crypto.randomUUID();
  const nonce = document.querySelector('script[nonce]').nonce;
  frame.setAttribute('sandbox', 'allow-scripts');
  frame.title = plugin.name;
  let latest;
  let ready = false;
  function onMessage(event) {
    if (event.source !== frame.contentWindow || event.data?.token !== token) return;
    if (event.data.type === 'ready') {
      ready = true;
      if (latest) frame.contentWindow.postMessage({ token, latest }, '*');
    } else if (event.data.type === 'send' && config.commandId && Array.isArray(event.data.params)) {
      api.send(config.commandId, event.data.params.slice(0, 10));
    }
  }
  window.addEventListener('message', onMessage);
  const source = plugin.source.replace(/<\/script/gi, '<\\/script');
  frame.srcdoc = `<!doctype html><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-${nonce}'; style-src 'nonce-${nonce}'"><style nonce="${nonce}">body{font:14px system-ui;color:#dbe9f5;background:#111722;padding:10px;overflow-wrap:anywhere}</style><div id="output"></div><script nonce="${nonce}">
    const token = ${JSON.stringify(token)};
    const output = document.getElementById('output');
    const onSamples = callback => addEventListener('message', event => {
      if (event.source === parent && event.data?.token === token) callback({ latest: event.data.latest });
    });
    const send = params => parent.postMessage({ token, type: 'send', params }, '*');
    addEventListener('error', event => { output.textContent = event.message; });
    parent.postMessage({ token, type: 'ready' }, '*');
  </script><script nonce="${nonce}">${source}</script>`;
  container.append(frame);
  return {
    update(data) {
      latest = data.latest;
      if (ready) frame.contentWindow.postMessage({ token, latest }, '*');
    },
    dispose() {
      window.removeEventListener('message', onMessage);
      frame.remove();
    }
  };
}
