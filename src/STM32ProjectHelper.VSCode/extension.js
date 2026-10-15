'use strict';
const vscode = require('vscode');
const path = require('path');
const crypto = require('crypto');
const fs = require('fs');
const { HostBridge } = require('./bridge');
const { resolveLanguage, createTranslator } = require('./media/i18n');
const controllerCatalog = require('./locales/controller.json');
const { discoverProjects, nearestProject, contains } = require('./project-discovery');
const { viewState } = require('./view-state');

const PAGES = [
  ['configure', '工程配置', 'settings-gear'], ['sources', '添加源文件', 'new-file'],
  ['import', '加入文件', 'files'], ['conversion', '工程转换', 'arrow-swap'],
  ['tools', '工具管理', 'tools'], ['history', '修改记录', 'history']
];
const OPTIONS = new Set(['configurePreset', 'buildPreset', 'configuration', 'targetScript', 'buildDirectory', 'firmwareElfPath', 'direction', 'target', 'sourceTarget', 'sourceProject', 'folderName', 'search', 'probeType', 'adapterSpeed', 'resetMode']);
const TOOL_KEYS = { cMake: 'cmake', ninja: 'ninja', compiler: 'compiler', openOcd: 'openOcd' };
const TOOL_NAMES = { cMake: ['CMake', 'CMake', 'cmake.exe'], ninja: ['Ninja', 'Ninja', 'ninja.exe'], compiler: ['Compiler', 'ARM GCC', 'arm-none-eabi-gcc.exe'], openOcd: ['OpenOcd', 'OpenOCD', 'openocd.exe'] };
const REVIEWED_WRITES = new Set(['applyChanges', 'restore', 'importFiles']);
const REVIEWED_ACTIONS = new Set(['apply', 'restore', 'importFiles']);
const DISK_ACTIONS = new Set(['writeTasks', 'writeSources', 'writeConversion', 'apply', 'restore', 'importFiles', 'verify', 'build', 'flash', 'wizardFinish']);
const DISK_COMMANDS = new Set(['applyChanges', 'restore', 'importFiles', 'verify', 'build', 'flash']);

class LocalizedOutput {
  constructor(name) { this.name = name; this.text = ''; this.channel = vscode.window.createOutputChannel(name); }
  append(value) { this.text = (this.text + value).slice(-8 * 1024 * 1024); this.channel.append(value); }
  appendLine(value) { this.text = (this.text + value + '\n').slice(-8 * 1024 * 1024); this.channel.appendLine(value); }
  show(preserveFocus) { this.channel.show(preserveFocus); }
  rename(name) {
    if (name === this.name) return;
    this.channel.dispose(); this.name = name; this.channel = vscode.window.createOutputChannel(name);
    if (this.text) this.channel.append(this.text);
  }
  dispose() { this.channel.dispose(); }
}

class Navigation {
  constructor(controller) { this.controller = controller; this.emitter = new vscode.EventEmitter(); this.onDidChangeTreeData = this.emitter.event; }
  getTreeItem(item) { return item; }
  getChildren() {
    const root = new vscode.TreeItem(this.controller.folder?.name || this.controller.t('请选择本地工作区'));
    root.description = this.controller.folder?.uri.fsPath;
    root.iconPath = new vscode.ThemeIcon('root-folder');
    root.command = { command: 'stm32ProjectHelper.selectWorkspace', title: this.controller.t('选择工作区') };
    return [root, ...PAGES.map(([id, label, icon]) => {
      const item = new vscode.TreeItem(this.controller.t(label)); item.iconPath = new vscode.ThemeIcon(icon);
      item.command = { command: 'stm32ProjectHelper.open', title: this.controller.t(label), arguments: [id] }; return item;
    })];
  }
  refresh() { this.emitter.fire(); }
  dispose() { this.emitter.dispose(); }
}

class Workbench {
  constructor(context) {
    this.translations = {}; this.updateLanguage();
    this.context = context; this.states = new Map(); this.epoch = 0; this.page = 'configure'; this.busy = false;
    this.output = new LocalizedOutput(this.t('STM32 工程助手'));
    this.diagnostics = vscode.languages.createDiagnosticCollection('stm32ProjectHelper');
    this.bridge = new HostBridge(path.join(context.extensionPath, 'server', 'STM32ProjectHelper.ExtensionHost.exe'), this.output, { translate: (source, ...args) => this.t(source, ...args), onTranslations: pairs => Object.assign(this.translations, pairs) });
    this.navigation = new Navigation(this); this.previewDocuments = new Map(); this.projectCandidates = new Map();
    context.subscriptions.push(this.output, this.diagnostics, this.navigation, this.bridge);
    context.subscriptions.push(vscode.workspace.registerTextDocumentContentProvider('stm32-preview', { provideTextDocumentContent: uri => this.previewDocuments.get(uri.toString())?.text || '' }),
      vscode.workspace.onDidCloseTextDocument(document => { if (document.uri.scheme === 'stm32-preview') this.releaseDiff(document.uri.toString()); }));
    context.subscriptions.push(vscode.window.registerTreeDataProvider('stm32ProjectHelper.navigation', this.navigation));
    context.subscriptions.push(vscode.commands.registerCommand('stm32ProjectHelper.open', page => this.open(page)),
      vscode.commands.registerCommand('stm32ProjectHelper.selectWorkspace', () => this.pickWorkspace()),
      vscode.commands.registerCommand('stm32ProjectHelper.refresh', () => this.operation('refresh')),
      vscode.commands.registerCommand('stm32ProjectHelper.build', () => this.operation('build')),
      vscode.commands.registerCommand('stm32ProjectHelper.flash', () => this.operation('flash')),
      vscode.commands.registerCommand('stm32ProjectHelper.setup', async () => { await this.open('configure'); return this.operation('startWizard'); }));
    context.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(async editor => {
      if (!editor || this.busy) return;
      const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
      if (!folder || this.folder && contains(this.folder.uri.fsPath, editor.document.uri.fsPath)) return;
      const activeEpoch = this.epoch;
      const projects = await this.projectsIn(folder);
      if (this.busy || activeEpoch !== this.epoch) return;
      const candidate = nearestProject(projects, editor.document.uri.fsPath);
      if (candidate) this.setFolder(this.projectFolder(folder, candidate.root));
      else if (folder.uri.toString() !== (this.folder?.workspaceFolder || this.folder)?.uri.toString()) this.setFolder(folder);
      else return;
      if (this.panel) void this.operation('refresh');
    }));
    context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(() => {
      this.projectCandidates.clear();
      if (this.folder && !vscode.workspace.workspaceFolders?.some(folder => folder.uri.toString() === (this.folder.workspaceFolder || this.folder).uri.toString())) this.setFolder(vscode.workspace.workspaceFolders?.[0]);
    }), vscode.workspace.onDidGrantWorkspaceTrust(() => { if (this.panel) void this.operation('refresh'); }));
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('stm32ProjectHelper.language')) { if (this.busy) this.pendingLanguage = true; else this.updateLanguage(); }
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
  updateLanguage() {
    const previousLanguage = this.language;
    const preference = vscode.workspace.getConfiguration('stm32ProjectHelper').get('language', 'auto');
    this.languagePreference = ['auto', 'zh-CN', 'en'].includes(preference) ? preference : 'auto';
    this.language = resolveLanguage(this.languagePreference, vscode.env.language);
    this.translator = createTranslator(controllerCatalog, this.language);
    if (previousLanguage && previousLanguage !== this.language && this.states) {
      for (const state of this.states.values()) {
        if (['tasks', 'firmware', 'conversion'].includes(state.preview?.kind)) state.preview = undefined;
      }
    }
    if (this.output) this.output.rename(this.t('STM32 工程助手'));
    if (this.panel) this.panel.title = this.t('STM32 工程助手');
    this.navigation?.refresh();
    if (this.states && this.state.verification) this.publishProblems(this.state.verification);
  }
  t(source, ...args) {
    const pairs = this.translations || {};
    const chinese = Object.keys(pairs).find(key => pairs[key] === source) || Object.keys(controllerCatalog).find(key => controllerCatalog[key] === source) || source;
    const english = pairs[chinese] || controllerCatalog[chinese] || chinese;
    const format = value => createTranslator({}, 'zh-CN')(value, ...args);
    const zh = format(chinese), en = format(english);
    if (zh !== en) pairs[zh] = en;
    return this.language === 'en' ? en : zh;
  }
  async setLanguage(value) {
    if (this.busy || !['auto', 'zh-CN', 'en'].includes(value)) return;
    await vscode.workspace.getConfiguration('stm32ProjectHelper').update('language', value, vscode.ConfigurationTarget.Global);
    this.updateLanguage(); this.render();
    return this.snapshot();
  }
  localizeDisplay(value, key = '') {
    const displayKeys = new Set(['status', 'summary', 'message', 'suggestion', 'stage', 'severity', 'description', 'detail', 'reason', 'recommendation', 'title', 'label', 'problems', 'warnings', 'issues', 'unavailable', 'action', 'instructions', 'chipEvidence', 'notes', 'errors', 'problem', 'version', 'savedFilesNotice', 'staleToolsNotice']);
    if (typeof value === 'string') return displayKeys.has(key) ? this.t(value) : value;
    if (Array.isArray(value)) return value.map(item => this.localizeDisplay(item, key));
    if (!value || typeof value !== 'object') return value;
    return Object.fromEntries(Object.entries(value).map(([name, item]) => [name, (name === 'message' && value.rawMessage) || ['options', 'changes', 'plan', 'candidateFiles', 'selectedFiles', 'sourceFiles', 'targets', 'configurePresets', 'buildPresets'].includes(name) ? item : this.localizeDisplay(item, name)]));
  }
  get state() {
    const key = this.folder?.uri.toString() || '';
    if (!this.states.has(key)) this.states.set(key, { options: { configuration: 'Debug', direction: 'MdkToCMake', folderName: 'User', probeType: 'cmsis-dap', adapterSpeed: '1000', resetMode: 'default' }, selectedFiles: [], sourceFiles: [], sourceRevision: 0, selectionRevision: 0, toolFingerprint: JSON.stringify(this.tools()), status: this.t('请选择操作。') });
    return this.states.get(key);
  }
  setFolder(folder) { this.cancelSource?.cancel(); this.folder = folder; this.epoch++; this.diagnostics.clear(); this.pruneDiffs(); this.navigation.refresh(); this.render(); }
  projectFolder(workspaceFolder, root) {
    return { uri: vscode.Uri.file(root), name: path.relative(workspaceFolder.uri.fsPath, root) || workspaceFolder.name, workspaceFolder };
  }
  async projectsIn(folder, refresh = false) {
    if (folder.uri.scheme !== 'file' || vscode.env.remoteName || process.platform !== 'win32') return [];
    const key = folder.uri.toString();
    if (refresh) this.projectCandidates.delete(key);
    if (!this.projectCandidates.has(key)) this.projectCandidates.set(key, discoverProjects(folder.uri.fsPath));
    return this.projectCandidates.get(key);
  }
  supported() {
    if (!vscode.workspace.isTrusted) throw new Error(this.t('请先信任当前工作区。'));
    if (process.platform !== 'win32' || process.arch !== 'x64' || vscode.env.remoteName) throw new Error(this.t('此扩展仅支持本地 Windows x64 VS Code。'));
    if (!this.folder || this.folder.uri.scheme !== 'file') throw new Error(this.t('请打开本地工程文件夹，或从工作区中选择工程。'));
  }
  assertSession(epoch) {
    this.supported();
    if (epoch !== this.epoch) throw new Error(this.t('当前工程已切换，请在新的工作区重新执行操作。'));
    if (this.cancelSource?.token.isCancellationRequested) throw new Error(this.t('操作已取消。'));
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
      const scope = includeImportSources ? this.t('当前工程或选中的导入源文件') : this.t('当前工程');
      const choice = await vscode.window.showWarningMessage(this.t('{0}有 {1} 个未保存文件。保存后继续？\n', scope, documents.length) + documents.map(document => document.uri.fsPath).join('\n'), { modal: true }, this.t('保存后继续'));
      this.assertSession(epoch);
      if (choice !== this.t('保存后继续')) { const error = new Error(this.t('已取消操作，未保存的编辑内容已保留。')); error.cancelled = true; throw error; }
      for (const document of documents) {
        this.assertSession(epoch);
        if (!document.isDirty) continue;
        const workspaceState = this.state;
        workspaceState.preview = undefined; workspaceState.importPreview = undefined; workspaceState.conversion = undefined; workspaceState.verification = undefined;
        const success = await document.save(); this.assertSession(epoch);
        if (!success || document.isDirty) throw new Error(this.t('文件未能保存，操作已停止：') + document.uri.fsPath);
        saved = true;
        this.state.status = this.t('编辑内容已保存。文件变更和复制计划需要重新预览。'); this.render();
      }
    }
  }
  async pickWorkspace() {
    if (process.platform !== 'win32' || process.arch !== 'x64' || vscode.env.remoteName) { await vscode.window.showErrorMessage(this.t('此扩展仅支持本地 Windows x64 VS Code。')); return; }
    const folders = vscode.workspace.workspaceFolders || [];
    if (!folders.length) { await vscode.window.showInformationMessage(this.t('请通过“文件 → 打开文件夹”打开本地工程。')); return; }
    const items = [];
    for (const folder of folders) {
      const candidates = await this.projectsIn(folder, true);
      for (const candidate of candidates) items.push({ label: path.relative(folder.uri.fsPath, candidate.root) || folder.name, description: candidate.root, detail: candidate.evidence.join(' · '), folder: this.projectFolder(folder, candidate.root) });
      if (!candidates.some(candidate => path.resolve(candidate.root) === path.resolve(folder.uri.fsPath))) items.push({ label: folder.name, description: folder.uri.fsPath, detail: this.t('手动选择工作区根目录'), folder });
    }
    const item = await vscode.window.showQuickPick(items, { title: this.t('选择当前工程工作区') });
    if (item) { this.setFolder(item.folder); await this.open(this.page); }
  }
  async open(page = 'configure') {
    if (PAGES.some(item => item[0] === page)) this.page = page;
    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel('stm32ProjectHelper.workbench', this.t('STM32 工程助手'), vscode.ViewColumn.Active,
        { enableScripts: true, retainContextWhenHidden: true, localResourceRoots: [vscode.Uri.joinPath(this.context.extensionUri, 'media')] });
      this.panel.iconPath = vscode.Uri.joinPath(this.context.extensionUri, 'media', 'chip.svg');
      this.panel.webview.html = this.html(this.panel.webview);
      this.panel.onDidDispose(() => { this.panel = undefined; this.cancelSource?.cancel(); clearTimeout(this.progressTimer); this.pruneDiffs(); }, null, this.context.subscriptions);
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
          if (['configurePreset', 'buildPreset', 'configuration', 'buildDirectory', 'targetScript', 'probeType', 'adapterSpeed', 'resetMode'].includes(message.key)) { this.state.verification = undefined; this.state.buildResult = undefined; this.state.flashResult = undefined; }
          if (['configurePreset', 'buildPreset', 'configuration', 'buildDirectory', 'firmwareElfPath'].includes(message.key)) this.state.memory = undefined;
          if (['configurePreset', 'buildPreset', 'configuration', 'buildDirectory'].includes(message.key)) this.state.conversion = undefined;
          if (message.key === 'direction') this.state.conversion = undefined;
          if (message.key === 'sourceTarget') { this.state.scan = undefined; this.state.selectedFiles = []; this.state.sourceRevision++; this.state.selectionRevision++; }
          if (message.key === 'folderName') this.state.importPreview = undefined;
          this.state.preview = undefined; this.render(); return;
        }
        if (message.action === 'selectSource' && !this.busy && typeof message.file === 'string' && this.state.scan?.candidateFiles?.includes(message.file)) {
          const selected = new Set(this.state.selectedFiles); if (message.selected === true) selected.add(message.file); else selected.delete(message.file);
          this.state.selectedFiles = [...selected]; this.state.selectionRevision++; this.state.preview = undefined;
          void this.panel?.webview.postMessage({ type: 'sourceSelection', file: message.file, selected: message.selected === true, count: selected.size, selectionRevision: this.state.selectionRevision, preview: null }); return;
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
    return `<!DOCTYPE html><html lang="${this.language}"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';"><title>${this.t('STM32 工程助手')}</title><link rel="stylesheet" href="${resource('workbench.css')}"></head><body><header><img src="${resource('chip.svg')}" alt=""><div><h1 id="app-title">${this.t('STM32 工程助手')}</h1><p id="workspace"></p></div><label class="language-selector" for="language"><span id="language-label">${this.t('语言')}</span><select id="language"><option value="auto">${this.t('跟随 VS Code')}</option><option value="zh-CN">中文</option><option value="en">English</option></select></label><button id="refresh">${this.t('重新检测')}</button></header><nav id="navigation" aria-label="${this.t('工程操作')}"></nav><div id="status" role="status" aria-live="polite"></div><main id="content"></main><footer><span id="footer-note">${this.t('当前工作区 · 写入前确认 · 软件检查')}</span><button id="cancel" hidden>${this.t('取消当前操作')}</button><button id="logs">${this.t('查看输出')}</button></footer><script nonce="${nonce}" src="${resource('i18n.js')}"></script><script nonce="${nonce}" src="${resource('ui-messages.js')}"></script><script nonce="${nonce}" src="${resource('workbench.js')}"></script></body></html>`;
  }
  tools(uri = this.folder?.uri) {
    const config = vscode.workspace.getConfiguration('stm32ProjectHelper', uri);
    return Object.fromEntries(Object.entries(TOOL_KEYS).map(([setting, wire]) => {
      const file = config.get('tools.' + setting);
      try { return [wire, file && fs.statSync(file).isFile() ? file : null]; } catch { return [wire, null]; }
    }));
  }
  invalidateToolState(state = this.state) {
    state.preview = undefined; state.verification = undefined; state.buildResult = undefined; state.flashResult = undefined; state.memory = undefined; state.conversion = undefined; state.inspection = undefined; state.toolInventory = undefined; state.repair = undefined;
    state.status = this.t('工具路径已更改，请重新检测工程并生成预览。');
  }
  assertPreview(preview) {
    if (this.state.preview !== preview || preview.toolFingerprint !== JSON.stringify(this.tools())) throw new Error(this.t('工程状态或工具路径发生变化。请重新点击“确认写入”计算当前改动，或重新预览。'));
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
    return { configuration: options.configuration, configurePreset: options.configurePreset,
      buildPreset: options.buildPreset || undefined, targetScript: options.targetScript || undefined, buildDirectory: options.buildDirectory || undefined,
      firmwareElfPath: options.firmwareElfPath || undefined, probeType: options.probeType || 'cmsis-dap', adapterSpeed: Number(options.adapterSpeed || 1000), resetMode: options.resetMode || 'default', tools: this.tools() };
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
      description: item.isActive ? this.t('当前使用') : item.isManaged ? this.t('工具管理目录') : this.t('本机已安装'),
      detail: item.executable, executable: item.executable }));
    items.push({ label: this.t('$(folder-opened) 手动选择其他路径…'), detail: this.t('选择 {0}', expectedFile), browse: true });
    state.status = this.t('请选择 {0} 版本。', title); this.render();
    const selected = await vscode.window.showQuickPick(items, { title: this.t('选择 {0} 版本', title),
      placeHolder: this.t('根据版本号和完整路径选择；验证通过后保存到当前工作区'), matchOnDetail: true }, this.cancelSource?.token);
    this.assertSession(epoch); if (!selected || this.cancelSource?.token.isCancellationRequested) return;
    let executable = selected.executable;
    if (selected.browse) {
      const files = await vscode.window.showOpenDialog({ canSelectFiles: true, canSelectFolders: false, canSelectMany: false,
        title: this.t('选择 {0}', expectedFile), filters: { [this.t('可执行文件')]: ['exe'] } });
      this.assertSession(epoch); if (!files?.length || this.cancelSource?.token.isCancellationRequested) return;
      executable = files[0].fsPath;
    }
    const result = await this.call('selectTool', { key, executable }, epoch, progress);
    this.assertSession(epoch); if (this.cancelSource?.token.isCancellationRequested) return;
    await this.saveTools(result.tools, epoch);
    await this.inspect(epoch, progress);
    state.toolInventory = await this.call('tools', { tools: this.tools() }, epoch, progress);
    state.status = this.t('{0} 路径已验证并保存。\n{1}\n请重新预览任务配置后进行编译检查。', title, executable);
    return result;
  }
  render() {
    if (!this.panel) return;
    let unavailable; try { this.supported(); } catch (error) { unavailable = error.message; }
    void this.panel.webview.postMessage({ type: 'state', page: this.page, language: this.language, languagePreference: this.languagePreference, pages: PAGES.map(([id, label, icon]) => [id, this.t(label), icon]), root: this.folder?.uri.fsPath, busy: this.busy, unavailable, state: this.localizeDisplay(viewState(this.state, this.page)), configuredTools: this.tools() });
  }
  renderProgress(percent) {
    if (!this.panel) return;
    this.state.percent = Math.max(0, Math.min(100, Number(percent) || 0));
    const send = () => { this.progressTimer = undefined; this.lastProgressTime = Date.now(); void this.panel?.webview.postMessage({ type: 'progress', status: this.t(this.state.status), percent: this.state.percent, busy: this.busy }); };
    if (Date.now() - (this.lastProgressTime || 0) >= 100) { clearTimeout(this.progressTimer); send(); }
    else if (!this.progressTimer) this.progressTimer = setTimeout(send, 100);
  }
  diskNotice() {
    this.state.savedFilesNotice = this.dirtyWorkspaceDocuments().length ? this.t('检测和预览基于已保存文件，未保存的编辑内容不会被改动。') : undefined;
  }
  needsSave(command, payload) {
    return DISK_COMMANDS.has(command) || command === 'inspectConversion' && payload?.direction === 'CMakeToMdk';
  }
  async call(command, payload, epoch, progress, reviewedPreview) {
    this.assertSession(epoch);
    const saved = this.needsSave(command, payload) ? await this.saveDirtyDocuments(epoch, command === 'importFiles') : false;
    this.diskNotice();
    if (saved && REVIEWED_WRITES.has(command)) throw new Error(this.t('文件在确认期间被编辑并保存。请重新生成当前改动后再确认写入。'));
    this.assertSession(epoch);
    if (reviewedPreview) this.assertPreview(reviewedPreview);
    const fingerprint = JSON.stringify(this.tools());
    const preferredTools = payload?.tools || payload?.commonOptions?.tools;
    if (preferredTools && JSON.stringify(preferredTools) !== fingerprint) throw new Error(this.t('准备操作时工具路径发生变化，请重新检测后再执行。'));
    const result = await this.bridge.request(command, this.folder.uri.fsPath, { ...payload, language: this.language }, this.cancelSource?.token, info => {
      if (epoch !== this.epoch) return;
      const segments = [info.stage, info.message].filter(Boolean);
      const chinese = segments.map(value => Object.keys(this.translations).find(key => this.translations[key] === value) || value).join('：');
      const english = segments.map(value => this.translations[value] || value).join(': ');
      this.translations[chinese] = english; this.state.status = this.t(chinese); this.renderProgress(info.percent); progress?.report({ message: this.state.status });
    });
    this.assertSession(epoch); if (this.cancelSource?.token.isCancellationRequested) throw new Error(this.t('操作已取消。'));
    if (!REVIEWED_WRITES.has(command) && fingerprint !== JSON.stringify(this.tools())) throw new Error(this.t('操作期间工具路径发生变化，请重新检测后再执行。'));
    return result;
  }
  async inspect(epoch, progress) {
    const result = await this.call('inspect', this.options(), epoch, progress); this.state.inspection = result;
    const project = result.project || {};
    if (!this.state.options.sourceTarget && result.targets?.length) this.state.options.sourceTarget = result.targets[0];
    for (const key of ['configurePreset', 'buildPreset', 'buildDirectory', 'targetScript']) if (this.state.options[key] === undefined && project[key]) this.state.options[key] = project[key];
    this.syncConfiguration();
    const savedTools = vscode.workspace.getConfiguration('stm32ProjectHelper', this.folder.uri);
    const missing = Object.entries(TOOL_KEYS).filter(([setting, wire]) => savedTools.get('tools.' + setting) && !this.tools()[wire]).map(([setting]) => TOOL_NAMES[setting][1]);
    this.state.staleToolsNotice = missing.length ? this.t('已保存的工具路径已失效，正在使用重新检测的安装：{0}。请选择并保存所需版本。', missing.join(', ')) : undefined;
    if (!this.state.wizard) this.state.wizard = { step: 0, complete: this.hasManagedTasks() };
    this.state.status = this.t('工程检测完成。'); return result;
  }
  hasManagedTasks() {
    try { return /(?:由 STM32 工程助手管理|Managed by STM32 Project Helper|由 CMake · DAPLink 配置助手管理)/.test(fs.readFileSync(path.join(this.folder.uri.fsPath, '.vscode', 'tasks.json'), 'utf8')); } catch { return false; }
  }
  preview(result, kind) {
    this.state.preview = { ...result, kind, toolFingerprint: JSON.stringify(this.tools()) }; this.state.status = result.summary || this.t('改动已生成。可确认写入，或按需查看文件差异。'); return result;
  }
  releaseDiff(uri) {
    this.previewDocuments.delete(uri);
    const open = new Set(vscode.workspace.textDocuments.filter(document => document.uri.scheme === 'stm32-preview').map(document => document.uri.toString()));
    const id = vscode.Uri.parse(uri).path.split('/')[1];
    for (const [key] of this.previewDocuments) if (vscode.Uri.parse(key).path.split('/')[1] === id && !open.has(key)) this.previewDocuments.delete(key);
  }
  pruneDiffs() {
    const open = new Set(vscode.workspace.textDocuments.filter(document => document.uri.scheme === 'stm32-preview').map(document => document.uri.toString()));
    for (const key of this.previewDocuments.keys()) if (!open.has(key)) this.previewDocuments.delete(key);
  }
  cacheDiff(uri, text) {
    this.pruneDiffs();
    this.previewDocuments.set(uri.toString(), { text });
  }
  async showDiffs(preview, epoch, indices) {
    let selected = Array.isArray(indices) ? [...new Set(indices)].filter(index => Number.isInteger(index) && index >= 0 && index < (preview.changes?.length || 0)) : undefined;
    if (!selected) {
      const picked = await vscode.window.showQuickPick((preview.changes || []).map((change, index) => ({ label: path.relative(this.folder.uri.fsPath, change.path), index, picked: index === 0 })), { canPickMany: true, title: this.t('选择要查看差异的文件') });
      this.assertSession(epoch); if (!picked?.length) return; selected = picked.map(item => item.index);
    }
    for (const index of selected) {
      const change = preview.changes[index];
      this.assertSession(epoch);
      const size = (change.before ? Buffer.byteLength(change.before, 'base64') : 0) + (change.after ? Buffer.byteLength(change.after, 'base64') : 0);
      if (size > 4 * 1024 * 1024) { await vscode.window.showInformationMessage(this.t('该文件超过差异预览大小限制，请在编辑器中直接查看：{0}', change.path)); continue; }
      const uid = crypto.randomUUID();
      const before = vscode.Uri.parse('stm32-preview:/' + uid + '/before/' + encodeURIComponent(path.basename(change.path)));
      const after = vscode.Uri.parse('stm32-preview:/' + uid + '/after/' + encodeURIComponent(path.basename(change.path)));
      // Keep both sides until VS Code opens the pair; release them when its documents close.
      this.pruneDiffs();
      const retained = [...this.previewDocuments.values()].reduce((total, item) => total + item.text.length * 2, 0);
      if (this.previewDocuments.size >= 32 || retained + size * 2 > 32 * 1024 * 1024) { await vscode.window.showInformationMessage(this.t('差异预览缓存已达到上限，请先关闭部分差异页面。')); break; }
      this.previewDocuments.set(before.toString(), { text: change.before === null ? this.t('（文件不存在）\n') : Buffer.from(change.before || '', 'base64').toString('utf8') });
      this.previewDocuments.set(after.toString(), { text: change.after === null ? this.t('（删除文件）\n') : Buffer.from(change.after || '', 'base64').toString('utf8') });
      await vscode.commands.executeCommand('vscode.diff', before, after, `${index + 1}/${preview.changes.length} · ${change.path}`, { preview: selected.length === 1 });
    }
  }
  async applyPreview(epoch, progress) {
    const preview = this.state.preview;
    if (preview?.kind === 'restore') return this.execute('restore', {}, epoch, progress);
    if (!preview || !preview.changes?.length || preview.canApply === false || preview.issues?.some(issue => issue.blocking)) throw new Error(this.t('当前改动无法写入。请解决提示的问题，再点击页面上的“确认写入”重新计算。'));
    this.assertPreview(preview);
    const files = preview.changes.slice(0, 30).map(change => `${change.before === null ? this.t('新增') : change.after === null ? this.t('删除') : this.t('修改')} · ${path.relative(this.folder.uri.fsPath, change.path)}`);
    if (preview.changes.length > files.length) files.push(this.t('另有 {0} 个文件，可在改动列表中查看。', preview.changes.length - files.length));
    this.state.status = this.t('等待确认写入 {0} 个文件。', preview.changes.length); this.render();
    const choice = await vscode.window.showWarningMessage(this.t('确认写入 {0} 个文件？', preview.changes.length),
      { modal: true, detail: [this.t('工程：') + this.folder.uri.fsPath, this.t(preview.summary || preview.label) || '', '', ...files].join('\n') }, this.t('确认写入'));
    if (choice !== this.t('确认写入')) { if (epoch === this.epoch) this.state.status = this.t('已取消写入，工程文件保持原样。'); return false; }
    this.assertSession(epoch);
    this.assertPreview(preview);
    const result = await this.call('applyChanges', { changes: preview.changes, label: preview.canonicalLabel || preview.label }, epoch, progress, preview);
    this.state.preview = undefined; this.state.lastResult = result; this.state.status = this.t('文件变更已保存。可运行软件检查。');
    if (this.state.wizard && preview.kind === 'tasks' && !preview.buildOnly && this.hasManagedTasks()) this.state.wizard = { step: 3, complete: true };
    await this.inspect(epoch, progress); this.state.status = this.t('文件变更已保存。可运行软件检查。');
    return true;
  }
  async writeChanges(previewAction, epoch, progress) {
    this.state.preview = undefined;
    await this.execute(previewAction, {}, epoch, progress);
    const preview = this.state.preview;
    const blockers = preview?.issues?.filter(issue => issue.blocking) || [];
    if (!preview || preview.canApply === false || blockers.length) throw new Error(this.t('当前改动无法写入。') + blockers.map(issue => this.t(issue.title || issue.message || issue.key)).join('；'));
    if (!preview.changes?.length) { this.state.status = this.t('当前配置已是最新，没有需要写入的文件。'); return false; }
    return this.applyPreview(epoch, progress);
  }
  publishProblems(result) {
    this.diagnostics.clear(); const grouped = new Map();
    for (const problem of result.problems || []) {
      if (!problem.filePath) continue;
      const file = path.isAbsolute(problem.filePath) ? problem.filePath : path.resolve(this.folder.uri.fsPath, problem.filePath);
      const line = Math.max(0, (problem.line || 1) - 1), column = Math.max(0, (problem.column || 1) - 1);
      const severity = /warning|警告/i.test(problem.severity) ? vscode.DiagnosticSeverity.Warning : /info|信息/i.test(problem.severity) ? vscode.DiagnosticSeverity.Information : vscode.DiagnosticSeverity.Error;
      const diagnostic = new vscode.Diagnostic(new vscode.Range(line, column, line, column + 1), [problem.rawMessage ? problem.message : this.t(problem.message), problem.suggestion && this.t(problem.suggestion)].filter(Boolean).join('\n'), severity);
      diagnostic.source = 'STM32 · ' + this.t(problem.stage || '构建'); if (!grouped.has(file)) grouped.set(file, []); grouped.get(file).push(diagnostic);
    }
    for (const [file, problems] of grouped) this.diagnostics.set(vscode.Uri.file(file), problems);
  }
  async verify(epoch, progress) {
    if (this.state.options.firmwareElfPath && !fs.existsSync(this.state.options.firmwareElfPath)) this.state.options.firmwareElfPath = undefined;
    const tasks = await this.call('previewTasks', this.options(), epoch, progress);
    if (tasks.changes?.length) {
      this.preview(tasks, 'tasks');
      if (!await this.applyPreview(epoch, progress)) { this.state.status = this.t('已取消任务配置，尚未运行软件检查。'); return; }
    }
    const result = await this.call('verify', this.options(), epoch, progress);
    this.state.verification = result; this.publishProblems(result); if (result.output) this.output.appendLine(result.output);
    this.state.status = result.success ? this.t('软件检查通过。请选择实际 ELF 后预览任务绑定。烧录任务由你在 VS Code 中手动运行。') : this.t('软件检查未通过。任务文件已保留，请查看问题与输出。');
    if (!result.success) return result;
    const firmware = result.firmware || [];
    if (!firmware.length) return result;
    const selected = await vscode.window.showQuickPick(firmware.map(file => ({ label: path.basename(file), description: file, file })), { title: this.t('选择用于任务的实际固件 ELF'), placeHolder: this.t('取消将保留现有任务绑定') });
    this.assertSession(epoch); if (!selected) return result;
    this.state.options.firmwareElfPath = selected.file;
    const preview = await this.call('previewTasks', this.options(), epoch, progress); this.preview(preview, 'firmware');
    if (preview.changes?.length) await this.applyPreview(epoch, progress);
    this.state.status = this.t('软件检查通过。固件任务绑定可在预览中确认；烧录任务由你在 VS Code 中手动运行。'); return result;
  }
  async ensureTasks(epoch, progress, buildOnly = false) {
    const result = await this.call('previewTasks', { ...this.options(), buildOnly }, epoch, progress);
    if (!result.changes?.length) return true;
    this.preview({ ...result, buildOnly }, 'tasks');
    return this.applyPreview(epoch, progress);
  }
  showBuildResult(result, action) {
    this.state[action === 'flash' ? 'flashResult' : 'buildResult'] = { ...result, output: undefined };
    this.state.verification = { ...result, output: undefined };
    this.publishProblems(result); if (result.output) this.output.appendLine(result.output);
    if (!result.success) { this.state.status = this.t('操作未完成，请查看问题说明与完整输出。'); return; }
    const firmware = result.firmware || [];
    if (firmware.length === 1) this.state.options.firmwareElfPath = firmware[0];
    else if (!firmware.includes(this.state.options.firmwareElfPath)) this.state.options.firmwareElfPath = undefined;
    this.state.status = action === 'flash' ? this.t('烧录完成，固件已校验并复位。') : this.t('编译完成。');
  }
  async buildOrFlash(action, epoch, progress) {
    const readyForFlash = !!this.state.inspection?.tools?.openOcd && !!this.state.options.targetScript;
    if (!await this.ensureTasks(epoch, progress, action === 'build' && !readyForFlash)) return;
    if (action === 'build') {
      const result = await this.call('build', this.options(), epoch, progress);
      this.showBuildResult(result, action);
      if (result.success && this.state.options.firmwareElfPath) {
        await this.readMemory(epoch, progress, false);
        this.state.status = this.t('编译完成。');
      }
      return result;
    }
    const built = await this.call('build', this.options(), epoch, progress);
    this.showBuildResult(built, 'build'); if (!built.success) return built;
    const firmware = built.firmware || [];
    if (!this.state.options.firmwareElfPath && firmware.length > 1) {
      const selected = await vscode.window.showQuickPick(firmware.map(file => ({ label: path.basename(file), description: file, file })), { title: this.t('选择要烧录的实际固件 ELF') });
      this.assertSession(epoch); if (!selected) { this.state.status = this.t('已完成编译，尚未选择烧录固件。'); return built; }
      this.state.options.firmwareElfPath = selected.file;
    }
    if (!this.state.options.firmwareElfPath) throw new Error(this.t('没有可用于烧录的当前固件。'));
    const option = this.options(), probe = { 'cmsis-dap': 'DAPLink / CMSIS-DAP', stlink: 'ST-Link', jlink: 'J-Link' }[option.probeType];
    const confirm = this.t('确认烧录');
    const chosen = await vscode.window.showWarningMessage(this.t('将固件写入连接的目标芯片？'), { modal: true,
      detail: [this.folder.uri.fsPath, option.configuration, option.firmwareElfPath, probe, option.adapterSpeed + ' kHz', option.resetMode].join('\n') }, confirm);
    this.assertSession(epoch); if (chosen !== confirm) { this.state.status = this.t('已取消烧录。'); return built; }
    const result = await this.call('flash', { ...option, explicitFlash: true }, epoch, progress);
    this.showBuildResult(result, action); return result;
  }
  async readMemory(epoch, progress, interactive = true) {
    const firmware = this.state.options.firmwareElfPath;
    if (!firmware) { if (interactive) throw new Error(this.t('请先编译并选择一个实际固件 ELF。')); return; }
    try { const result = await this.call('memory', this.options(), epoch, progress); this.state.memory = result; return result; }
    catch (error) {
      this.assertSession(epoch);
      if (interactive) throw error;
      this.output.appendLine(error.message); this.state.memory = { firmwareElfPath: firmware, regions: [], sections: [], totalFlash: null, totalRam: null, notes: [error.message] };
    }
  }
  async operation(action, message = {}) {
    const actions = new Set(['refresh', 'previewTasks', 'writeTasks', 'apply', 'diff', 'verify', 'pickSourceFolder', 'scanSources', 'selectAllSources', 'previewSources', 'writeSources', 'pickImportFiles', 'previewImport', 'importDiff', 'importFiles', 'inspectConversion', 'previewConversion', 'writeConversion', 'tools', 'pickTool', 'repair', 'importTool', 'activateTool', 'history', 'previewRestore', 'restore', 'logs', 'workspace', 'build', 'flash', 'memory', 'startWizard', 'wizardNext', 'wizardBack', 'wizardFinish', 'dismissWizard']);
    if (action === 'setLanguage') return this.setLanguage(message.value);
    if (!actions.has(action)) return;
    if (action === 'logs') { this.output.show(true); return; }
    if (action === 'workspace') return this.pickWorkspace();
    if (this.busy) return;
    if (action === 'selectAllSources') {
      this.state.selectedFiles = message.selected === true ? [...(this.state.scan?.candidateFiles || [])] : []; this.state.preview = undefined; this.state.selectionRevision++;
      void this.panel?.webview.postMessage({ type: 'sourceSelection', all: true, selected: message.selected === true, count: this.state.selectedFiles.length, selectionRevision: this.state.selectionRevision, preview: null }); return;
    }
    try { this.supported(); } catch (error) { this.state.status = error.message; this.render(); await vscode.window.showErrorMessage(error.message); return; }
    const epoch = this.epoch;
    this.diskNotice(); this.busy = true; this.state.percent = 0; this.cancelSource = new vscode.CancellationTokenSource(); this.render();
    try {
      return await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: this.t('STM32 工程助手'), cancellable: true }, async (progress, token) => {
        const subscription = token.onCancellationRequested(() => this.cancelSource?.cancel());
        try {
          if (DISK_ACTIONS.has(action) || action === 'inspectConversion' && this.state.options.direction === 'CMakeToMdk') {
            const saved = await this.saveDirtyDocuments(epoch, ['previewImport', 'importDiff', 'importFiles'].includes(action));
            if (saved && REVIEWED_ACTIONS.has(action)) throw new Error(this.t('编辑内容已保存。请重新预览后再确认写入。'));
          }
          return await this.execute(action, message, epoch, progress);
        } finally { subscription.dispose(); }
      });
    } catch (error) {
      this.output.appendLine(error.stack || error.message);
      if (epoch === this.epoch) { this.state.status = error.message; if (!error.cancelled && !this.cancelSource.token.isCancellationRequested) await vscode.window.showErrorMessage(error.message, this.t('查看输出')).then(choice => { if (choice) this.output.show(true); }); }
    } finally {
      clearTimeout(this.progressTimer); this.progressTimer = undefined;
      this.cancelSource.dispose(); this.cancelSource = undefined; this.busy = false; if (this.pendingLanguage) { this.pendingLanguage = false; this.updateLanguage(); } this.diskNotice(); this.render();
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
      case 'diff': if (state.preview) return this.showDiffs(state.preview, epoch, message.indices); return;
      case 'verify': return this.verify(epoch, progress);
      case 'build': case 'flash': return this.buildOrFlash(action, epoch, progress);
      case 'memory': return this.readMemory(epoch, progress);
      case 'startWizard': state.wizard = { step: 0, complete: false }; return;
      case 'dismissWizard': state.wizard = { step: state.wizard?.step || 0, complete: true }; return;
      case 'wizardBack': state.wizard = { step: Math.max(0, (state.wizard?.step || 0) - 1), complete: false }; return;
      case 'wizardNext': {
        const step = state.wizard?.step || 0;
        if (step === 0) { await this.inspect(epoch, progress); if (!state.inspection?.project?.isCMakeProject) throw new Error(this.t('当前工程没有根目录 CMakeLists.txt，请先在工程转换中生成 CMake 工程。')); }
        if (step === 1 && (!state.inspection?.tools?.cmake || !state.inspection?.tools?.ninja || !state.inspection?.tools?.compiler || state.inspection?.pendingTools?.length)) throw new Error(this.t('请先选择可用的构建工具版本。'));
        if (step === 2) {
          if (!state.options.targetScript) throw new Error(this.t('请先确认芯片对应的 Target 脚本。'));
          await this.execute('previewTasks', {}, epoch, progress);
        }
        state.wizard = { step: Math.min(3, step + 1), complete: false }; return;
      }
      case 'wizardFinish': {
        const saved = await this.writeChanges('previewTasks', epoch, progress);
        if (saved || this.hasManagedTasks()) state.wizard = { step: 3, complete: true }; return saved;
      }
      case 'pickSourceFolder': {
        const folders = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, canSelectMany: false, defaultUri: this.folder.uri, title: this.t('选择当前工程中的源文件夹') });
        this.assertSession(epoch); if (!folders?.length) return;
        const relative = path.relative(this.folder.uri.fsPath, folders[0].fsPath);
        if (relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative)) throw new Error(this.t('源文件夹必须位于当前工作区内。'));
        if (state.folder !== folders[0].fsPath) state.scan = undefined;
        state.folder = folders[0].fsPath; state.preview = undefined;
        return this.execute('scanSources', {}, epoch, progress);
      }
      case 'scanSources': {
        if (!state.folder) throw new Error(this.t('请先选择源文件夹。'));
        const scan = await this.call('scanSources', { folder: state.folder, target: state.options.sourceTarget }, epoch, progress);
        const previous = state.scan ? new Set(state.selectedFiles) : undefined;
        state.scan = scan; state.selectedFiles = scan.candidateFiles.filter(file => previous ? previous.has(file) : scan.selectedFiles?.includes(file)); state.sourceRevision++; state.selectionRevision++;
        if (!state.options.sourceTarget && scan.targets?.length) state.options.sourceTarget = scan.targets[0];
        state.status = this.t('源文件扫描完成。'); return scan;
      }
      case 'selectAllSources': state.selectedFiles = message.selected === true ? [...(state.scan?.candidateFiles || [])] : []; state.preview = undefined; return;
      case 'previewSources': return this.preview(await this.call('previewSources', { folder: state.folder, target: state.options.sourceTarget, selectedFiles: state.selectedFiles }, epoch, progress), 'sources');
      case 'pickImportFiles': {
        const files = await vscode.window.showOpenDialog({ canSelectFiles: true, canSelectFolders: false, canSelectMany: true, title: this.t('选择要复制到工程的本地文件') });
        this.assertSession(epoch); if (files) { state.sourceFiles = files.map(file => file.fsPath); state.importPreview = undefined; } return;
      }
      case 'previewImport': {
        const result = await this.call('previewImport', { folderName: state.options.folderName, sourceFiles: state.sourceFiles }, epoch, progress); state.importPreview = result; state.status = result.summary || this.t('请确认文件复制计划。'); return result;
      }
      case 'importDiff': {
        const plan = state.importPreview?.plan; if (!plan) throw new Error(this.t('请先预览加入文件计划。'));
        let indices = Array.isArray(message.indices) ? [...new Set(message.indices)].filter(index => Number.isInteger(index) && index >= 0 && index < plan.files.length) : undefined;
        if (!indices) {
          const picked = await vscode.window.showQuickPick(plan.files.map((file, index) => ({ label: path.basename(file.destinationPath), description: file.destinationPath, index, picked: index === 0 })), { canPickMany: true, title: this.t('选择要查看差异的文件') });
          this.assertSession(epoch); if (!picked?.length) return; indices = picked.map(item => item.index);
        }
        for (const index of indices) {
          const file = plan.files[index];
          this.assertSession(epoch);
          const before = vscode.Uri.parse('stm32-preview:/' + crypto.randomUUID() + '/new/' + encodeURIComponent(path.basename(file.destinationPath)));
          this.cacheDiff(before, '');
          await vscode.commands.executeCommand('vscode.diff', before, vscode.Uri.file(file.sourcePath), this.t('新增 · ') + file.destinationPath, { preview: indices.length === 1 });
        }
        return;
      }
      case 'importFiles': {
        const preview = state.importPreview;
        if (!preview?.plan) throw new Error(this.t('请先预览加入文件计划。'));
        this.assertSession(epoch);
        const text = preview.plan.folderExisted ? this.t('目标文件夹已存在，是否向其中追加所列文件？') : this.t('确认创建目标文件夹并复制所列文件？');
        const choice = await vscode.window.showWarningMessage(text + '\n' + preview.plan.folderPath + '\n' + preview.plan.files.length + this.t(' 个文件'), { modal: true }, this.t('确认复制'));
        this.assertSession(epoch); if (choice !== this.t('确认复制')) return;
        const result = await this.call('importFiles', { plan: preview.plan, confirmedExistingFolder: preview.plan.folderExisted }, epoch, progress);
        state.importPreview = undefined; state.sourceFiles = []; state.status = this.t('文件已复制。可在“添加源文件”中注册到构建目标。'); return result;
      }
      case 'inspectConversion': {
        state.preview = undefined;
        if (state.options.direction === 'CMakeToMdk') {
          const options = this.options();
          state.status = this.t('CMake → MDK 检查需要运行 CMake 配置，以读取构建目标。'); this.render();
          const choice = await vscode.window.showWarningMessage(this.t('确认运行 CMake 配置以读取转换目标？\n该步骤会在构建目录生成缓存和构建文件。\n工程：') + this.folder.uri.fsPath + this.t('\n构建目录：') + (options.buildDirectory || this.t('由 CMake 预设确定')) + this.t('\n配置：') + options.configuration, { modal: true }, this.t('确认配置并检查'));
          this.assertSession(epoch);
          if (choice !== this.t('确认配置并检查')) { state.status = this.t('已取消 CMake 配置，尚未检查转换目标。'); return; }
          state.status = this.t('正在执行 CMake 配置并读取转换目标。'); this.render();
        }
        const result = await this.call('inspectConversion', { direction: state.options.direction, commonOptions: this.options() }, epoch, progress);
        state.conversion = result; const info = result.inspection || result;
        state.options.target = info.targets?.[0] || result.targets?.[0] || '';
        state.options.sourceProject = info.sourceProjects?.[0] || result.sourceProjects?.[0] || '';
        if (result.buildDirectory) state.options.buildDirectory = result.buildDirectory;
        state.status = state.options.direction === 'CMakeToMdk' && result.buildDirectory ? this.t('CMake 配置已完成，转换目标已读取。') : this.t('转换工程检查完成。'); return result;
      }
      case 'previewConversion': {
        if (!state.conversion) throw new Error(this.t('请先检查转换工程。'));
        return this.preview(await this.call('previewConversion', { direction: state.options.direction, target: state.options.target, sourceProject: state.options.sourceProject, buildDirectory: state.options.buildDirectory, configuration: state.options.configuration }, epoch, progress), 'conversion');
      }
      case 'tools': state.toolInventory = await this.call('tools', { tools: this.tools() }, epoch, progress); state.status = this.t('本地工具列表已刷新。'); return state.toolInventory;
      case 'pickTool': {
        return this.chooseTool(message.key, epoch, progress);
      }
      case 'repair': {
        const choice = await vscode.window.showWarningMessage(this.t('确认检查并下载修复缺失的构建工具？'), { modal: true }, this.t('确认修复'));
        this.assertSession(epoch); if (!choice) return;
        const result = await this.call('repair', { tools: this.tools(), targetScript: state.options.targetScript }, epoch, progress);
        if (result.selectionRequired?.length) {
          await this.inspect(epoch, progress); state.repair = result;
          state.status = this.t('已检测到多个工具安装，请先选择版本，再按需要修复缺失工具。');
          const selected = await vscode.window.showQuickPick(result.selectionRequired.map(item => ({ label: this.t(item.title),
            key: Object.keys(TOOL_NAMES).find(setting => TOOL_NAMES[setting][0] === item.key) })),
          { title: this.t('选择需要指定版本的工具'), placeHolder: this.t('现有工具无需重新下载') }, this.cancelSource?.token);
          this.assertSession(epoch); if (selected) await this.chooseTool(selected.key, epoch, progress);
          return result;
        }
        await this.saveTools(result.tools, epoch); state.repair = result; await this.inspect(epoch, progress);
        state.status = result.problems?.length ? result.problems.map(value => this.t(value)).join('\n') : this.t('工具修复完成。'); return result;
      }
      case 'importTool': {
        const key = await vscode.window.showQuickPick(['cmake', 'ninja', 'compiler', 'openocd'], { title: this.t('选择离线工具种类') });
        this.assertSession(epoch); if (!key) return;
        const files = await vscode.window.showOpenDialog({ canSelectMany: false, filters: { [this.t('ZIP 工具包')]: ['zip'] }, title: this.t('选择本地工具 ZIP 包') });
        this.assertSession(epoch); if (!files?.length) return;
        const result = await this.call('importTool', { key, archive: files[0].fsPath, tools: this.tools() }, epoch, progress);
        state.status = this.t('离线工具导入完成，请在版本列表中激活。'); state.toolInventory = await this.call('tools', { tools: this.tools() }, epoch, progress); return result;
      }
      case 'activateTool': {
        if (!Number.isInteger(message.index) || message.index < 0) return;
        const installation = state.toolInventory?.installations?.[message.index]; if (!installation) return;
        const result = await this.call('activateTool', { installation, tools: this.tools() }, epoch, progress);
        await this.saveTools(result.tools || result, epoch); await this.inspect(epoch, progress);
        state.toolInventory = await this.call('tools', { tools: this.tools() }, epoch, progress); state.status = this.t('工具版本已激活。'); return result;
      }
      case 'history': state.history = await this.call('history', {}, epoch, progress); state.status = this.t('修改记录已刷新。'); return state.history;
      case 'previewRestore': {
        if (!state.history?.records?.some(record => record.id === message.id)) return;
        const result = await this.call('previewRestore', { id: message.id }, epoch, progress);
        return this.preview({ ...result, recordId: message.id }, 'restore');
      }
      case 'restore': {
        const preview = state.preview; if (preview?.kind !== 'restore') throw new Error(this.t('请先预览要还原的记录。'));
        this.assertSession(epoch);
        const conflicts = preview.conflicts || [];
        const text = conflicts.length ? this.t('下列文件在记录之后被修改。确认覆盖这些更改并还原？\n') + conflicts.join('\n') : this.t('确认还原此修改记录？');
        const label = conflicts.length ? this.t('确认覆盖并还原') : this.t('确认还原');
        const choice = await vscode.window.showWarningMessage(text, { modal: true }, label); this.assertSession(epoch); if (choice !== label) return;
        this.assertPreview(preview);
        const result = await this.call('restore', { id: preview.recordId, changes: preview.changes, overwriteChangedFiles: conflicts.length > 0 }, epoch, progress, preview);
        state.preview = undefined; await this.inspect(epoch, progress); state.history = await this.call('history', {}, epoch, progress); state.status = this.t('修改记录已还原。'); return result;
      }
    }
  }
  snapshot() { return { language: this.language, languagePreference: this.languagePreference, root: this.folder?.uri.fsPath, page: this.page, busy: this.busy, ready: !!this.state.inspection && !this.busy, trusted: vscode.workspace.isTrusted, panelOpen: !!this.panel, navigation: PAGES.map(item => this.t(item[1])), state: this.localizeDisplay(JSON.parse(JSON.stringify(this.state))) }; }
}

async function activate(context) {
  const workbench = new Workbench(context);
  context.subscriptions.push({ dispose() { workbench.cancelSource?.cancel(); clearTimeout(workbench.progressTimer); workbench.bridge.dispose(); workbench.previewDocuments.clear(); workbench.projectCandidates.clear(); workbench.panel?.dispose(); } });
  if (vscode.workspace.getConfiguration('stm32ProjectHelper').get('autoOpen', true) && vscode.workspace.isTrusted && process.platform === 'win32' && process.arch === 'x64' && !vscode.env.remoteName && workbench.folder) {
    const workspace = workbench.folder;
    const candidates = await workbench.projectsIn(workspace);
    const activeFile = vscode.window.activeTextEditor?.document.uri.fsPath;
    const selected = nearestProject(candidates, activeFile) || (candidates.length === 1 ? candidates[0] : undefined);
    if (selected) { workbench.setFolder(workbench.projectFolder(workspace, selected.root)); await workbench.open(); }
    else if (candidates.length > 1) { workbench.state.status = workbench.t('检测到多个 STM32 工程，请选择当前工程。'); await workbench.open(); await workbench.pickWorkspace(); }
  }
  return { getSnapshot: () => workbench.snapshot(), openPage: page => workbench.open(page), request: (command, payload) => workbench.operation(command === 'inspect' ? 'refresh' : command, payload),
    snapshot: () => workbench.snapshot(), open: page => workbench.open(page), operation: (action, payload) => workbench.operation(action, payload) };
}
function deactivate() {}
module.exports = { activate, deactivate };
