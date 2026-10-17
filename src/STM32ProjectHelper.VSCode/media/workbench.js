'use strict';
(() => {
  const vscode = acquireVsCodeApi();
  const content = document.getElementById('content');
  let current;
  let sourceView, serialView, previewHost, renderedKey, previewKey, navigationKey;
  let fieldSequence = 0;
  const views = new Map();
  let local;
  let t = window.STM32I18n.createTranslator(window.STM32UiMessages, 'zh-CN');
  const send = (action, detail = {}) => vscode.postMessage({ action, ...detail });
  const element = (tag, text, className) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = String(text); if (className) node.className = className; return node; };
  const append = (parent, ...nodes) => { parent.append(...nodes.filter(Boolean)); return parent; };
  const button = (text, action, detail = {}, secondary = false) => {
    const node = element('button', text, secondary ? 'secondary' : ''); node.dataset.busyControl = ''; node.disabled = current.busy || !!current.unavailable;
    node.addEventListener('click', () => {
      if (['build', 'flash', 'memory', 'verify', 'writeTasks', 'previewTasks', 'apply', 'wizardNext', 'wizardFinish', 'writeConversion', 'previewConversion', 'inspectConversion', 'prepareCompileDatabase', 'writeClangd', 'writeDebug', 'startDebug', 'probeCheck', 'analyzeFirmware', 'hardFault', 'rtos', 'connectSerial', 'sendSerial', 'configureTelemetry'].includes(action)) {
        const invalid = [...content.querySelectorAll('[data-field]')].find(input => !input.checkValidity());
        if (invalid) { invalid.reportValidity(); invalid.focus(); return; }
      }
      send(action, detail);
    }); return node;
  };
  const lock = (node, disabled) => { node.dataset.locked = disabled ? 'true' : ''; node.disabled = disabled || current.busy || !!current.unavailable; return node; };
  const section = (title, hint, parent = content) => { const node = element('section', undefined, 'card'); append(node, element('h2', title)); if (hint) append(node, element('p', hint, 'muted')); parent.append(node); return node; };
  const actions = (parent, ...buttons) => append(parent, append(element('div', undefined, 'actions'), ...buttons));
  const text = (parent, title, value) => append(parent, append(element('div', undefined, 'detail'), element('span', title, 'muted'), element('span', value || '—')));
  const field = (parent, key, title, value, choices, hint, multiline = false) => {
    const wrapper = element('label', undefined, 'field'); append(wrapper, element('span', title));
    const input = element(choices ? 'select' : multiline ? 'textarea' : 'input'); input.dataset.busyControl = ''; input.disabled = current.busy || !!current.unavailable;
    if (choices) for (const choice of choices) { const item = typeof choice === 'string' ? { value: choice, label: choice } : choice; const option = element('option', item.label); option.value = item.value; input.append(option); }
    else if (multiline) input.rows = 6;
    else input.type = 'text';
    input.value = local.drafts.get(key) ?? String(value ?? ''); input.dataset.field = key;
    input.addEventListener('input', () => local.drafts.set(key, input.value));
    input.addEventListener('change', () => {
      if (!input.checkValidity()) { input.reportValidity(); return; }
      if (key === 'configurePreset') for (const dependency of ['buildPreset', 'configuration', 'buildDirectory', 'firmwareElfPath']) local.drafts.delete(dependency);
      local.drafts.set(key, input.value);
      send('setOption', { key, value: input.value });
    });
    append(wrapper, input); if (hint) append(wrapper, element('small', hint, 'muted')); parent.append(wrapper); return input;
  };
  const checkbox = (parent, key, title, checked, action = 'setOption') => {
    const wrapper = element('label', undefined, 'check-field'), input = element('input');
    input.type = 'checkbox'; input.checked = !!checked; input.dataset.busyControl = ''; input.disabled = current.busy || !!current.unavailable;
    input.addEventListener('change', () => send(action, action === 'setOption' ? { key, value: String(input.checked) } : { enabled: input.checked }));
    append(wrapper, input, element('span', title)); parent.append(wrapper); return input;
  };
  const configurationField = (parent, state) => {
    const fixed = state.fixedConfiguration;
    if (fixed) local.drafts.delete('configuration');
    const presets = state.inspection?.project?.configurePresets || [];
    const choices = [...new Set([...(state.inspection?.configurations || []), state.options.configuration, fixed, ...presets.flatMap(p => [p.configureConfiguration, ...(p.buildPresets || []).map(b => b.configuration)])].filter(Boolean))];
    const input = field(parent, 'configuration', t('构建配置'), fixed || state.options.configuration, fixed ? [fixed] : undefined, fixed ? t('配置由所选预设固定；切换预设可更改。') : t('选择工程配置或输入自定义名称；支持名称内的空格和 Unicode。'));
    if (fixed) lock(input, true);
    else {
      input.required = true; input.maxLength = 128;
      const validate = () => { const value = input.value; input.setCustomValidity(value.length > 0 && value.length <= 128 && value === value.trim() && !value.endsWith('.') && !/[\u0000-\u001f\u007f-\u009f;/\\:*?"<>|]/u.test(value) ? '' : t('配置名称须为 1–128 个安全字符，不能包含控制字符、分号或路径分隔符。')); };
      input.addEventListener('input', validate); validate();
      const suggestions = element('datalist'); suggestions.id = `configuration-${++fieldSequence}`;
      for (const value of choices) { const option = element('option'); option.value = value; suggestions.append(option); }
      input.setAttribute('list', suggestions.id); parent.append(suggestions);
    }
  };
  const detailPanel = (parent, key, title) => {
    const view = local, details = element('details'); details.dataset.detail = key; details.open = view.details.get(key) || false;
    details.append(element('summary', title)); details.addEventListener('toggle', () => view.details.set(key, details.open)); parent.append(details); return details;
  };
  const probeFields = (parent, state) => {
    const options = state.options;
    field(parent, 'probeType', t('调试探针'), options.probeType || 'cmsis-dap', [{ value: 'cmsis-dap', label: 'CMSIS-DAP / DAPLink' }, { value: 'stlink', label: 'ST-Link' }, { value: 'jlink', label: 'J-Link' }]);
    const advanced = detailPanel(parent, 'probe-advanced', t('高级探针设置'));
    const speed = field(advanced, 'adapterSpeed', t('适配器速度（kHz）'), options.adapterSpeed ?? 1000, undefined, t('范围 1–50000 kHz。'));
    speed.type = 'number'; speed.min = '1'; speed.max = '50000'; speed.step = '1'; speed.required = true;
    field(advanced, 'resetMode', t('复位方式'), options.resetMode || 'default', [{ value: 'default', label: t('默认复位') }, { value: 'connect-under-reset', label: t('复位下连接') }, { value: 'sysresetreq', label: 'SYSRESETREQ' }]);
  };
  const list = (parent, values, empty = t('暂无内容。')) => {
    if (!values?.length) { append(parent, element('p', empty, 'muted')); return; }
    const node = element('ul', undefined, 'file-list'); for (const value of values) append(node, element('li', value)); parent.append(node);
  };
  const issues = (parent, values) => {
    for (const issue of values || []) {
      const node = element('div', undefined, issue.blocking || issue.blocksConfiguration ? 'issue error' : 'issue');
      append(node, element('strong', issue.title || issue.message || issue.key || t('提示')));
      if (issue.action || issue.instructions) append(node, element('p', issue.action || issue.instructions)); parent.append(node);
      const setting = { CMake: 'cMake', Ninja: 'ninja', Compiler: 'compiler', OpenOcd: 'openOcd' }[issue.key];
      if (setting) actions(node, button(t('选择版本…'), 'pickTool', { key: setting }, true));
    }
  };
  const toolRows = (panel, state) => {
    const detected = state.inspection?.tools || state.toolInventory?.tools || {};
    for (const [key, title, wire, backendKey] of [['cMake', 'CMake', 'cmake', 'CMake'], ['ninja', 'Ninja', 'ninja', 'Ninja'], ['compiler', 'ARM GCC', 'compiler', 'Compiler'], ['openOcd', 'OpenOCD', 'openOcd', 'OpenOcd']]) {
      const pending = state.inspection?.pendingTools?.find(item => item.key === backendKey);
      const version = state.inspection?.versions?.find(item => item.key === backendKey);
      const row = element('div', undefined, 'tool-row');
      const info = append(element('div'), element('strong', title), element('p', pending ? t('待选择 · 检测到 {0} 个安装', pending.candidates.length) : version?.available ? version.version : t('未通过检测'), pending ? 'warning' : 'muted'));
      const executable = detected[wire] || current.configuredTools?.[wire];
      if (executable) info.append(element('p', executable, 'muted'));
      append(row, info, button(t('选择版本…'), 'pickTool', { key }, true)); panel.append(row);
    }
  };
  const preview = state => {
    const plan = state.preview; if (!plan) return;
    const panel = section(t('文件变更预览'), plan.summary || plan.label, previewHost);
    if (plan.outputProject) text(panel, t('输出工程'), plan.outputProject);
    if (plan.sourceCount !== undefined) text(panel, t('源文件 / 头文件目录'), `${plan.sourceCount} / ${plan.includeCount}`);
    issues(panel, plan.issues);
    if (!plan.changes?.length) list(panel, [], t('没有文件需要修改。'));
    for (const [index, change] of (plan.changes || []).entries()) {
      const kind = { add: '新增', delete: '删除', modify: '修改' }[change.kind] || '修改';
      const row = element('div', undefined, 'change-row');
      append(row, append(element('div'), element('strong', `${t(kind)} · ${change.path}`), element('p', `${bytes(change.beforeSize)} → ${bytes(change.afterSize)}`, 'muted')), button(t('查看差异'), 'diff', { indices: [index] }, true)); panel.append(row);
    }
    if (plan.conflicts?.length) { append(panel, element('p', t('这些文件之后有修改，还原需要确认覆盖：'), 'warning')); list(panel, plan.conflicts); }
    const apply = button(plan.kind === 'restore' ? t('确认还原记录…') : t('确认写入…'), plan.kind === 'restore' ? 'restore' : 'apply');
    lock(apply, plan.canApply === false || plan.issues?.some(issue => issue.blocking) || !plan.changes?.length);
    actions(panel, apply);
  };
  const bytes = value => Number.isFinite(Number(value)) && value !== null && value !== undefined ? `${Number(value).toLocaleString(current.language === 'en' ? 'en' : 'zh-CN')} B` : t('未知');
  const problemRows = (parent, problems) => {
    for (const problem of problems || []) {
      const row = element('div', undefined, /warning|警告/i.test(problem.severity) ? 'issue warning' : 'issue error');
      append(row, element('strong', [problem.stage, problem.message].filter(Boolean).join(' · ')));
      if (problem.suggestion) row.append(element('p', problem.suggestion)); parent.append(row);
    }
  };
  const firmwareChoices = state => [...new Set([...(state.buildResult?.firmware || []), ...(state.flashResult?.firmware || []), ...(state.verification?.firmware || []), state.options.firmwareElfPath].filter(Boolean))];
  function setupWizard(state) {
    const wizard = state.wizard;
    if (!wizard || wizard.complete || wizard.dismissed) return false;
    const step = Math.min(3, Math.max(0, Number(wizard.step) || 0));
    const titles = ['当前工程', '环境工具', '芯片与探针', '任务预览与写入'];
    const panel = section(t('首次配置向导'), t('第 {0} / 4 步 · {1}', step + 1, t(titles[step])));
    const steps = element('ol', undefined, 'wizard-steps');
    for (const [index, title] of titles.entries()) { const item = element('li', t(title), index === step ? 'active' : index < step ? 'complete' : ''); if (index === step) item.setAttribute('aria-current', 'step'); steps.append(item); }
    panel.append(steps);
    const project = state.inspection?.project || {};
    if (step === 0) {
      text(panel, t('当前工程'), current.root); text(panel, t('芯片'), project.chip);
      if (project.chipEvidence) text(panel, t('识别依据'), project.chipEvidence);
      list(panel, project.notes, '');
      append(panel, element('p', t('确认当前工作区是要配置的工程；如有多个工程，请选择对应目录。'), 'muted'));
      actions(panel, button(t('选择工作区…'), 'workspace', {}, true), button(t('重新检测'), 'refresh', {}, true));
      if (!project.isCMakeProject) { append(panel, element('p', t('当前工程尚无 CMakeLists.txt；MDK 工程请先完成转换。'), 'warning')); actions(panel, button(t('前往工程转换'), 'navigate', { page: 'conversion' }, true)); }
      issues(panel, (state.inspection?.issues || []).filter(issue => !['CMake', 'Ninja', 'Compiler', 'OpenOcd'].includes(issue.key)));
    } else if (step === 1) {
      append(panel, element('p', t('选择要使用的 CMake、Ninja、ARM GCC 和 OpenOCD 版本。'), 'muted'));
      toolRows(panel, state); issues(panel, state.inspection?.issues);
      actions(panel, button(t('修复缺失工具…'), 'repair', {}, true));
    } else if (step === 2) {
      text(panel, t('芯片'), project.chip); if (project.chipEvidence) text(panel, t('识别依据'), project.chipEvidence);
      field(panel, 'targetScript', t('OpenOCD 目标脚本'), state.options.targetScript, undefined, t('自动识别不足时填写目标脚本路径。'));
      probeFields(panel, state);
      append(panel, element('p', t('此步骤仅保存探针与脚本设置，不连接硬件。'), 'muted'));
    } else {
      presetFields(panel, state); configurationField(panel, state);
      field(panel, 'buildDirectory', t('构建目录'), state.options.buildDirectory);
      text(panel, t('调试探针'), probeName(state.options.probeType)); text(panel, t('OpenOCD 目标脚本'), state.options.targetScript || project.chip);
      issues(panel, state.inspection?.issues);
      append(panel, element('p', t('预览下方文件列表，再确认写入编译和烧录任务。'), 'muted'));
      actions(panel, button(t('预览改动'), 'previewTasks', {}, true));
    }
    const back = lock(button(t('上一步'), 'wizardBack', {}, true), step === 0);
    const next = button(step === 3 ? t('确认写入并完成…') : t('下一步'), step === 3 ? 'wizardFinish' : 'wizardNext');
    actions(panel, back, next, button(t('手动配置'), 'dismissWizard', {}, true));
    return true;
  }
  const probeName = value => ({ stlink: 'ST-Link', jlink: 'J-Link' }[value] || 'CMSIS-DAP / DAPLink');
  function presetFields(panel, state) {
    const presets = state.inspection?.project?.configurePresets || [], options = state.options;
    field(panel, 'configurePreset', t('CMake 配置预设'), options.configurePreset, [{ value: '', label: t('不使用预设') }, ...presets.map(p => ({ value: p.name, label: p.displayName || p.name }))]);
    const preset = presets.find(p => p.name === options.configurePreset);
    field(panel, 'buildPreset', t('构建预设'), options.buildPreset, [{ value: '', label: t('自动检测') }, ...(preset?.buildPresets || []).map(p => ({ value: p.name, label: p.displayName || p.name }))]);
  }
  function execution(state) {
    const options = state.options, firmware = firmwareChoices(state);
    const panel = section(t('编译与烧录'), t('烧录会先重新编译，并在原生确认后连接所选探针。'));
    text(panel, t('芯片'), state.inspection?.project?.chip); text(panel, t('构建配置'), options.configuration); text(panel, t('构建目录'), options.buildDirectory || t('由 CMake 预设确定')); text(panel, t('调试探针'), probeName(options.probeType));
    if (firmware.length) field(panel, 'firmwareElfPath', t('固件 ELF'), options.firmwareElfPath || (firmware.length === 1 ? firmware[0] : ''), [{ value: '', label: firmware.length > 1 ? t('请选择固件目标') : t('使用唯一编译输出') }, ...firmware.map(value => ({ value, label: value }))]);
    else text(panel, t('固件 ELF'), t('编译后确定固件输出'));
    if (firmware.length > 1 && !options.firmwareElfPath) append(panel, element('p', t('存在多个固件输出；烧录或分析前请选择目标。'), 'warning'));
    actions(panel, button(t('立即编译'), 'build'), button(t('烧录…'), 'flash', {}, true), button(t('分析 Flash / RAM'), 'memory', {}, true));
    for (const [kind, result] of [['build', state.buildResult], ['flash', state.flashResult], ['verify', state.verification]]) {
      if (!result) continue;
      const title = kind === 'build' ? t('编译结果') : kind === 'flash' ? t('烧录结果') : t('软件检查结果');
      const details = detailPanel(panel, `result-${kind}`, `${title} · ${result.success ? t('成功') : t('失败')}`);
      if (!local.details.has(`result-${kind}`)) details.open = true;
      if (result.openOcdChecked !== undefined) text(details, t('OpenOCD 脚本解析'), result.openOcdChecked ? t('已检查') : t('未检查'));
      if (kind === 'flash') text(details, t('写入设备'), result.flashed ? t('已完成') : t('未执行'));
      list(details, result.firmware, t('尚无固件输出。')); problemRows(details, result.problems);
    }
  }
  function memoryReport(state) {
    const report = state.memory; if (!report) return;
    const panel = section(t('Flash / RAM 静态占用'), t('显示 ELF 与链接器区域的静态分配；不表示运行时堆、栈或剩余内存。'));
    text(panel, t('固件 ELF'), report.firmwareElfPath); text(panel, t('Flash 总占用'), bytes(report.totalFlash)); text(panel, t('RAM 总占用'), bytes(report.totalRam));
    const table = element('table', undefined, 'memory-table');
    append(table, append(element('thead'), append(element('tr'), ...['区域', '已用', '容量', '剩余', '使用率'].map(title => element('th', t(title))))));
    const body = element('tbody');
    for (const region of report.regions || []) {
      const row = element('tr'); const capacity = region.length;
      const percent = capacity !== null && capacity !== undefined && Number.isFinite(region.percent) ? region.percent : null;
      const usage = element('td', percent === null ? t('未知') : `${Number(percent.toFixed(1))}%`);
      if (percent !== null) { const bar = element('progress'); bar.max = 100; bar.value = Math.min(100, Math.max(0, percent)); bar.setAttribute('aria-label', t('{0} 使用率', region.name)); usage.append(bar); }
      append(row, element('td', region.name), element('td', bytes(region.used)), element('td', bytes(capacity)), element('td', bytes(capacity == null ? null : region.remaining)), usage); body.append(row);
    }
    append(panel, append(table, body)); list(panel, report.notes, '');
    if (report.sections?.length) {
      const details = detailPanel(panel, 'memory-sections', t('查看段分布'));
      const sections = element('table'); const rows = element('tbody');
      append(sections, append(element('thead'), append(element('tr'), element('th', t('段')), element('th', t('地址')), element('th', t('大小')))));
      for (const item of report.sections) append(rows, append(element('tr'), element('td', item.name), element('td', typeof item.address === 'number' ? `0x${item.address.toString(16)}` : item.address), element('td', bytes(item.size))));
      details.append(append(sections, rows));
    }
  }
  function configure(state) {
    const project = state.inspection?.project || {}, options = state.options;
    if (setupWizard(state)) { memoryReport(state); return; }
    execution(state); memoryReport(state);
    const settings = section(t('工程配置'), t('生成 VS Code 编译和烧录任务；软件检查只进行配置、编译与脚本解析。'));
    const panel = detailPanel(settings, 'project-settings', t('工程配置详情'));
    text(panel, t('芯片'), project.chip); if (project.chipEvidence) text(panel, t('识别依据'), project.chipEvidence);
    presetFields(panel, state);
    configurationField(panel, state);
    field(panel, 'buildDirectory', t('构建目录'), options.buildDirectory);
    field(panel, 'targetScript', t('OpenOCD 目标脚本'), options.targetScript);
    probeFields(panel, state);
    if (options.firmwareElfPath) text(panel, t('已选择的固件'), options.firmwareElfPath);
    issues(panel, state.inspection?.issues); list(panel, project.notes, '');
    actions(settings, button(t('确认写入…'), 'writeTasks'), button(t('预览改动'), 'previewTasks', {}, true), button(t('运行软件检查'), 'verify', {}, true), button(t('重新打开配置向导'), 'startWizard', {}, true));
    const environment = section(t('构建工具'), t('检测到多个安装时请选择使用的版本；工具路径验证通过后保存到当前工作区。'));
    toolRows(environment, state);
    actions(environment, button(t('修复缺失工具…'), 'repair', {}, true));
  }
  function sources(state) {
    const panel = section(t('添加源文件'), t('扫描当前工作区内的文件夹，选择源文件及头文件，再更新所选 CMake 构建目标。已经注册的文件会自动去重。'));
    text(panel, t('源文件夹'), state.folder);
    actions(panel, button(t('选择源文件夹…'), 'pickSourceFolder'), button(t('重新扫描'), 'scanSources', {}, true));
    const targets = state.scan?.targets || state.inspection?.targets || [];
    field(panel, 'sourceTarget', t('构建目标'), state.options.sourceTarget, targets);
    if (state.scan) {
      list(panel, state.scan.notes, '');
      const registered = section(t('已注册文件'));
      if (state.scan.alreadyRegisteredFiles?.length) detailPanel(registered, 'registered-files', t('{0} 个文件', state.scan.alreadyRegisteredFiles.length)).append(element('pre', state.scan.alreadyRegisteredFiles.join('\n'), 'registered-files'));
      else list(registered, [], t('没有已注册文件。'));
      const selection = section(t('选择文件'));
      const counter = element('p', '', 'muted'); selection.append(counter);
      const search = element('input'); search.type = 'search'; search.placeholder = t('搜索文件名或路径'); search.value = local.search ?? state.options.search ?? ''; search.setAttribute('aria-label', t('搜索源文件')); search.dataset.sourceSearch = ''; selection.append(search);
      actions(selection, button(t('全选'), 'selectAllSources', { selected: true }, true), button(t('清空选择'), 'selectAllSources', { selected: false }, true));
      const rows = element('div', undefined, 'source-list'); rows.setAttribute('role', 'group'); rows.setAttribute('aria-label', t('选择文件')); selection.append(rows);
      sourceView = virtualSources(rows, search, counter, state);
      actions(selection, button(t('确认写入…'), 'writeSources'), button(t('预览改动'), 'previewSources', {}, true));
    }
  }
  function virtualSources(viewport, search, counter, state) {
    const rowHeight = 38, overscan = 6, candidates = state.scan.candidateFiles || [];
    const normalized = candidates.map(file => file.toLowerCase());
    const registered = new Set(state.scan.alreadyRegisteredFiles || []);
    let selected = new Set(state.selectedFiles || []), selectionRevision = state.selectionRevision, selectedReference = state.selectedFiles, filtered = [], first = -1, last = -1;
    const spacer = element('div', undefined, 'source-spacer'); viewport.append(spacer);
    const updateCounter = () => { counter.textContent = t('{0} 个已选择 · {1} 个可选文件', selected.size, candidates.length) + (search.value ? ` · ${t('{0} 个匹配文件', filtered.length)}` : ''); };
    const paint = (force = false) => {
      const start = Math.max(0, Math.floor(viewport.scrollTop / rowHeight) - overscan);
      const end = Math.min(filtered.length, start + Math.ceil((viewport.clientHeight || 460) / rowHeight) + overscan * 2);
      if (!force && start === first && end === last) return;
      first = start; last = end; spacer.replaceChildren();
      for (let index = start; index < end; index++) {
        const file = candidates[filtered[index]], row = element('label', undefined, 'source-row');
        row.style.top = `${index * rowHeight}px`; row.title = file;
        const check = element('input'); check.type = 'checkbox'; check.checked = selected.has(file); check.disabled = current.busy; check.dataset.file = file;
        check.addEventListener('change', () => {
          if (check.checked) selected.add(file); else selected.delete(file);
          updateCounter(); send('selectSource', { file, selected: check.checked });
        });
        append(row, check, element('span', file + (registered.has(file) ? t(' · 已注册') : ''))); spacer.append(row);
      }
    };
    const filter = (restore = false) => {
      local.search = search.value; const query = search.value.toLowerCase();
      filtered = []; for (let index = 0; index < candidates.length; index++) if (!query || normalized[index].includes(query)) filtered.push(index);
      spacer.style.height = `${filtered.length * rowHeight}px`;
      viewport.scrollTop = restore ? local.sourceScroll || 0 : 0; local.sourceScroll = viewport.scrollTop;
      updateCounter(); paint(true);
    };
    viewport.addEventListener('scroll', () => { local.sourceScroll = viewport.scrollTop; paint(); }, { passive: true });
    search.addEventListener('input', () => filter());
    search.addEventListener('change', () => send('setOption', { key: 'search', value: search.value }));
    filter(true);
    return {
      applySelection(change) {
        if (change.all) selected = change.selected ? new Set(candidates) : new Set();
        else if (change.selected) selected.add(change.file); else selected.delete(change.file);
        current.state.selectedFiles = [...selected];
        current.state.selectionRevision = change.selectionRevision;
        selectedReference = current.state.selectedFiles; selectionRevision = change.selectionRevision; updateCounter();
        for (const check of spacer.querySelectorAll('input')) if (change.all || check.dataset.file === change.file) check.checked = selected.has(check.dataset.file);
      },
      update(next) {
        if (selectionRevision !== next.selectionRevision || selectedReference !== next.selectedFiles) {
          selected = new Set(next.selectedFiles || []); selectionRevision = next.selectionRevision; selectedReference = next.selectedFiles; updateCounter();
        }
        for (const check of spacer.querySelectorAll('input')) { check.checked = selected.has(check.dataset.file); check.disabled = current.busy; }
      }
    };
  }
  function importFiles(state) {
    const panel = section(t('加入文件'), t('从本地选择文件并复制到当前工程的指定子文件夹；同名文件会使用新的名称。'));
    field(panel, 'folderName', t('工程子文件夹名称'), state.options.folderName, undefined, t('仅填写单个文件夹名称。'));
    actions(panel, button(t('选择本地文件…'), 'pickImportFiles'), button(t('预览复制计划'), 'previewImport', {}, true)); list(panel, state.sourceFiles, t('尚未选择文件。'));
    if (state.importPreview?.plan) {
      const plan = state.importPreview.plan, preview = section(t('文件复制计划'), state.importPreview.summary);
      text(preview, t('目标文件夹'), plan.folderPath); text(preview, t('目标文件夹状态'), plan.folderExisted ? t('已经存在，复制前需要确认追加') : t('将创建'));
      const table = element('table'); append(table, append(element('thead'), append(element('tr'), element('th', t('来源')), element('th', t('目标')), element('th', t('查看差异')))));
      const body = element('tbody'); for (const [index, file] of plan.files.entries()) append(body, append(element('tr'), element('td', file.sourcePath), element('td', file.destinationPath), append(element('td'), button(t('查看差异'), 'importDiff', { indices: [index] }, true)))); append(preview, append(table, body));
      actions(preview, button(t('确认复制…'), 'importFiles'));
    }
  }
  function conversion(state) {
    const panel = section(t('工程转换'), state.options.direction === 'CMakeToMdk' ? t('CMake → MDK 检查会运行 CMake 配置并生成构建缓存，以读取目标列表；执行前需要原生确认。随后可预览转换输出。') : t('检查工程、选择构建目标，再预览全部输出文件与兼容性问题。'));
    field(panel, 'direction', t('转换方向'), state.options.direction, [{ value: 'MdkToCMake', label: 'MDK → CMake' }, { value: 'CMakeToMdk', label: 'CMake → MDK' }]);
    configurationField(panel, state);
    actions(panel, button(state.options.direction === 'CMakeToMdk' ? t('配置并检查转换目标…') : t('检查转换工程'), 'inspectConversion'));
    if (state.conversion) {
      const info = state.conversion.inspection || state.conversion;
      text(panel, t('芯片'), info.chip); issues(panel, info.issues);
      field(panel, 'target', t('构建目标'), state.options.target, info.targets || state.conversion.targets || []);
      if ((info.sourceProjects || state.conversion.sourceProjects)?.length) field(panel, 'sourceProject', t('MDK 工程'), state.options.sourceProject, info.sourceProjects || state.conversion.sourceProjects);
      field(panel, 'buildDirectory', t('构建目录'), state.options.buildDirectory);
      actions(panel, button(t('确认写入…'), 'writeConversion'), button(t('预览改动'), 'previewConversion', {}, true));
    }
  }
  function tools(state) {
    const panel = section(t('工具路径'), t('工具路径保存在当前工作区设置中。离线导入和版本激活使用本地工具管理目录。'));
    const detected = state.inspection?.tools || state.toolInventory?.tools || {};
    toolRows(panel, state);
    text(panel, t('内置 OpenOCD scripts'), detected.scripts);
    append(panel, element('p', t('OpenOCD 使用随扩展附带的完整脚本目录。'), 'muted'));
    for (const version of state.inspection?.versions || []) text(panel, version.key, version.available ? version.version || version.path : version.problem || t('不可用'));
    issues(panel, state.inspection?.issues);
    actions(panel, button(t('修复缺失工具…'), 'repair'), button(t('导入离线 ZIP…'), 'importTool', {}, true), button(t('刷新版本列表'), 'tools', {}, true));
    if (state.repair?.problems?.length) list(panel, state.repair.problems);
    const inventory = section(t('工具版本')); text(inventory, t('工具管理目录'), state.toolInventory?.root || state.inspection?.toolsRoot);
    const installations = state.toolInventory?.installations || [];
    if (!installations.length) list(inventory, [], t('尚未发现本地工具版本，可手动选择路径或导入工具包。'));
    for (const [index, installation] of installations.entries()) {
      const row = element('div', undefined, 'tool-row');
      const activate = button(installation.isActive ? t('当前使用') : t('使用此版本'), 'activateTool', { index }, true);
      lock(activate, installation.isActive || installation.version === '未通过版本验证' || installation.version === 'Version validation failed');
      append(row, append(element('div'), element('strong', `${installation.key} ${t(installation.version)}`), element('p', installation.isManaged ? t('工具管理目录') : t('本机已安装'), 'muted'), element('p', installation.executable, 'muted')), activate); inventory.append(row);
    }
  }
  function history(state) {
    const panel = section(t('修改记录'), t('选择记录查看还原差异；文件之后有修改时，将要求确认覆盖。')); actions(panel, button(t('刷新修改记录'), 'history', {}, true));
    const records = state.history?.records || [];
    if (!records.length) list(panel, [], t('当前工程尚无修改记录。'));
    for (const record of records) {
      const row = element('div', undefined, 'history-row');
      const restore = lock(button(t('预览还原'), 'previewRestore', { id: record.id }, true), !!record.restoredUtc);
      const status = { Complete: '已完成', Pending: '等待完成', RecoveryNeeded: '需要恢复' }[record.status] || record.status || '';
      append(row, append(element('div'), element('strong', record.label), element('p', t('{0} · {1} 个文件 · {2}', new Date(record.createdUtc).toLocaleString(current.language === 'en' ? 'en' : 'zh-CN'), record.changes?.length || 0, record.restoredUtc ? t('已还原') : t(status)), 'muted')), restore); panel.append(row);
    }
  }
  const address = value => typeof value === 'number' && Number.isFinite(value) ? `0x${value.toString(16)}` : value ?? '—';
  const resultTable = (parent, headers, values, cells, limit = 200) => {
    if (!values?.length) { list(parent, [], t('暂无分析结果。')); return; }
    const viewport = element('div', undefined, 'table-scroll'), table = element('table', undefined, 'analysis-table'), body = element('tbody');
    append(table, append(element('thead'), append(element('tr'), ...headers.map(title => element('th', title)))));
    for (const item of values.slice(0, limit)) append(body, append(element('tr'), ...cells(item).map(value => element('td', value ?? '—'))));
    append(parent, append(viewport, append(table, body)));
    if (values.length > limit) parent.append(element('p', t('显示前 {0} 项（共 {1} 项）。', limit, values.length), 'muted'));
  };
  const analysisFirmwareField = (panel, state) => {
    const firmware = firmwareChoices(state);
    field(panel, 'firmwareElfPath', t('固件 ELF'), state.options.firmwareElfPath, firmware.length ? [{ value: '', label: t('请选择固件目标') }, ...firmware.map(value => ({ value, label: value }))] : undefined);
  };
  function development(state) {
    const options = state.options;
    const code = section(t('代码智能提示'), t('从真实 CMake 配置生成 compile_commands.json，再写入 .clangd。生成配置文件前会要求确认。'));
    presetFields(code, state); configurationField(code, state);
    field(code, 'compilationDatabaseDirectory', t('编译数据库目录'), options.compilationDatabaseDirectory || options.buildDirectory, undefined, t('填写实际包含 compile_commands.json 的构建目录。'));
    actions(code, button(t('配置并生成编译数据库…'), 'prepareCompileDatabase'), button(t('写入 .clangd…'), 'writeClangd', {}, true), button(t('查看 clangd 扩展'), 'openClangdExtension', {}, true));
    if (state.compileDatabase) { text(code, 'compile_commands.json', state.compileDatabase.compileCommands); list(code, state.compileDatabase.notes, ''); }

    const debug = section(t('Cortex-Debug 调试'), t('写入 launch.json 前会显示确认；启动调试会连接硬件，可能暂停或复位目标。'));
    field(debug, 'debugName', t('调试配置名称'), options.debugName);
    analysisFirmwareField(debug, state);
    field(debug, 'targetScript', t('OpenOCD 目标脚本'), options.targetScript);
    probeFields(debug, state);
    actions(debug, button(t('写入调试配置…'), 'writeDebug'), button(t('启动调试…'), 'startDebug', {}, true));

    const sync = section(t('CMake / MDK 同步'), t('监视工程配置变化并提示同步；预览文件差异后，再确认写入。'));
    if (state.sync) {
      sync.append(element('p', state.sync.changed ? t('检测到配置变化，请检查同步差异。') : t('配置变化检查已完成。'), state.sync.changed ? 'issue warning' : 'muted'));
      if (state.sync.status) text(sync, t('同步状态'), t(state.sync.status));
      list(sync, (state.sync.files || []).map(item => typeof item === 'string' ? item : item.path || item.name || ''), '');
      list(sync, state.sync.notes, '');
    } else sync.append(element('p', t('检查一次以记录同步基线；之后的变化会显示提醒。'), 'muted'));
    actions(sync, button(t('检查同步状态'), 'inspectSync'), button(t('预览同步差异'), 'previewSync', {}, true), button(t('写入同步结果…'), 'writeSync', {}, true));

    const probe = section(t('探针与芯片检查'), t('点击按钮将连接目标，可能暂停或复位芯片。'));
    checkbox(probe, 'preflightCheck', t('烧录前执行探针与芯片检查'), options.preflightCheck === true || options.preflightCheck === 'true');
    const result = state.probeCheck;
    for (const [key, title] of [['scriptFound', t('目标脚本可用')], ['probeFound', t('探针已发现')], ['chipConnected', t('芯片已连接')]]) {
      const value = result?.[key];
      const row = element('div', undefined, 'detail');
      append(row, element('span', title, 'muted'), element('span', value === true ? t('通过') : value === false ? t('未通过') : t('尚未确认'), value === false ? 'warning' : value === true ? 'check-passed' : 'muted')); probe.append(row);
    }
    list(probe, result?.notes, '');
    actions(probe, button(t('连接并检查硬件…'), 'probeCheck'));
  }
  function analysis(state) {
    const firmware = section(t('固件与 MAP 分析'), t('读取 ELF 符号和可选 MAP 文件，检查符号大小与段分布。'));
    analysisFirmwareField(firmware, state);
    field(firmware, 'mapFilePath', t('MAP 文件（可选）'), state.options.mapFilePath);
    actions(firmware, button(t('分析固件'), 'analyzeFirmware'), button(t('选择 MAP 文件…'), 'pickMapFile', {}, true));
    const report = state.advancedAnalysis;
    if (report) {
      const symbols = detailPanel(firmware, 'firmware-symbols', t('符号大小'));
      if (!local.details.has('firmware-symbols')) symbols.open = true;
      resultTable(symbols, [t('符号'), t('地址'), t('大小'), t('类型'), t('段')], report.symbols, item => [item.name, address(item.address), bytes(item.size), item.kind, item.section]);
      const sections = detailPanel(firmware, 'map-sections', t('MAP 段分布'));
      resultTable(sections, [t('段'), t('地址'), t('大小'), t('目标文件')], report.mapSections, item => [item.name, address(item.address), bytes(item.size), item.objectFile]);
      if (report.regions?.length) resultTable(firmware, [t('区域'), t('地址'), t('容量'), t('已占用'), t('剩余')], report.regions,
        item => [item.name, address(item.origin), item.length == null ? t('未知') : bytes(item.length), bytes(item.used), item.remaining == null ? t('未知') : bytes(item.remaining)]);
      list(firmware, report.notes, '');
    }

    const fault = section(t('HardFault 诊断'), t('粘贴异常寄存器 JSON 或文本；使用所选 ELF 解析地址，不连接硬件。'));
    field(fault, 'hardFaultRegisters', t('异常寄存器'), state.options.hardFaultRegisters, undefined, t('支持 CFSR、HFSR、PC、LR 等寄存器的名称与十六进制值。'), true);
    actions(fault, button(t('诊断 HardFault'), 'hardFault'));
    if (state.hardFault) {
      for (const finding of state.hardFault.findings || []) {
        const item = element('div', undefined, 'issue');
        append(item, element('strong', [finding.code, finding.title].filter(Boolean).join(' · ')));
        if (finding.detail) item.append(element('p', finding.detail)); fault.append(item);
      }
      resultTable(fault, [t('寄存器'), t('地址'), t('函数'), t('源码位置')], state.hardFault.locations, item => [item.register, address(item.address), item.function, item.file ? `${item.file}${item.line ? `:${item.line}` : ''}` : '—']);
      list(fault, state.hardFault.notes, '');
    }

    const rtos = section(t('RTOS 任务'), t('手动连接现有 GDB 服务读取任务；连接可能暂停目标。每次点击才刷新，不会自动轮询。'));
    field(rtos, 'gdbEndpoint', t('GDB 服务地址'), state.options.gdbEndpoint || 'localhost:3333');
    actions(rtos, button(t('连接并刷新任务…'), 'rtos'));
    if (state.rtos) {
      const states = { running: '运行', ready: '就绪', blocked: '阻塞', suspended: '挂起', deleted: '等待删除', 'suspended-or-blocked': '挂起或阻塞', stopped: '已停止', exited: '已退出' };
      resultTable(rtos, [t('任务'), t('状态'), t('优先级'), t('栈剩余高水位（StackType_t 单位）')], state.rtos.tasks, item => [item.name, t(states[item.state] || item.state), item.priority, item.stackHighWater]);
      list(rtos, state.rtos.notes, '');
      if (state.rtos.output) detailPanel(rtos, 'rtos-output', t('查看 GDB 输出')).append(element('pre', state.rtos.output, 'raw-output'));
    }

    const exported = section(t('故障报告'), t('生成脱敏报告，先在编辑器预览，再选择保存位置。'));
    actions(exported, button(t('预览并导出报告…'), 'exportReport'));
    if (state.report) {
      if (state.report.path) text(exported, t('报告位置'), state.report.path);
      if (typeof state.report.redacted === 'boolean') text(exported, t('隐私处理'), state.report.redacted ? t('已脱敏') : t('未脱敏'));
      if (state.report.preview) detailPanel(exported, 'report-preview', t('报告预览')).append(element('pre', state.report.preview, 'raw-output'));
    }
  }
  function devices(state) {
    const options = state.options, serial = state.serial || {};
    const panel = section(t('串口终端'), t('选择串口后手动连接；支持 ASCII 与 HEX 发送，日志显示最近接收的数据。'));
    const ports = serial.ports || [];
    const choices = [{ value: '', label: t('请选择串口') }, ...ports.map(port => ({ value: port.name, label: port.description ? `${port.name} · ${port.description}` : port.name }))];
    if (options.serialPort && !ports.some(port => port.name === options.serialPort)) choices.push({ value: options.serialPort, label: options.serialPort });
    field(panel, 'serialPort', t('串口'), options.serialPort, choices);
    const baud = field(panel, 'serialBaud', t('波特率'), options.serialBaud || 115200); baud.type = 'number'; baud.min = '1'; baud.max = '4000000'; baud.step = '1'; baud.required = true;
    const refresh = button(t('刷新串口'), 'listSerial', {}, true), connect = button(t('连接串口'), 'connectSerial'), disconnect = button(t('断开连接'), 'disconnectSerial', {}, true);
    actions(panel, connect, disconnect, refresh);
    const connection = element('p', undefined, 'connection-state'), counters = element('p', undefined, 'muted'), error = element('p', undefined, 'issue error');
    const log = element('pre', undefined, 'serial-log'); log.tabIndex = 0; log.setAttribute('role', 'log'); log.setAttribute('aria-label', t('串口日志')); log.setAttribute('aria-live', 'off');
    log.addEventListener('scroll', () => { local.serialScroll = log.scrollTop; local.serialAtBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 32; }, { passive: true });
    append(panel, connection, counters, error, log);
    field(panel, 'serialMode', t('发送格式'), options.serialMode || 'ASCII', ['ASCII', 'HEX']);
    field(panel, 'serialNewline', t('行尾'), options.serialNewline || 'none', [{ value: 'none', label: t('不追加') }, { value: 'LF', label: 'LF' }, { value: 'CRLF', label: 'CRLF' }]);
    const outgoing = field(panel, 'serialText', t('发送内容'), options.serialText, undefined, t('HEX 模式使用空格分隔的十六进制字节，例如 01 A5 FF。'), true); outgoing.rows = 3;
    const transmit = button(t('发送'), 'sendSerial'), save = button(t('导出串口日志…'), 'exportSerial', {}, true); actions(panel, transmit, save);
    let linesKey;
    serialView = { update(next = {}) {
      connection.textContent = next.connected ? t('已连接 · {0} · {1} baud', next.port || options.serialPort || '—', next.baud || options.serialBaud || 115200) : t('串口未连接');
      connection.className = next.connected ? 'connection-state check-passed' : 'connection-state muted';
      counters.textContent = t('接收 {0} · 发送 {1}', bytes(next.receivedBytes ?? 0), bytes(next.sentBytes ?? 0));
      error.hidden = !next.error; error.textContent = next.error || '';
      lock(connect, !!next.connected); lock(disconnect, !next.connected); lock(transmit, !next.connected);
      const lines = (next.lines || []).slice(-1000), key = JSON.stringify(lines);
      if (key !== linesKey) {
        const bottom = linesKey === undefined ? local.serialAtBottom !== false : log.scrollHeight - log.scrollTop - log.clientHeight < 32;
        const scrollTop = linesKey === undefined ? local.serialScroll || 0 : log.scrollTop;
        linesKey = key;
        log.textContent = lines.length ? lines.map(line => {
          const stamp = line.time ? new Date(line.time) : null;
          const time = stamp && Number.isFinite(stamp.getTime()) ? stamp.toLocaleTimeString(current.language === 'en' ? 'en' : 'zh-CN', { hour12: false }) : line.time || '—';
          const direction = /^(tx|send|sent|out)$/i.test(line.direction) ? t('发送') : /^(rx|receive|received|in)$/i.test(line.direction) ? t('接收') : line.direction || '';
          return `[${time}] ${direction} ${line.text ?? ''}`;
        }).join('\n') : t('等待串口数据。');
        if (bottom) log.scrollTop = log.scrollHeight; else log.scrollTop = scrollTop;
      }
    } };
    serialView.update(serial);

    const telemetry = state.telemetry || {};
    const privacy = section(t('匿名使用统计'), t('PostHog 默认关闭。仅在主动启用后发送允许的功能事件，不包含工程路径、源码、串口内容或输出日志。'));
    text(privacy, t('配置状态'), telemetry.configured ? t('已配置') : t('尚未配置'));
    const enabled = checkbox(privacy, 'telemetryEnabled', t('启用匿名使用统计'), telemetry.enabled, 'toggleTelemetry');
    lock(enabled, !telemetry.enabled && (!telemetry.configured || telemetry.allowed === false));
    if (telemetry.allowed === false) privacy.append(element('p', t('当前 VS Code 遥测设置不允许发送统计。'), 'muted'));
    field(privacy, 'telemetryHost', t('PostHog 服务地址'), options.telemetryHost || telemetry.host);
    const token = field(privacy, 'telemetryProjectKey', t('PostHog 项目令牌'), undefined, undefined, t('令牌保存到 VS Code 安全存储；已保存的令牌不会显示。')); token.type = 'password'; token.autocomplete = 'off';
    actions(privacy, button(t('保存统计配置…'), 'configureTelemetry', {}, true));
  }
  const renderers = { configure, sources, import: importFiles, conversion, tools, history, development, analysis, devices };
  function updateProgress(message) {
    const status = document.getElementById('status');
    const value = message.unavailable || message.status || current?.state?.status || '';
    status.textContent = value; status.className = message.unavailable ? 'error' : message.busy ? 'running' : '';
    document.getElementById('refresh').disabled = message.busy || !!current?.unavailable;
    document.getElementById('cancel').hidden = !message.busy;
    document.getElementById('language').disabled = !!message.busy;
    let bar = document.getElementById('operation-progress');
    if (!bar) { bar = element('progress'); bar.id = 'operation-progress'; bar.max = 100; document.querySelector('footer').insertBefore(bar, document.getElementById('cancel')); }
    bar.hidden = !message.busy; bar.setAttribute('aria-label', t('操作进度'));
    const percent = typeof message.percent === 'number' && Number.isFinite(message.percent) ? Math.min(100, Math.max(0, message.percent)) : null;
    if (percent === null) bar.removeAttribute('value'); else bar.value = percent;
    bar.title = percent === null ? value : `${value} · ${Math.round(percent)}%`;
    for (const node of document.querySelectorAll('[data-busy-control]')) node.disabled = !!message.busy || !!current?.unavailable || node.dataset.locked === 'true';
    for (const node of document.getElementById('navigation').children) node.disabled = !!message.busy;
    if (current?.state && sourceView) sourceView.update(current.state);
  }
  function contentKey(message) {
    const { selectedFiles, preview: ignoredPreview, serial: ignoredSerial, status, percent, selectionRevision, sourceRevision, ...state } = message.state || {};
    state.options = { ...state.options }; delete state.options.search;
    if (message.page === 'devices') state.serialPorts = ignoredSerial?.ports;
    if (message.page === 'sources' && sourceRevision !== undefined) state.scan = state.scan ? { sourceRevision } : undefined;
    return JSON.stringify([message.root, message.page, message.language, message.unavailable, message.configuredTools, state]);
  }
  function renderState(message) {
    const focused = document.activeElement;
    const focusedKey = focused?.dataset.field;
    const focusedSearch = focused?.dataset.sourceSearch !== undefined;
    const selectionStart = focused?.selectionStart, selectionEnd = focused?.selectionEnd;
    if (focusedKey && focused.value !== String(current?.state?.options?.[focusedKey] ?? '')) local?.drafts.set(focusedKey, focused.value);
    current = message;
    const viewKey = `${message.root || ''}\n${message.page}`;
    if (!views.has(viewKey)) views.set(viewKey, { drafts: new Map(), details: new Map(), sourceScroll: 0, search: undefined });
    local = views.get(viewKey);
    for (const [key, value] of local.drafts) if (String(message.state?.options?.[key] ?? '') === value) local.drafts.delete(key);
    t = window.STM32I18n.createTranslator(window.STM32UiMessages, message.language);
    document.documentElement.lang = message.language;
    document.title = t('STM32 工程助手');
    for (const [id, source] of [['app-title', 'STM32 工程助手'], ['language-label', '语言'], ['footer-note', '当前工作区 · 写入前确认 · 软件检查'], ['refresh', '重新检测'], ['logs', '查看输出'], ['cancel', '取消当前操作']]) {
      const node = document.getElementById(id); if (node) node.textContent = t(source);
    }
    const language = document.getElementById('language');
    language.value = message.languagePreference || 'auto';
    for (const option of language.options) if (option.value === 'auto') option.textContent = t('跟随 VS Code');
    language.setAttribute('aria-label', t('语言'));
    document.getElementById('workspace').textContent = message.root || t('尚未打开工作区');
    const navigation = document.getElementById('navigation'); navigation.setAttribute('aria-label', t('工程操作'));
    const nextNavigation = JSON.stringify([message.pages, message.page]);
    if (nextNavigation !== navigationKey) {
      navigationKey = nextNavigation; navigation.replaceChildren();
      for (const [id, title] of message.pages || []) {
        const item = element('button', title, id === message.page ? 'active' : '');
        item.setAttribute('aria-current', id === message.page ? 'page' : 'false'); item.addEventListener('click', () => send('navigate', { page: id })); navigation.append(item);
      }
    }
    const nextKey = contentKey(message);
    if (nextKey !== renderedKey) {
      renderedKey = nextKey; sourceView = undefined; serialView = undefined; previewKey = undefined; content.replaceChildren();
      for (const notice of [message.state?.savedFilesNotice, message.state?.staleToolsNotice, message.state?.syncNotice]) if (notice) content.append(element('p', notice, 'issue warning'));
      if (message.unavailable) {
        const panel = section(t('打开当前工程'), message.unavailable), pick = element('button', t('选择工作区…'));
        pick.addEventListener('click', () => send('workspace')); panel.append(pick);
      } else renderers[message.page]?.(message.state);
      previewHost = element('div', undefined, 'preview-content'); content.append(previewHost);
      const target = focusedKey ? [...content.querySelectorAll('[data-field]')].find(node => node.dataset.field === focusedKey) : focusedSearch ? content.querySelector('[data-source-search]') : null;
      if (target) { target.focus({ preventScroll: true }); if (typeof selectionStart === 'number' && (['text', 'search', 'password'].includes(target.type) || target.tagName === 'TEXTAREA')) target.setSelectionRange(selectionStart, selectionEnd); }
    }
    if (previewHost) {
      const nextPreview = JSON.stringify([message.language, message.state?.preview]);
      if (nextPreview !== previewKey) { previewKey = nextPreview; previewHost.replaceChildren(); if (!message.unavailable) preview(message.state); }
    }
    if (serialView) serialView.update(message.state?.serial);
    updateProgress({ ...message, status: message.state?.status, percent: message.percent ?? message.state?.percent });
  }
  window.addEventListener('message', event => {
    const message = event.data;
    if (message?.type === 'state') renderState(message);
    else if (message?.type === 'progress' && current) { current.busy = message.busy; if (message.status !== undefined) current.state.status = message.status; if (message.percent !== undefined) current.state.percent = message.percent; updateProgress(message); }
    else if (message?.type === 'sourceSelection' && current?.page === 'sources') {
      sourceView?.applySelection(message); current.state.preview = undefined;
      if (previewHost) { previewHost.replaceChildren(); previewKey = JSON.stringify([current.language, undefined]); }
    }
    else if (message?.type === 'serialData' && current?.state) {
      const incoming = message.serial || {}, previous = current.state.serial || {};
      const lines = incoming.delta ? [...new Map([...(previous.lines || []), ...(incoming.lines || [])].map(line => [line.id, line])).values()].slice(-1000) : incoming.lines;
      current.state.serial = { ...previous, ...incoming, lines: lines || [] };
      serialView?.update(current.state.serial);
    }
    else if (message?.type === 'clearSensitiveDrafts') {
      for (const view of views.values()) view.drafts.delete('telemetryProjectKey');
      for (const input of content.querySelectorAll('[data-field]')) if (input.dataset.field === 'telemetryProjectKey') input.value = '';
    }
  });
  let language = document.getElementById('language');
  if (!language) {
    const wrapper = element('label', undefined, 'language-selector');
    const label = element('span', '语言'); label.id = 'language-label';
    language = element('select'); language.id = 'language';
    for (const [value, title] of [['auto', '跟随 VS Code'], ['zh-CN', '简体中文'], ['en', 'English']]) {
      const option = element('option', title); option.value = value; language.append(option);
    }
    append(wrapper, label, language); document.querySelector('header').append(wrapper);
  }
  language.addEventListener('change', () => send('setLanguage', { value: language.value }));
  document.getElementById('refresh').addEventListener('click', () => send('refresh'));
  document.getElementById('logs').addEventListener('click', () => send('logs'));
  document.getElementById('cancel').addEventListener('click', () => send('cancel'));
  send('ready');
})();
