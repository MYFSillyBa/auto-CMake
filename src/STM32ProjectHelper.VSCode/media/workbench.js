'use strict';
(() => {
  const vscode = acquireVsCodeApi();
  const content = document.getElementById('content');
  let current;
  const send = (action, detail = {}) => vscode.postMessage({ action, ...detail });
  const element = (tag, text, className) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = String(text); if (className) node.className = className; return node; };
  const append = (parent, ...nodes) => { parent.append(...nodes.filter(Boolean)); return parent; };
  const button = (text, action, detail = {}, secondary = false) => {
    const node = element('button', text, secondary ? 'secondary' : ''); node.disabled = current.busy || !!current.unavailable;
    node.addEventListener('click', () => send(action, detail)); return node;
  };
  const section = (title, hint) => { const node = element('section', undefined, 'card'); append(node, element('h2', title)); if (hint) append(node, element('p', hint, 'muted')); content.append(node); return node; };
  const actions = (parent, ...buttons) => append(parent, append(element('div', undefined, 'actions'), ...buttons));
  const text = (parent, title, value) => append(parent, append(element('div', undefined, 'detail'), element('span', title, 'muted'), element('span', value || '—')));
  const field = (parent, key, title, value, choices, hint) => {
    const wrapper = element('label', undefined, 'field'); append(wrapper, element('span', title));
    const input = element(choices ? 'select' : 'input'); input.disabled = current.busy || !!current.unavailable;
    if (choices) for (const choice of choices) { const item = typeof choice === 'string' ? { value: choice, label: choice } : choice; const option = element('option', item.label); option.value = item.value; input.append(option); }
    else input.type = 'text';
    input.value = value || ''; input.dataset.field = key; input.addEventListener('change', () => send('setOption', { key, value: input.value }));
    append(wrapper, input); if (hint) append(wrapper, element('small', hint, 'muted')); parent.append(wrapper); return input;
  };
  const configurationField = (parent, state) => {
    const fixed = state.fixedConfiguration;
    const input = field(parent, 'configuration', '构建配置', state.options.configuration, fixed ? [fixed] : ['Debug', 'Release'], fixed ? '配置由所选预设固定；切换预设可更改。' : undefined);
    if (fixed) input.disabled = true;
  };
  const list = (parent, values, empty = '暂无内容。') => {
    if (!values?.length) { append(parent, element('p', empty, 'muted')); return; }
    const node = element('ul', undefined, 'file-list'); for (const value of values) append(node, element('li', value)); parent.append(node);
  };
  const issues = (parent, values) => {
    for (const issue of values || []) {
      const node = element('div', undefined, issue.blocking || issue.blocksConfiguration ? 'issue error' : 'issue');
      append(node, element('strong', issue.title || issue.message || issue.key || '提示'));
      if (issue.action || issue.instructions) append(node, element('p', issue.action || issue.instructions)); parent.append(node);
      const setting = { CMake: 'cMake', Ninja: 'ninja', Compiler: 'compiler', OpenOcd: 'openOcd' }[issue.key];
      if (setting) actions(node, button('选择版本…', 'pickTool', { key: setting }, true));
    }
  };
  const toolRows = (panel, state) => {
    const detected = state.inspection?.tools || state.toolInventory?.tools || {};
    for (const [key, title, wire, backendKey] of [['cMake', 'CMake', 'cmake', 'CMake'], ['ninja', 'Ninja', 'ninja', 'Ninja'], ['compiler', 'ARM GCC', 'compiler', 'Compiler'], ['openOcd', 'OpenOCD', 'openOcd', 'OpenOcd']]) {
      const pending = state.inspection?.pendingTools?.find(item => item.key === backendKey);
      const version = state.inspection?.versions?.find(item => item.key === backendKey);
      const row = element('div', undefined, 'tool-row');
      const info = append(element('div'), element('strong', title), element('p', pending ? `待选择 · 检测到 ${pending.candidates.length} 个安装` : version?.available ? version.version : '未通过检测', pending ? 'warning' : 'muted'));
      const executable = detected[wire] || current.configuredTools[wire];
      if (executable) info.append(element('p', executable, 'muted'));
      append(row, info, button('选择版本…', 'pickTool', { key }, true)); panel.append(row);
    }
  };
  const preview = state => {
    const plan = state.preview; if (!plan) return;
    const panel = section('文件变更预览', plan.summary || plan.label);
    if (plan.outputProject) text(panel, '输出工程', plan.outputProject);
    if (plan.sourceCount !== undefined) text(panel, '源文件 / 头文件目录', `${plan.sourceCount} / ${plan.includeCount}`);
    issues(panel, plan.issues); list(panel, plan.changes?.map(change => `${change.before === null ? '新增' : change.after === null ? '删除' : '修改'} · ${change.path}`), '没有文件需要修改。');
    if (plan.conflicts?.length) { append(panel, element('p', '这些文件之后有修改，还原需要确认覆盖：', 'warning')); list(panel, plan.conflicts); }
    const apply = button(plan.kind === 'restore' ? '确认还原记录…' : '确认写入…', plan.kind === 'restore' ? 'restore' : 'apply');
    if (plan.canApply === false || plan.issues?.some(issue => issue.blocking) || !plan.changes?.length) apply.disabled = true;
    actions(panel, button('打开全部文件差异', 'diff', {}, true), apply);
  };
  function configure(state) {
    const project = state.inspection?.project || {}, options = state.options;
    const panel = section('工程配置', '生成 VS Code 编译和烧录任务；软件检查只进行配置、编译与脚本解析。');
    text(panel, '芯片', project.chip); if (project.chipEvidence) text(panel, '识别依据', project.chipEvidence);
    const presets = project.configurePresets || [];
    field(panel, 'configurePreset', 'CMake 配置预设', options.configurePreset, [{ value: '', label: '不使用预设' }, ...presets.map(p => ({ value: p.name, label: p.displayName || p.name }))]);
    const preset = presets.find(p => p.name === options.configurePreset);
    field(panel, 'buildPreset', '构建预设', options.buildPreset, [{ value: '', label: '自动检测' }, ...(preset?.buildPresets || []).map(p => ({ value: p.name, label: p.displayName || p.name }))]);
    configurationField(panel, state);
    field(panel, 'buildDirectory', '构建目录', options.buildDirectory);
    field(panel, 'targetScript', 'OpenOCD 目标脚本', options.targetScript);
    if (options.firmwareElfPath) text(panel, '已选择的固件', options.firmwareElfPath);
    issues(panel, state.inspection?.issues); list(panel, project.notes, '');
    actions(panel, button('确认写入…', 'writeTasks'), button('预览改动', 'previewTasks', {}, true), button('运行软件检查', 'verify', {}, true));
    const environment = section('构建工具', '检测到多个安装时请选择使用的版本；工具路径验证通过后保存到当前工作区。');
    toolRows(environment, state);
    actions(environment, button('修复缺失工具…', 'repair', {}, true));
    if (state.verification) {
      const result = state.verification, checks = section(result.success ? '软件检查通过' : '软件检查结果');
      text(checks, 'OpenOCD 脚本解析', result.openOcdChecked ? '已检查' : '未检查');
      list(checks, result.firmware, '尚无固件输出。');
      for (const problem of result.problems || []) append(checks, element('p', [problem.stage, problem.message, problem.suggestion].filter(Boolean).join(' · '), /warning|警告/i.test(problem.severity) ? 'warning' : 'error'));
      append(checks, element('p', '烧录请通过“终端 → 运行任务”手动选择生成的烧录任务。', 'muted'));
    }
    preview(state);
  }
  function sources(state) {
    const panel = section('添加源文件', '扫描当前工作区内的文件夹，选择源文件及头文件，再更新所选 CMake 构建目标。已经注册的文件会自动去重。');
    text(panel, '源文件夹', state.folder);
    actions(panel, button('选择源文件夹…', 'pickSourceFolder'), button('重新扫描', 'scanSources', {}, true));
    const targets = state.scan?.targets || state.inspection?.targets || [];
    field(panel, 'sourceTarget', '构建目标', state.options.sourceTarget, targets);
    if (state.scan) {
      list(panel, state.scan.notes, '');
      const registered = section('已注册文件'); list(registered, state.scan.alreadyRegisteredFiles, '没有已注册文件。');
      const selection = section('选择文件', `${state.selectedFiles.length} 个已选择 · ${(state.scan.candidateFiles || []).length} 个可选文件`);
      const search = element('input'); search.type = 'search'; search.placeholder = '搜索文件名或路径'; search.value = state.options.search || ''; search.setAttribute('aria-label', '搜索源文件'); search.disabled = current.busy; selection.append(search);
      actions(selection, button('全选', 'selectAllSources', { selected: true }, true), button('清空选择', 'selectAllSources', { selected: false }, true));
      const rows = element('div', undefined, 'source-list'); const selected = new Set(state.selectedFiles), registeredFiles = new Set(state.scan.alreadyRegisteredFiles || []);
      for (const file of state.scan.candidateFiles || []) {
        const row = element('label', undefined, 'source-row'); row.dataset.path = file.toLowerCase();
        const check = element('input'); check.type = 'checkbox'; check.checked = selected.has(file); check.disabled = current.busy;
        check.addEventListener('change', () => send('selectSource', { file, selected: check.checked })); append(row, check, element('span', file + (registeredFiles.has(file) ? ' · 已注册' : ''))); rows.append(row);
      }
      const filter = () => { for (const row of rows.children) row.hidden = !row.dataset.path.includes(search.value.toLowerCase()); };
      search.addEventListener('input', filter); search.addEventListener('change', () => send('setOption', { key: 'search', value: search.value })); filter(); selection.append(rows);
      actions(selection, button('确认写入…', 'writeSources'), button('预览改动', 'previewSources', {}, true));
    }
    preview(state);
  }
  function importFiles(state) {
    const panel = section('加入文件', '从本地选择文件并复制到当前工程的指定子文件夹；同名文件会使用新的名称。');
    field(panel, 'folderName', '工程子文件夹名称', state.options.folderName, undefined, '仅填写单个文件夹名称。');
    actions(panel, button('选择本地文件…', 'pickImportFiles'), button('预览复制计划', 'previewImport', {}, true)); list(panel, state.sourceFiles, '尚未选择文件。');
    if (state.importPreview?.plan) {
      const plan = state.importPreview.plan, preview = section('文件复制计划', state.importPreview.summary);
      text(preview, '目标文件夹', plan.folderPath); text(preview, '目标文件夹状态', plan.folderExisted ? '已经存在，复制前需要确认追加' : '将创建');
      const table = element('table'); append(table, append(element('thead'), append(element('tr'), element('th', '来源'), element('th', '目标'))));
      const body = element('tbody'); for (const file of plan.files) append(body, append(element('tr'), element('td', file.sourcePath), element('td', file.destinationPath))); append(preview, append(table, body));
      actions(preview, button('打开全部复制差异', 'importDiff', {}, true), button('确认复制…', 'importFiles'));
    }
  }
  function conversion(state) {
    const panel = section('工程转换', state.options.direction === 'CMakeToMdk' ? 'CMake → MDK 检查会运行 CMake 配置并生成构建缓存，以读取目标列表；执行前需要原生确认。随后可预览转换输出。' : '检查工程、选择构建目标，再预览全部输出文件与兼容性问题。');
    field(panel, 'direction', '转换方向', state.options.direction, [{ value: 'MdkToCMake', label: 'MDK → CMake' }, { value: 'CMakeToMdk', label: 'CMake → MDK' }]);
    configurationField(panel, state);
    actions(panel, button(state.options.direction === 'CMakeToMdk' ? '配置并检查转换目标…' : '检查转换工程', 'inspectConversion'));
    if (state.conversion) {
      const info = state.conversion.inspection || state.conversion;
      text(panel, '芯片', info.chip); issues(panel, info.issues);
      field(panel, 'target', '构建目标', state.options.target, info.targets || state.conversion.targets || []);
      if ((info.sourceProjects || state.conversion.sourceProjects)?.length) field(panel, 'sourceProject', 'MDK 工程', state.options.sourceProject, info.sourceProjects || state.conversion.sourceProjects);
      field(panel, 'buildDirectory', '构建目录', state.options.buildDirectory);
      actions(panel, button('确认写入…', 'writeConversion'), button('预览改动', 'previewConversion', {}, true));
    }
    preview(state);
  }
  function tools(state) {
    const panel = section('工具路径', '工具路径保存在当前工作区设置中。离线导入和版本激活使用本地工具管理目录。');
    const detected = state.inspection?.tools || state.toolInventory?.tools || {};
    toolRows(panel, state);
    text(panel, '内置 OpenOCD scripts', detected.scripts);
    append(panel, element('p', 'OpenOCD 使用随扩展附带的完整脚本目录。', 'muted'));
    for (const version of state.inspection?.versions || []) text(panel, version.key, version.available ? version.version || version.path : version.problem || '不可用');
    issues(panel, state.inspection?.issues);
    actions(panel, button('修复缺失工具…', 'repair'), button('导入离线 ZIP…', 'importTool', {}, true), button('刷新版本列表', 'tools', {}, true));
    if (state.repair?.problems?.length) list(panel, state.repair.problems);
    const inventory = section('工具版本'); text(inventory, '工具管理目录', state.toolInventory?.root || state.inspection?.toolsRoot);
    const installations = state.toolInventory?.installations || [];
    if (!installations.length) list(inventory, [], '尚未发现本地工具版本，可手动选择路径或导入工具包。');
    for (const [index, installation] of installations.entries()) {
      const row = element('div', undefined, 'tool-row');
      const activate = button(installation.isActive ? '当前使用' : '使用此版本', 'activateTool', { index }, true);
      if (installation.isActive || installation.version === '未通过版本验证') activate.disabled = true;
      append(row, append(element('div'), element('strong', `${installation.key} ${installation.version}`), element('p', installation.isManaged ? '工具管理目录' : '本机已安装', 'muted'), element('p', installation.executable, 'muted')), activate); inventory.append(row);
    }
  }
  function history(state) {
    const panel = section('修改记录', '选择记录查看还原差异；文件之后有修改时，将要求确认覆盖。'); actions(panel, button('刷新修改记录', 'history', {}, true));
    const records = state.history?.records || [];
    if (!records.length) list(panel, [], '当前工程尚无修改记录。');
    for (const record of records) {
      const row = element('div', undefined, 'history-row');
      const restore = button('预览还原', 'previewRestore', { id: record.id }, true); if (record.restoredUtc) restore.disabled = true;
      append(row, append(element('div'), element('strong', record.label), element('p', `${new Date(record.createdUtc).toLocaleString()} · ${record.changes?.length || 0} 个文件 · ${record.restoredUtc ? '已还原' : record.status || ''}`, 'muted')), restore); panel.append(row);
    }
    preview(state);
  }
  const renderers = { configure, sources, import: importFiles, conversion, tools, history };
  window.addEventListener('message', event => {
    const message = event.data; if (message?.type !== 'state') return;
    const focused = document.activeElement?.dataset.field;
    current = message;
    document.getElementById('workspace').textContent = message.root || '尚未打开工作区';
    document.getElementById('status').textContent = message.unavailable || message.state.status;
    document.getElementById('status').className = message.unavailable ? 'error' : message.busy ? 'running' : '';
    document.getElementById('refresh').disabled = message.busy || !!message.unavailable;
    document.getElementById('cancel').hidden = !message.busy;
    const navigation = document.getElementById('navigation'); navigation.replaceChildren();
    for (const [id, title] of message.pages) {
      const item = element('button', title, id === message.page ? 'active' : ''); item.disabled = message.busy;
      item.setAttribute('aria-current', id === message.page ? 'page' : 'false'); item.addEventListener('click', () => send('navigate', { page: id })); navigation.append(item);
    }
    content.replaceChildren();
    if (message.unavailable) { const panel = section('打开当前工程', message.unavailable); const pick = element('button', '选择工作区…'); pick.addEventListener('click', () => send('workspace')); panel.append(pick); }
    else renderers[message.page]?.(message.state);
    if (focused) [...content.querySelectorAll('[data-field]')].find(node => node.dataset.field === focused)?.focus();
  });
  document.getElementById('refresh').addEventListener('click', () => send('refresh'));
  document.getElementById('logs').addEventListener('click', () => send('logs'));
  document.getElementById('cancel').addEventListener('click', () => send('cancel'));
  send('ready');
})();
