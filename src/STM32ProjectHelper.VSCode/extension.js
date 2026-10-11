'use strict';
const vscode = require('vscode');
const path = require('path');
const crypto = require('crypto');
const fs = require('fs');
const { HostBridge } = require('./bridge');

const PAGES = [
  ['configure', '工程配置', 'settings-gear'], ['sources', '添加源文件', 'new-file'],
  ['import', '加入文件', 'files'], ['conversion', '工程转换', 'arrow-swap'],
  ['tools', '工具管理', 'tools'], ['history', '修改记录', 'history']
];
const OPTIONS = new Set(['configurePreset', 'buildPreset', 'configuration', 'targetScript', 'buildDirectory', 'firmwareElfPath', 'direction', 'target', 'sourceTarget', 'sourceProject', 'folderName', 'search']);
const TOOL_KEYS = { cMake: 'cmake', ninja: 'ninja', compiler: 'compiler', openOcd: 'openOcd' };
const TOOL_NAMES = { cMake: ['CMake', 'CMake', 'cmake.exe'], ninja: ['Ninja', 'Ninja', 'ninja.exe'], compiler: ['Compiler', 'ARM GCC', 'arm-none-eabi-gcc.exe'], openOcd: ['OpenOcd', 'OpenOCD', 'openocd.exe'] };
const REVIEWED_WRITES = new Set(['applyChanges', 'restore', 'importFiles']);
const REVIEWED_ACTIONS = new Set(['apply', 'restore', 'importFiles']);

class Navigation {
  constructor(controller) { this.controller = controller; this.emitter = new vscode.EventEmitter(); this.onDidChangeTreeData = this.emitter.event; }
  getTreeItem(item) { return item; }
  getChildren() {
    const root = new vscode.TreeItem(this.controller.folder?.name || '请选择本地工作区');
    root.description = this.controller.folder?.uri.fsPath;
    root.iconPath = new vscode.ThemeIcon('root-folder');
    root.command = { command: 'stm32ProjectHelper.selectWorkspace', title: '选择工作区' };
    return [root, ...PAGES.map(([id, label, icon]) => {
      const item = new vscode.TreeItem(label); item.iconPath = new vscode.ThemeIcon(icon);
      item.command = { command: 'stm32ProjectHelper.open', title: label, arguments: [id] }; return item;
    })];
  }
  refresh() { this.emitter.fire(); }
  dispose() { this.emitter.dispose(); }
}

class Workbench {
  constructor(context) {
    this.context = context; this.states = new Map(); this.epoch = 0; this.page = 'configure'; this.busy = false;
    this.output = vscode.window.createOutputChannel('STM32 工程助手');
    this.diagnostics = vscode.languages.createDiagnosticCollection('stm32ProjectHelper');
    this.bridge = new HostBridge(path.join(context.extensionPath, 'server', 'STM32ProjectHelper.ExtensionHost.exe'), this.output);
    this.navigation = new Navigation(this); this.previewDocuments = new Map();
    context.subscriptions.push(this.output, this.diagnostics, this.navigation, this.bridge);
    context.subscriptions.push(vscode.workspace.registerTextDocumentContentProvider('stm32-preview', { provideTextDocumentContent: uri => this.previewDocuments.get(uri.toString()) || '' }));
    context.subscriptions.push(vscode.window.registerTreeDataProvider('stm32ProjectHelper.navigation', this.navigation));
    context.subscriptions.push(vscode.commands.registerCommand('stm32ProjectHelper.open', page => this.open(page)),
      vscode.commands.registerCommand('stm32ProjectHelper.selectWorkspace', () => this.pickWorkspace()),
      vscode.commands.registerCommand('stm32ProjectHelper.refresh', () => this.operation('refresh')));
    context.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(editor => {
      if (!editor || this.busy) return;
      const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
      if (folder && folder.uri.toString() !== this.folder?.uri.toString()) { this.setFolder(folder); if (this.panel) void this.operation('refresh'); }
    }));
    context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(() => {
      if (this.folder && !vscode.workspace.workspaceFolders?.some(folder => folder.uri.toString() === this.folder.uri.toString())) this.setFolder(vscode.workspace.workspaceFolders?.[0]);
    }), vscode.workspace.onDidGrantWorkspaceTrust(() => { if (this.panel) void this.operation('refresh'); }));
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(event => {
      for (const [uri, state] of this.states) {
        if (!uri || !event.affectsConfiguration('stm32ProjectHelper.tools', vscode.Uri.parse(uri))) continue;
        const fingerprint = JSON.stringify(this.tools(vscode.Uri.parse(uri)));
        if (fingerprint === state.toolFingerprint) continue;
        state.toolFingerprint = fingerprint; this.invalidateToolState(state);
      }
      this.render();
    }));
    const active = vscode.window.activeTextEditor;
    this.setFolder((active && vscode.workspace.getWorkspaceFolder(active.document.uri)) || vscode.workspace.workspaceFolders?.[0]);
  }
  get state() {
    const key = this.folder?.uri.toString() || '';
    if (!this.states.has(key)) this.states.set(key, { options: { configuration: 'Debug', direction: 'MdkToCMake', folderName: 'User' }, selectedFiles: [], sourceFiles: [], toolFingerprint: JSON.stringify(this.tools()), status: '请选择操作。' });
    return this.states.get(key);
  }
  setFolder(folder) { this.cancelSource?.cancel(); this.folder = folder; this.epoch++; this.diagnostics.clear(); this.navigation.refresh(); this.render(); }
  supported() {
    if (!vscode.workspace.isTrusted) throw new Error('请先信任当前工作区。');
    if (process.platform !== 'win32' || process.arch !== 'x64' || vscode.env.remoteName) throw new Error('此扩展仅支持本地 Windows x64 VS Code。');
    if (!this.folder || this.folder.uri.scheme !== 'file') throw new Error('请打开本地工程文件夹，或从工作区中选择工程。');
  }
  assertSession(epoch) {
    this.supported();
    if (epoch !== this.epoch) throw new Error('当前工程已切换，请在新的工作区重新执行操作。');
    if (this.cancelSource?.token.isCancellationRequested) throw new Error('操作已取消。');
  }
  dirtyWorkspaceDocuments(includeImportSources = false) {
    const root = this.folder?.uri.fsPath; if (!root) return [];
    const selectedSources = new Set(includeImportSources ? this.state.sourceFiles.map(file => path.resolve(file).toLowerCase()) : []);
    return vscode.workspace.textDocuments.filter(document => {
      if (!document.isDirty || document.uri.scheme !== 'file') return false;
      const relative = path.relative(root, document.uri.fsPath);
      return (relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative)) || selectedSources.has(path.resolve(document.uri.fsPath).toLowerCase());
    });
  }
  async saveDirtyDocuments(epoch, includeImportSources = false) {
    let saved = false;
    while (true) {
      this.assertSession(epoch);
      const documents = this.dirtyWorkspaceDocuments(includeImportSources); if (!documents.length) return saved;
      const scope = includeImportSources ? '当前工程或选中的导入源文件' : '当前工程';
      const choice = await vscode.window.showWarningMessage(scope + '有 ' + documents.length + ' 个未保存文件。保存后继续？\n' + documents.map(document => document.uri.fsPath).join('\n'), { modal: true }, '保存后继续');
      this.assertSession(epoch);
      if (choice !== '保存后继续') { const error = new Error('已取消操作，未保存的编辑内容已保留。'); error.cancelled = true; throw error; }
      for (const document of documents) {
        this.assertSession(epoch);
        if (!document.isDirty) continue;
        const workspaceState = this.state;
        workspaceState.preview = undefined; workspaceState.importPreview = undefined; workspaceState.conversion = undefined; workspaceState.verification = undefined;
        const success = await document.save(); this.assertSession(epoch);
        if (!success || document.isDirty) throw new Error('文件未能保存，操作已停止：' + document.uri.fsPath);
        saved = true;
        this.state.status = '编辑内容已保存。文件变更和复制计划需要重新预览。'; this.render();
      }
    }
  }
  async pickWorkspace() {
    const folders = vscode.workspace.workspaceFolders || [];
    if (!folders.length) { await vscode.window.showInformationMessage('请通过“文件 → 打开文件夹”打开本地工程。'); return; }
    const item = await vscode.window.showQuickPick(folders.map(folder => ({ label: folder.name, description: folder.uri.fsPath, folder })), { title: '选择当前工程工作区' });
    if (item) { this.setFolder(item.folder); await this.open(this.page); }
  }
  async open(page = 'configure') {
    if (PAGES.some(item => item[0] === page)) this.page = page;
    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel('stm32ProjectHelper.workbench', 'STM32 工程助手', vscode.ViewColumn.Active,
        { enableScripts: true, retainContextWhenHidden: true, localResourceRoots: [vscode.Uri.joinPath(this.context.extensionUri, 'media')] });
      this.panel.iconPath = vscode.Uri.joinPath(this.context.extensionUri, 'media', 'chip.svg');
      this.panel.webview.html = this.html(this.panel.webview);
      this.panel.onDidDispose(() => { this.panel = undefined; }, null, this.context.subscriptions);
      this.panel.webview.onDidReceiveMessage(message => {
        if (!message || typeof message !== 'object') return;
        if (message.action === 'ready') { this.render(); return; }
        if (message.action === 'navigate' && PAGES.some(item => item[0] === message.page)) { void this.open(message.page); return; }
        if (message.action === 'cancel') { this.cancelSource?.cancel(); return; }
        if (message.action === 'setOption' && !this.busy && OPTIONS.has(message.key) && typeof message.value === 'string' && message.value.length <= 4096) {
          this.state.options[message.key] = message.value;
          if (message.key === 'configurePreset') {
            const preset = this.state.inspection?.project?.configurePresets?.find(p => p.name === message.value);
            this.state.options.buildPreset = preset?.buildPreset || ''; this.state.options.buildDirectory = preset?.binaryDirectory || ''; this.state.options.firmwareElfPath = undefined;
          }
          if (['configurePreset', 'buildPreset', 'configuration'].includes(message.key)) this.syncConfiguration();
          if (message.key === 'configuration' || message.key === 'buildDirectory') this.state.options.firmwareElfPath = undefined;
          if (['configurePreset', 'buildPreset', 'configuration', 'buildDirectory', 'targetScript'].includes(message.key)) this.state.verification = undefined;
          if (['configurePreset', 'buildPreset', 'configuration', 'buildDirectory'].includes(message.key)) this.state.conversion = undefined;
          if (message.key === 'direction') this.state.conversion = undefined;
          if (message.key === 'sourceTarget') { this.state.scan = undefined; this.state.selectedFiles = []; }
          if (message.key === 'folderName') this.state.importPreview = undefined;
          this.state.preview = undefined; this.render(); return;
        }
        if (message.action === 'selectSource' && !this.busy && typeof message.file === 'string' && this.state.scan?.candidateFiles?.includes(message.file)) {
          const selected = new Set(this.state.selectedFiles); if (message.selected === true) selected.add(message.file); else selected.delete(message.file);
          this.state.selectedFiles = [...selected]; this.state.preview = undefined; this.render(); return;
        }
        void this.operation(message.action, message);
      }, null, this.context.subscriptions);
    } else this.panel.reveal();
    this.render();
    if (!this.state.inspection) await this.operation('refresh');
    else if (page === 'history') await this.operation('history');
    else if (page === 'tools') await this.operation('tools');
  }
  html(webview) {
    const nonce = crypto.randomBytes(24).toString('base64');
    const resource = file => webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', file));
    return `<!DOCTYPE html><html lang="zh-CN"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';"><title>STM32 工程助手</title><link rel="stylesheet" href="${resource('workbench.css')}"></head><body><header><img src="${resource('chip.svg')}" alt=""><div><h1>STM32 工程助手</h1><p id="workspace"></p></div><button id="refresh">重新检测</button></header><nav id="navigation" aria-label="工程操作"></nav><div id="status" role="status" aria-live="polite"></div><main id="content"></main><footer><span>当前工作区 · 写入前确认 · 软件检查</span><button id="cancel" hidden>取消当前操作</button><button id="logs">查看输出</button></footer><script nonce="${nonce}" src="${resource('workbench.js')}"></script></body></html>`;
  }
  tools(uri = this.folder?.uri) {
    const config = vscode.workspace.getConfiguration('stm32ProjectHelper', uri);
    return Object.fromEntries(Object.entries(TOOL_KEYS).map(([setting, wire]) => [wire, config.get('tools.' + setting) || null]));
  }
  invalidateToolState(state = this.state) {
    state.preview = undefined; state.verification = undefined; state.conversion = undefined; state.inspection = undefined; state.toolInventory = undefined; state.repair = undefined;
    state.status = '工具路径已更改，请重新检测工程并生成预览。';
  }
  assertPreview(preview) {
    if (this.state.preview !== preview || preview.toolFingerprint !== JSON.stringify(this.tools())) throw new Error('工程状态或工具路径发生变化。请重新点击“确认写入”计算当前改动，或重新预览。');
  }
  fixedConfiguration() {
    const options = this.state.options;
    const preset = this.state.inspection?.project?.configurePresets?.find(item => item.name === options.configurePreset);
    const buildPreset = options.buildPreset || preset?.buildPreset;
    return preset?.buildPresets?.find(item => item.name === buildPreset)?.configuration || preset?.configureConfiguration || undefined;
  }
  syncConfiguration() {
    const fixed = this.fixedConfiguration(); this.state.fixedConfiguration = fixed;
    if (fixed && this.state.options.configuration !== fixed) {
      this.state.options.configuration = fixed; this.state.options.firmwareElfPath = undefined; this.state.verification = undefined; this.state.conversion = undefined;
    }
  }
  options() {
    this.syncConfiguration();
    const { options } = this.state;
    if (options.configuration !== 'Debug' && options.configuration !== 'Release') throw new Error('当前预设固定为 ' + options.configuration + '；工程助手支持 Debug 和 Release，请选择对应的预设。');
    return { configuration: options.configuration, configurePreset: options.configurePreset,
      buildPreset: options.buildPreset || undefined, targetScript: options.targetScript || undefined, buildDirectory: options.buildDirectory || undefined,
      firmwareElfPath: options.firmwareElfPath || undefined, tools: this.tools() };
  }
  async saveTools(tools, epoch) {
    if (!tools) return; this.assertSession(epoch);
    const config = vscode.workspace.getConfiguration('stm32ProjectHelper', this.folder.uri);
    let changed = false;
    for (const [setting, wire] of Object.entries(TOOL_KEYS)) if (tools[wire] && config.get('tools.' + setting) !== tools[wire]) { this.assertSession(epoch); await config.update('tools.' + setting, tools[wire], vscode.ConfigurationTarget.WorkspaceFolder); changed = true; }
    if (changed) { this.invalidateToolState(); this.state.toolFingerprint = JSON.stringify(this.tools()); }
  }
  async chooseTool(setting, epoch, progress) {
    if (!Object.hasOwn(TOOL_NAMES, setting)) return;
    const [key, title, expectedFile] = TOOL_NAMES[setting];
    const state = this.state;
    const inventory = await this.call('tools', { tools: this.tools() }, epoch, progress);
    state.toolInventory = inventory;
    const candidates = inventory.installations.filter(item => item.key === key).sort((left, right) => Number(right.isActive) - Number(left.isActive));
    const items = candidates.map(item => ({ label: item.version || title,
      description: item.isActive ? '当前使用' : item.isManaged ? '工具管理目录' : '本机已安装',
      detail: item.executable, executable: item.executable }));
    items.push({ label: '$(folder-opened) 手动选择其他路径…', detail: '选择 ' + expectedFile, browse: true });
    state.status = '请选择 ' + title + ' 版本。'; this.render();
    const selected = await vscode.window.showQuickPick(items, { title: '选择 ' + title + ' 版本',
      placeHolder: '根据版本号和完整路径选择；验证通过后保存到当前工作区', matchOnDetail: true }, this.cancelSource?.token);
    this.assertSession(epoch); if (!selected || this.cancelSource?.token.isCancellationRequested) return;
    let executable = selected.executable;
    if (selected.browse) {
      const files = await vscode.window.showOpenDialog({ canSelectFiles: true, canSelectFolders: false, canSelectMany: false,
        title: '选择 ' + expectedFile, filters: { '可执行文件': ['exe'] } });
      this.assertSession(epoch); if (!files?.length || this.cancelSource?.token.isCancellationRequested) return;
      executable = files[0].fsPath;
    }
    const result = await this.call('selectTool', { key, executable }, epoch, progress);
    this.assertSession(epoch); if (this.cancelSource?.token.isCancellationRequested) return;
    await this.saveTools(result.tools, epoch);
    await this.inspect(epoch, progress);
    state.toolInventory = await this.call('tools', { tools: this.tools() }, epoch, progress);
    state.status = title + ' 路径已验证并保存。\n' + executable + '\n请重新预览任务配置后进行编译检查。';
    return result;
  }
  render() {
    if (!this.panel) return;
    let unavailable; try { this.supported(); } catch (error) { unavailable = error.message; }
    void this.panel.webview.postMessage({ type: 'state', page: this.page, pages: PAGES, root: this.folder?.uri.fsPath, busy: this.busy, unavailable, state: this.state, configuredTools: this.tools() });
  }
  async call(command, payload, epoch, progress, reviewedPreview) {
    this.assertSession(epoch);
    const saved = await this.saveDirtyDocuments(epoch, command === 'previewImport' || command === 'importFiles');
    if (saved && REVIEWED_WRITES.has(command)) throw new Error('文件在确认期间被编辑并保存。请重新生成当前改动后再确认写入。');
    this.assertSession(epoch);
    if (reviewedPreview) this.assertPreview(reviewedPreview);
    const fingerprint = JSON.stringify(this.tools());
    const preferredTools = payload?.tools || payload?.commonOptions?.tools;
    if (preferredTools && JSON.stringify(preferredTools) !== fingerprint) throw new Error('准备操作时工具路径发生变化，请重新检测后再执行。');
    const result = await this.bridge.request(command, this.folder.uri.fsPath, payload, this.cancelSource?.token, info => {
      if (epoch !== this.epoch) return;
      this.state.status = [info.stage, info.message].filter(Boolean).join('：'); this.render(); progress?.report({ message: this.state.status });
    });
    this.assertSession(epoch); if (this.cancelSource?.token.isCancellationRequested) throw new Error('操作已取消。');
    if (!REVIEWED_WRITES.has(command) && fingerprint !== JSON.stringify(this.tools())) throw new Error('操作期间工具路径发生变化，请重新检测后再执行。');
    return result;
  }
  async inspect(epoch, progress) {
    const result = await this.call('inspect', this.options(), epoch, progress); this.state.inspection = result;
    const project = result.project || {};
    if (!this.state.options.sourceTarget && result.targets?.length) this.state.options.sourceTarget = result.targets[0];
    for (const key of ['configurePreset', 'buildPreset', 'buildDirectory', 'targetScript']) if (this.state.options[key] === undefined && project[key]) this.state.options[key] = project[key];
    this.syncConfiguration();
    this.state.status = '工程检测完成。'; return result;
  }
  preview(result, kind) {
    this.state.preview = { ...result, kind, toolFingerprint: JSON.stringify(this.tools()) }; this.state.status = result.summary || '改动已生成。可确认写入，或按需查看文件差异。'; return result;
  }
  async showDiffs(preview, epoch) {
    for (const [index, change] of (preview.changes || []).entries()) {
      this.assertSession(epoch);
      const uid = crypto.randomUUID();
      const before = vscode.Uri.parse('stm32-preview:/' + uid + '/before/' + encodeURIComponent(path.basename(change.path)));
      const after = vscode.Uri.parse('stm32-preview:/' + uid + '/after/' + encodeURIComponent(path.basename(change.path)));
      this.previewDocuments.set(before.toString(), change.before === null ? '（文件不存在）\n' : Buffer.from(change.before || '', 'base64').toString('utf8'));
      this.previewDocuments.set(after.toString(), change.after === null ? '（删除文件）\n' : Buffer.from(change.after || '', 'base64').toString('utf8'));
      await vscode.commands.executeCommand('vscode.diff', before, after, `${index + 1}/${preview.changes.length} · ${change.path}`, { preview: false });
    }
  }
  async applyPreview(epoch, progress) {
    const preview = this.state.preview;
    if (preview?.kind === 'restore') return this.execute('restore', {}, epoch, progress);
    if (!preview || !preview.changes?.length || preview.canApply === false || preview.issues?.some(issue => issue.blocking)) throw new Error('当前改动无法写入。请解决提示的问题，再点击页面上的“确认写入”重新计算。');
    this.assertPreview(preview);
    const files = preview.changes.slice(0, 30).map(change => `${change.before === null ? '新增' : change.after === null ? '删除' : '修改'} · ${path.relative(this.folder.uri.fsPath, change.path)}`);
    if (preview.changes.length > files.length) files.push(`另有 ${preview.changes.length - files.length} 个文件，可在改动列表中查看。`);
    this.state.status = `等待确认写入 ${preview.changes.length} 个文件。`; this.render();
    const choice = await vscode.window.showWarningMessage(`确认写入 ${preview.changes.length} 个文件？`,
      { modal: true, detail: ['工程：' + this.folder.uri.fsPath, preview.summary || preview.label || '', '', ...files].join('\n') }, '确认写入');
    if (choice !== '确认写入') { if (epoch === this.epoch) this.state.status = '已取消写入，工程文件保持原样。'; return false; }
    this.assertSession(epoch);
    this.assertPreview(preview);
    const result = await this.call('applyChanges', { changes: preview.changes, label: preview.label }, epoch, progress, preview);
    this.state.preview = undefined; this.state.lastResult = result; this.state.status = '文件变更已保存。可运行软件检查。';
    await this.inspect(epoch, progress); this.state.status = '文件变更已保存。可运行软件检查。';
    return true;
  }
  async writeChanges(previewAction, epoch, progress) {
    this.state.preview = undefined;
    await this.execute(previewAction, {}, epoch, progress);
    const preview = this.state.preview;
    const blockers = preview?.issues?.filter(issue => issue.blocking) || [];
    if (!preview || preview.canApply === false || blockers.length) throw new Error('当前改动无法写入。' + blockers.map(issue => issue.title || issue.message || issue.key).join('；'));
    if (!preview.changes?.length) { this.state.status = '当前配置已是最新，没有需要写入的文件。'; return false; }
    return this.applyPreview(epoch, progress);
  }
  publishProblems(result) {
    this.diagnostics.clear(); const grouped = new Map();
    for (const problem of result.problems || []) {
      if (!problem.filePath) continue;
      const file = path.isAbsolute(problem.filePath) ? problem.filePath : path.resolve(this.folder.uri.fsPath, problem.filePath);
      const line = Math.max(0, (problem.line || 1) - 1), column = Math.max(0, (problem.column || 1) - 1);
      const severity = /warning|警告/i.test(problem.severity) ? vscode.DiagnosticSeverity.Warning : /info|信息/i.test(problem.severity) ? vscode.DiagnosticSeverity.Information : vscode.DiagnosticSeverity.Error;
      const diagnostic = new vscode.Diagnostic(new vscode.Range(line, column, line, column + 1), [problem.message, problem.suggestion].filter(Boolean).join('\n'), severity);
      diagnostic.source = 'STM32 · ' + (problem.stage || '构建'); if (!grouped.has(file)) grouped.set(file, []); grouped.get(file).push(diagnostic);
    }
    for (const [file, problems] of grouped) this.diagnostics.set(vscode.Uri.file(file), problems);
  }
  async verify(epoch, progress) {
    if (this.state.options.firmwareElfPath && !fs.existsSync(this.state.options.firmwareElfPath)) this.state.options.firmwareElfPath = undefined;
    const tasks = await this.call('previewTasks', this.options(), epoch, progress);
    if (tasks.changes?.length) {
      this.preview(tasks, 'tasks');
      if (!await this.applyPreview(epoch, progress)) { this.state.status = '已取消任务配置，尚未运行软件检查。'; return; }
    }
    const result = await this.call('verify', this.options(), epoch, progress);
    this.state.verification = result; this.publishProblems(result); if (result.output) this.output.appendLine(result.output);
    this.state.status = result.success ? '软件检查通过。请选择实际 ELF 后预览任务绑定。烧录任务由你在 VS Code 中手动运行。' : '软件检查未通过。任务文件已保留，请查看问题与输出。';
    if (!result.success) return result;
    const firmware = result.firmware || [];
    if (!firmware.length) return result;
    const selected = await vscode.window.showQuickPick(firmware.map(file => ({ label: path.basename(file), description: file, file })), { title: '选择用于任务的实际固件 ELF', placeHolder: '取消将保留现有任务绑定' });
    this.assertSession(epoch); if (!selected) return result;
    this.state.options.firmwareElfPath = selected.file;
    const preview = await this.call('previewTasks', this.options(), epoch, progress); this.preview(preview, 'firmware');
    if (preview.changes?.length) await this.applyPreview(epoch, progress);
    this.state.status = '软件检查通过。固件任务绑定可在预览中确认；烧录任务由你在 VS Code 中手动运行。'; return result;
  }
  async operation(action, message = {}) {
    const actions = new Set(['refresh', 'previewTasks', 'writeTasks', 'apply', 'diff', 'verify', 'pickSourceFolder', 'scanSources', 'selectAllSources', 'previewSources', 'writeSources', 'pickImportFiles', 'previewImport', 'importDiff', 'importFiles', 'inspectConversion', 'previewConversion', 'writeConversion', 'tools', 'pickTool', 'repair', 'importTool', 'activateTool', 'history', 'previewRestore', 'restore', 'logs', 'workspace']);
    if (!actions.has(action)) return;
    if (action === 'logs') { this.output.show(true); return; }
    if (action === 'workspace') return this.pickWorkspace();
    if (this.busy) return;
    try { this.supported(); } catch (error) { this.state.status = error.message; this.render(); await vscode.window.showErrorMessage(error.message); return; }
    const epoch = this.epoch;
    this.busy = true; this.cancelSource = new vscode.CancellationTokenSource(); this.render();
    try {
      return await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'STM32 工程助手', cancellable: true }, async (progress, token) => {
        const subscription = token.onCancellationRequested(() => this.cancelSource?.cancel());
        try {
          if (action !== 'selectAllSources' && action !== 'pickImportFiles') {
            const saved = await this.saveDirtyDocuments(epoch, ['previewImport', 'importDiff', 'importFiles'].includes(action));
            if (saved && REVIEWED_ACTIONS.has(action)) throw new Error('编辑内容已保存。请重新预览后再确认写入。');
          }
          return await this.execute(action, message, epoch, progress);
        } finally { subscription.dispose(); }
      });
    } catch (error) {
      this.output.appendLine(error.stack || error.message);
      if (epoch === this.epoch) { this.state.status = error.message; if (!error.cancelled && !this.cancelSource.token.isCancellationRequested) await vscode.window.showErrorMessage(error.message, '查看输出').then(choice => { if (choice) this.output.show(true); }); }
    } finally {
      this.cancelSource.dispose(); this.cancelSource = undefined; this.busy = false; this.render();
      if (epoch !== this.epoch && this.panel && !this.state.inspection) void this.operation('refresh');
    }
  }
  async execute(action, message, epoch, progress) {
    const state = this.state;
    switch (action) {
      case 'writeTasks': return this.writeChanges('previewTasks', epoch, progress);
      case 'writeSources': return this.writeChanges('previewSources', epoch, progress);
      case 'writeConversion': return this.writeChanges('previewConversion', epoch, progress);
      case 'refresh': state.preview = undefined; return this.inspect(epoch, progress);
      case 'previewTasks': return this.preview(await this.call('previewTasks', this.options(), epoch, progress), 'tasks');
      case 'apply': return this.applyPreview(epoch, progress);
      case 'diff': if (state.preview) return this.showDiffs(state.preview, epoch); return;
      case 'verify': return this.verify(epoch, progress);
      case 'pickSourceFolder': {
        const folders = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, canSelectMany: false, defaultUri: this.folder.uri, title: '选择当前工程中的源文件夹' });
        this.assertSession(epoch); if (!folders?.length) return;
        const relative = path.relative(this.folder.uri.fsPath, folders[0].fsPath);
        if (relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative)) throw new Error('源文件夹必须位于当前工作区内。');
        if (state.folder !== folders[0].fsPath) state.scan = undefined;
        state.folder = folders[0].fsPath; state.preview = undefined;
        return this.execute('scanSources', {}, epoch, progress);
      }
      case 'scanSources': {
        if (!state.folder) throw new Error('请先选择源文件夹。');
        const scan = await this.call('scanSources', { folder: state.folder, target: state.options.sourceTarget }, epoch, progress);
        const previous = state.scan ? new Set(state.selectedFiles) : undefined;
        state.scan = scan; state.selectedFiles = scan.candidateFiles.filter(file => previous ? previous.has(file) : scan.selectedFiles?.includes(file));
        if (!state.options.sourceTarget && scan.targets?.length) state.options.sourceTarget = scan.targets[0];
        state.status = '源文件扫描完成。'; return scan;
      }
      case 'selectAllSources': state.selectedFiles = message.selected === true ? [...(state.scan?.candidateFiles || [])] : []; state.preview = undefined; return;
      case 'previewSources': return this.preview(await this.call('previewSources', { folder: state.folder, target: state.options.sourceTarget, selectedFiles: state.selectedFiles }, epoch, progress), 'sources');
      case 'pickImportFiles': {
        const files = await vscode.window.showOpenDialog({ canSelectFiles: true, canSelectFolders: false, canSelectMany: true, title: '选择要复制到工程的本地文件' });
        this.assertSession(epoch); if (files) { state.sourceFiles = files.map(file => file.fsPath); state.importPreview = undefined; } return;
      }
      case 'previewImport': {
        const result = await this.call('previewImport', { folderName: state.options.folderName, sourceFiles: state.sourceFiles }, epoch, progress); state.importPreview = result; state.status = result.summary || '请确认文件复制计划。'; return result;
      }
      case 'importDiff': {
        const plan = state.importPreview?.plan; if (!plan) throw new Error('请先预览加入文件计划。');
        for (const file of plan.files) {
          this.assertSession(epoch);
          const before = vscode.Uri.parse('stm32-preview:/' + crypto.randomUUID() + '/new/' + encodeURIComponent(path.basename(file.destinationPath)));
          this.previewDocuments.set(before.toString(), '');
          await vscode.commands.executeCommand('vscode.diff', before, vscode.Uri.file(file.sourcePath), '新增 · ' + file.destinationPath, { preview: false });
        }
        return;
      }
      case 'importFiles': {
        const preview = state.importPreview;
        if (!preview?.plan) throw new Error('请先预览加入文件计划。');
        await this.execute('importDiff', {}, epoch, progress); this.assertSession(epoch);
        const text = preview.plan.folderExisted ? '目标文件夹已存在，是否向其中追加所列文件？' : '确认创建目标文件夹并复制所列文件？';
        const choice = await vscode.window.showWarningMessage(text + '\n' + preview.plan.folderPath + '\n' + preview.plan.files.length + ' 个文件', { modal: true }, '确认复制');
        this.assertSession(epoch); if (choice !== '确认复制') return;
        const result = await this.call('importFiles', { plan: preview.plan, confirmedExistingFolder: preview.plan.folderExisted }, epoch, progress);
        state.importPreview = undefined; state.sourceFiles = []; state.status = '文件已复制。可在“添加源文件”中注册到构建目标。'; return result;
      }
      case 'inspectConversion': {
        state.preview = undefined;
        if (state.options.direction === 'CMakeToMdk') {
          const options = this.options();
          state.status = 'CMake → MDK 检查需要运行 CMake 配置，以读取构建目标。'; this.render();
          const choice = await vscode.window.showWarningMessage('确认运行 CMake 配置以读取转换目标？\n该步骤会在构建目录生成缓存和构建文件。\n工程：' + this.folder.uri.fsPath + '\n构建目录：' + (options.buildDirectory || '由 CMake 预设确定') + '\n配置：' + options.configuration, { modal: true }, '确认配置并检查');
          this.assertSession(epoch);
          if (choice !== '确认配置并检查') { state.status = '已取消 CMake 配置，尚未检查转换目标。'; return; }
          state.status = '正在执行 CMake 配置并读取转换目标。'; this.render();
        }
        const result = await this.call('inspectConversion', { direction: state.options.direction, commonOptions: this.options() }, epoch, progress);
        state.conversion = result; const info = result.inspection || result;
        state.options.target = info.targets?.[0] || result.targets?.[0] || '';
        state.options.sourceProject = info.sourceProjects?.[0] || result.sourceProjects?.[0] || '';
        if (result.buildDirectory) state.options.buildDirectory = result.buildDirectory;
        state.status = state.options.direction === 'CMakeToMdk' && result.buildDirectory ? 'CMake 配置已完成，转换目标已读取。' : '转换工程检查完成。'; return result;
      }
      case 'previewConversion': {
        if (!state.conversion) throw new Error('请先检查转换工程。');
        return this.preview(await this.call('previewConversion', { direction: state.options.direction, target: state.options.target, sourceProject: state.options.sourceProject, buildDirectory: state.options.buildDirectory, configuration: state.options.configuration }, epoch, progress), 'conversion');
      }
      case 'tools': state.toolInventory = await this.call('tools', { tools: this.tools() }, epoch, progress); state.status = '本地工具列表已刷新。'; return state.toolInventory;
      case 'pickTool': {
        return this.chooseTool(message.key, epoch, progress);
      }
      case 'repair': {
        const choice = await vscode.window.showWarningMessage('确认检查并下载修复缺失的构建工具？', { modal: true }, '确认修复');
        this.assertSession(epoch); if (!choice) return;
        const result = await this.call('repair', { tools: this.tools(), targetScript: state.options.targetScript }, epoch, progress);
        if (result.selectionRequired?.length) {
          await this.inspect(epoch, progress); state.repair = result;
          state.status = '已检测到多个工具安装，请先选择版本，再按需要修复缺失工具。';
          const selected = await vscode.window.showQuickPick(result.selectionRequired.map(item => ({ label: item.title,
            key: Object.keys(TOOL_NAMES).find(setting => TOOL_NAMES[setting][0] === item.key) })),
          { title: '选择需要指定版本的工具', placeHolder: '现有工具无需重新下载' }, this.cancelSource?.token);
          this.assertSession(epoch); if (selected) await this.chooseTool(selected.key, epoch, progress);
          return result;
        }
        await this.saveTools(result.tools, epoch); state.repair = result; await this.inspect(epoch, progress);
        state.status = result.problems?.length ? result.problems.join('\n') : '工具修复完成。'; return result;
      }
      case 'importTool': {
        const key = await vscode.window.showQuickPick(['cmake', 'ninja', 'compiler', 'openocd'], { title: '选择离线工具种类' });
        this.assertSession(epoch); if (!key) return;
        const files = await vscode.window.showOpenDialog({ canSelectMany: false, filters: { 'ZIP 工具包': ['zip'] }, title: '选择本地工具 ZIP 包' });
        this.assertSession(epoch); if (!files?.length) return;
        const result = await this.call('importTool', { key, archive: files[0].fsPath, tools: this.tools() }, epoch, progress);
        state.status = '离线工具导入完成，请在版本列表中激活。'; state.toolInventory = await this.call('tools', { tools: this.tools() }, epoch, progress); return result;
      }
      case 'activateTool': {
        if (!Number.isInteger(message.index) || message.index < 0) return;
        const installation = state.toolInventory?.installations?.[message.index]; if (!installation) return;
        const result = await this.call('activateTool', { installation, tools: this.tools() }, epoch, progress);
        await this.saveTools(result.tools || result, epoch); await this.inspect(epoch, progress);
        state.toolInventory = await this.call('tools', { tools: this.tools() }, epoch, progress); state.status = '工具版本已激活。'; return result;
      }
      case 'history': state.history = await this.call('history', {}, epoch, progress); state.status = '修改记录已刷新。'; return state.history;
      case 'previewRestore': {
        if (!state.history?.records?.some(record => record.id === message.id)) return;
        const result = await this.call('previewRestore', { id: message.id }, epoch, progress);
        return this.preview({ ...result, recordId: message.id }, 'restore');
      }
      case 'restore': {
        const preview = state.preview; if (preview?.kind !== 'restore') throw new Error('请先预览要还原的记录。');
        await this.showDiffs(preview, epoch); this.assertSession(epoch);
        const conflicts = preview.conflicts || [];
        const text = conflicts.length ? '下列文件在记录之后被修改。确认覆盖这些更改并还原？\n' + conflicts.join('\n') : '确认还原此修改记录？';
        const label = conflicts.length ? '确认覆盖并还原' : '确认还原';
        const choice = await vscode.window.showWarningMessage(text, { modal: true }, label); this.assertSession(epoch); if (choice !== label) return;
        this.assertPreview(preview);
        const result = await this.call('restore', { id: preview.recordId, changes: preview.changes, overwriteChangedFiles: conflicts.length > 0 }, epoch, progress, preview);
        state.preview = undefined; await this.inspect(epoch, progress); state.history = await this.call('history', {}, epoch, progress); state.status = '修改记录已还原。'; return result;
      }
    }
  }
  snapshot() { return { root: this.folder?.uri.fsPath, page: this.page, busy: this.busy, ready: !!this.state.inspection && !this.busy, trusted: vscode.workspace.isTrusted, panelOpen: !!this.panel, navigation: PAGES.map(item => item[1]), state: JSON.parse(JSON.stringify(this.state)) }; }
}

async function activate(context) {
  const workbench = new Workbench(context);
  context.subscriptions.push({ dispose() { workbench.cancelSource?.cancel(); workbench.panel?.dispose(); } });
  if (vscode.workspace.getConfiguration('stm32ProjectHelper').get('autoOpen', true) && vscode.workspace.isTrusted && process.platform === 'win32' && process.arch === 'x64' && !vscode.env.remoteName && workbench.folder) {
    const matches = await vscode.workspace.findFiles(new vscode.RelativePattern(workbench.folder, '{CMakeLists.txt,**/*.uvprojx,*.ioc}'), '**/{.git,node_modules}/**', 1);
    if (matches.length) await workbench.open();
  }
  return { getSnapshot: () => workbench.snapshot(), openPage: page => workbench.open(page), request: (command, payload) => workbench.operation(command === 'inspect' ? 'refresh' : command, payload),
    snapshot: () => workbench.snapshot(), open: page => workbench.open(page), operation: (action, payload) => workbench.operation(action, payload) };
}
function deactivate() {}
module.exports = { activate, deactivate };
