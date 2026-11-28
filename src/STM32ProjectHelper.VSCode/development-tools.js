'use strict';
const vscode = require('vscode');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { SerialSession } = require('./serial-session');
const { UsageStatistics } = require('./usage-statistics');
const { createFaultReport } = require('./fault-report');
const { fingerprintProject, projectDelta, RELEVANT } = require('./cubemx-sync');
const CLANGD = 'llvm-vs-code-extensions.vscode-clangd';
const INTELLIGENCE_BACKUP = 'intelligence.userSettingsBackup';
const ENGINE = 'C_Cpp.intelliSenseEngine';
const CLANGD_ARGS = 'clangd.arguments';
const sameValue = (a, b) => JSON.stringify(a) === JSON.stringify(b);

function withoutPinnedDatabase(args) {
  if (!Array.isArray(args)) return args;
  const kept = [];
  for (let i = 0; i < args.length; i++) {
    const argument = args[i];
    if (typeof argument === 'string' && argument.startsWith('--compile-commands-dir=')) continue;
    if (argument === '--compile-commands-dir') {
      if (typeof args[i + 1] === 'string' && !args[i + 1].startsWith('-')) i++;
      continue;
    }
    kept.push(argument);
  }
  return kept;
}

const ACTIONS = new Set(['writeClangd', 'openClangdExtension', 'prepareCompileDatabase', 'writeDebug', 'startDebug', 'inspectSync', 'previewSync', 'writeSync', 'probeCheck', 'analyzeFirmware', 'pickMapFile', 'hardFault', 'rtos', 'listSerial', 'connectSerial', 'disconnectSerial', 'sendSerial', 'exportSerial', 'exportReport', 'toggleTelemetry']);
const BLOCK = /^# BEGIN CMakeDapLink managed folders\r?\n[\s\S]*?^# END CMakeDapLink managed folders/gm;
function decodeRegisters(value) {
  const text = String(value || '').trim(); if (text.length > 32768) throw new Error('寄存器输入过大。');
  const values = text.startsWith('{') ? JSON.parse(text) : Object.fromEntries([...text.matchAll(/\b([A-Za-z][A-Za-z0-9]*)\s*[:=]\s*(0x[0-9a-fA-F]+|\d+)\b/g)].map(match => [match[1].toUpperCase(), match[2]]));
  if (!values || Array.isArray(values) || typeof values !== 'object' || !Object.keys(values).length) throw new Error('请输入寄存器 JSON 或 CFSR=0x00000000 形式的值。');
  return Object.fromEntries(Object.entries(values).map(([key, value]) => [key.toUpperCase(), String(value)]));
}
class DevelopmentTools {
  constructor(workbench) {
    this.workbench = workbench; this.baselines = new Map(); this.pendingFiles = new Set();
    this.statistics = new UsageStatistics(workbench.context, () => vscode.env.isTelemetryEnabled === true);
    this.serial = new SerialSession(serial => { this.workbench.state.serial = serial; void this.workbench.panel?.webview.postMessage({ type: 'serialData', serial }); });
    this.watcher = vscode.workspace.createFileSystemWatcher('**/*');
    for (const event of [this.watcher.onDidCreate, this.watcher.onDidChange, this.watcher.onDidDelete]) event(uri => this.changed(uri));
    workbench.context.subscriptions.push(this.watcher);
    if (vscode.env.onDidChangeTelemetryEnabled) workbench.context.subscriptions.push(vscode.env.onDidChangeTelemetryEnabled(allowed => { if (!allowed) for (const controller of this.statistics.inflight) controller.abort(); this.workbench.render(); }));
  }
  t(source, ...args) { return this.workbench.t(source, ...args); }
  intelligenceStatus() {
    const scoped = vscode.workspace.getConfiguration(undefined, this.workbench.folder?.uri);
    const engine = scoped.inspect(ENGINE), args = scoped.inspect(CLANGD_ARGS);
    return {
      clangdInstalled: !!vscode.extensions.getExtension(CLANGD),
      hasBackup: !!this.workbench.context.globalState.get(INTELLIGENCE_BACKUP),
      engineOverride: [engine?.workspaceValue, engine?.workspaceFolderValue].some(value => value !== undefined && value !== 'disabled'),
      databaseOverride: [args?.workspaceValue, args?.workspaceFolderValue].some(value => !sameValue(value, withoutPinnedDatabase(value)))
    };
  }
  async updateIntelligence(restore) {
    const w = this.workbench;
    if (this.intelligenceBusy) return;
    this.intelligenceBusy = true;
    try {
      const config = vscode.workspace.getConfiguration();
      const storage = w.context.globalState;
      let backup = storage.get(INTELLIGENCE_BACKUP);
      const current = key => vscode.workspace.getConfiguration().inspect(key)?.globalValue;
      if (restore) {
        if (!backup) return;
        for (const [key, values] of Object.entries(backup)) {
          if (sameValue(current(key), values.after)) await config.update(key, values.before, vscode.ConfigurationTarget.Global);
        }
        await storage.update(INTELLIGENCE_BACKUP, undefined);
        w.state.intelligenceNotice = this.t('已恢复本工具修改的用户设置，之后手动编辑的设置已保留。');
      } else {
        if (!vscode.extensions.getExtension(CLANGD)) {
          w.state.intelligenceNotice = this.t('请先安装并启用 clangd 扩展，再应用全局修复。');
          return;
        }
        const desired = [
          [ENGINE, 'disabled'],
          ['clangd.detectExtensionConflicts', false],
          ['clangd.onConfigChanged', 'restart'],
          [CLANGD_ARGS, withoutPinnedDatabase(current(CLANGD_ARGS))]
        ];
        if (current('clangd.enable') === false) desired.unshift(['clangd.enable', true]);
        backup = { ...backup };
        for (const [key, after] of desired) {
          const before = current(key);
          if (sameValue(before, after)) continue;
          if (!backup[key] || !sameValue(before, backup[key].after)) backup[key] = { before, after };
          await storage.update(INTELLIGENCE_BACKUP, backup);
          await config.update(key, after, vscode.ConfigurationTarget.Global);
        }
        w.state.intelligenceNotice = this.t('全局代码提示修复已生效。请用构建结果核对仍存在的诊断。');
      }
    } catch (error) {
      w.output.appendLine(error.message);
      w.state.intelligenceNotice = this.t('更新用户设置失败：{0}', error.message);
    } finally {
      this.intelligenceBusy = false;
      w.render();
    }
  }
  async confirmTelemetry() {
    const answer = await vscode.window.showInformationMessage(this.t('同意向应用开发者发送匿名使用统计？'), {
      modal: true,
      detail: [this.statistics.snapshot().host,
        this.t('统计默认关闭，可在“开发环境 → 隐私设置”中修改。'),
        this.t('仅发送随机匿名标识、版本、语言、功能名称与成功状态。网络服务仍可看到来源 IP；不发送工程路径、源码、日志或芯片序列号。VS Code 总统计开关关闭时不发送。')].filter(Boolean).join('\n\n')
    }, this.t('同意并启用'), this.t('暂不开启'));
    return answer === this.t('同意并启用');
  }
  async promptTelemetryOnce() {
    await this.statistics.promptOnce(() => this.confirmTelemetry());
    this.workbench.render();
  }
  options() { const w = this.workbench; return { ...w.options(), compilationDatabaseDirectory: w.state.options.compilationDatabaseDirectory || undefined, debugName: w.state.options.debugName || undefined, debugMode: w.state.options.debugMode || 'launch', gdbEndpoint: w.state.options.gdbEndpoint || 'localhost:3333', mapFilePath: w.state.options.mapFilePath || undefined }; }
  changed(uri) {
    const w = this.workbench, root = w.folder?.uri.fsPath; if (!root || uri.scheme !== 'file' || !RELEVANT.test(uri.fsPath)) return;
    const relative = path.relative(root, uri.fsPath); if (relative.startsWith('..') || path.isAbsolute(relative) || relative.split(/[\\/]/).some(part => ['.git', '.vscode', 'build', 'bin', 'obj', 'node_modules'].includes(part.toLowerCase()))) return;
    this.pendingFiles.add(relative); clearTimeout(this.syncTimer);
    this.syncTimer = setTimeout(() => this.flushChanges(), 800);
  }
  async flushChanges() {
    const w = this.workbench; if (w.busy) { this.syncTimer = setTimeout(() => this.flushChanges(), 500); return; }
    try { await this.inspectSync(); w.render(); } catch (error) { w.output.appendLine(this.t(error.message)); }
    this.pendingFiles.clear();
  }
  readBlocks(root) { try { return fs.readFileSync(path.join(root, 'CMakeLists.txt'), 'utf8').match(BLOCK) || []; } catch { return []; } }
  blockKey(root) { return 'cubemx.managedBlocks.' + crypto.createHash('sha256').update(root.toLowerCase()).digest('hex'); }
  async rememberBlocks() {
    const w = this.workbench, root = w.folder?.uri.fsPath, baseline = this.baselines.get(root); if (!baseline) return;
    const blocks = this.readBlocks(root);
    if (blocks.length) { baseline.blocks = blocks; await w.context.workspaceState.update(this.blockKey(root), blocks); }
  }
  async baseline() {
    const w = this.workbench, root = w.folder?.uri.fsPath; if (!root || this.baselines.has(root)) return;
    const files = await fingerprintProject(root); if (w.folder?.uri.fsPath !== root) return;
    const current = this.readBlocks(root), saved = w.context.workspaceState.get(this.blockKey(root), []);
    const blocks = current.length ? current : Array.isArray(saved) ? saved.filter(block => typeof block === 'string' && block.length <= 4 * 1024 * 1024).slice(0, 8) : [];
    this.baselines.set(root, { files, blocks });
    if (current.length) await w.context.workspaceState.update(this.blockKey(root), current);
    const missing = !current.length && blocks.length > 0;
    w.state.sync = { changed: missing, files: missing ? ['CMakeLists.txt'] : [], notes: [this.t('已记录当前会话的 CubeMX 配置基线。')] };
    if (missing) { w.state.syncNotice = this.t('工程生成配置已变化，可在开发配置页面预览 CubeMX 同步。'); return; }
    w.state.syncNotice = undefined;
  }
  async inspectSync() {
    const w = this.workbench, root = w.folder.uri.fsPath, epoch = w.epoch;
    const after = await fingerprintProject(root); w.assertSession(epoch);
    const previous = this.baselines.get(root);
    if (!previous) this.baselines.set(root, { files: after, blocks: this.readBlocks(root) });
    const files = previous ? projectDelta(previous.files, after) : [];
    if (previous?.blocks?.length && !this.readBlocks(root).length && !files.includes('CMakeLists.txt')) files.push('CMakeLists.txt');
    w.state.sync = { changed: files.length > 0, files, notes: [this.t(files.length ? '检测到工程配置变化，请预览同步差异后确认。' : '当前配置与会话基线一致。')] };
    w.state.syncNotice = files.length ? this.t('工程生成配置已变化，可在开发配置页面预览 CubeMX 同步。') : undefined;
    return w.state.sync;
  }
  async syncPreview(epoch, progress) {
    const w = this.workbench, root = w.folder.uri.fsPath; await this.inspectSync();
    let preview, inspection;
    try { inspection = await w.call('inspectConversion', { direction: 'MdkToCMake' }, epoch, progress); }
    catch (error) { if (error.cancelled || w.cancelSource?.token.isCancellationRequested) throw error; w.output.appendLine(error.message); }
    if (inspection?.sourceProjects?.length) {
      const projects = inspection.sourceProjects;
      const sourceProject = projects.length === 1 ? projects[0] : await vscode.window.showQuickPick(projects, { title: this.t('选择 CubeMX 的 MDK 工程作为同步来源') });
      w.assertSession(epoch); if (!sourceProject) return;
      let target = inspection.targets?.[0];
      if (inspection.targets?.length > 1) target = await vscode.window.showQuickPick(inspection.targets, { title: this.t('选择要同步的构建目标') });
      w.assertSession(epoch); if (!target) return;
      preview = await w.call('previewConversion', { direction: 'MdkToCMake', sourceProject, target, configuration: w.state.options.configuration }, epoch, progress);
    } else {
      const file = path.join(root, 'CMakeLists.txt'); if (!fs.existsSync(file)) throw new Error(this.t('工程没有可同步的 CMakeLists.txt 或 MDK 工程。'));
      preview = { changes: [], label: '同步 CubeMX 工程配置', summary: this.t('保留 CubeMX 生成配置，并恢复本工具管理的源文件块。'), notes: [this.t('没有 MDK 来源时，不重新生成外设代码或猜测 CMake 编译参数。')] };
    }
    const baseline = this.baselines.get(root), currentBlocks = this.readBlocks(root), remembered = currentBlocks.length ? currentBlocks : baseline?.blocks || [];
    const cmakeFile = path.join(root, 'CMakeLists.txt');
    let change = preview.changes.find(item => path.resolve(item.path).toLowerCase() === cmakeFile.toLowerCase());
    const before = fs.existsSync(cmakeFile) ? fs.readFileSync(cmakeFile) : null;
    let content = change?.after ? Buffer.from(change.after, 'base64').toString('utf8') : before?.toString('utf8') || '';
    const additions = remembered.filter(block => !content.includes(block));
    if (additions.length) {
      content += '\n' + additions.join('\n') + '\n';
      if (!change) { change = { path: cmakeFile, before: before?.toString('base64') || null }; preview.changes.push(change); }
      change.after = Buffer.from(content).toString('base64');
    }
    w.preview(preview, 'sync'); return preview;
  }
  async confirmHardware(epoch, source) {
    const w = this.workbench, label = this.t('确认连接硬件');
    const answer = await vscode.window.showWarningMessage(this.t(source), { modal: true, detail: this.t('操作可能短暂停止或复位芯片，不会写入固件。请避免与其他调试会话同时使用。') }, label);
    w.assertSession(epoch); return answer === label;
  }
  async probeCheck(epoch, progress, confirmed = false) {
    const w = this.workbench;
    if (!confirmed && !await this.confirmHardware(epoch, '将连接所选下载器并检测芯片？')) return;
    w.state.probeCheck = undefined;
    const result = await w.call('probeCheck', { ...this.options(), explicitHardware: true }, epoch, progress); w.state.probeCheck = result;
    if (result.output) w.output.appendLine(result.output); return result;
  }
  async startDebug(epoch, progress) {
    const w = this.workbench, state = w.state;
    if (vscode.debug.activeDebugSession) throw new Error(this.t('请先结束当前调试会话。'));
    if (!['launch', 'attach'].includes(this.options().debugMode)) throw new Error(this.t('调试模式必须为 launch 或 attach。'));
    if (!vscode.extensions.getExtension('marus25.cortex-debug')) {
      await vscode.commands.executeCommand('workbench.extensions.search', '@id:marus25.cortex-debug');
      throw new Error(this.t('请安装 Cortex-Debug 扩展，再启动调试。'));
    }
    const launching = this.options().debugMode === 'launch';
    if (launching) {
      const preferred = state.options.firmwareElfPath;
      const build = () => w.call('build', { ...this.options(), ensureFirmware: true }, epoch, progress);
      let built = await build();
      if (built.needsFirmware) {
        const selected = await vscode.window.showQuickPick(built.firmware.map(file => ({ label: path.basename(file), description: file, file })), { title: this.t('选择用于调试的最新 ELF') }, w.cancelSource?.token);
        w.assertSession(epoch);
        if (!selected) { state.status = this.t('未选择调试固件。'); return; }
        state.options.firmwareElfPath = selected.file;
        built = await build();
      }
      w.showBuildResult(built, 'build');
      if (!built.success) return built;
      const firmware = built.firmware || [];
      if (!firmware.length) throw new Error(this.t('本次编译没有生成可调试的 ELF。'));
      if (firmware.length > 1 && !state.options.firmwareElfPath) {
        const selected = await vscode.window.showQuickPick(firmware.map(file => ({ label: path.basename(file), description: file, file })), { title: this.t('选择用于调试的最新 ELF') }, w.cancelSource?.token);
        w.assertSession(epoch);
        if (!selected) { state.status = this.t('未选择调试固件。'); return; }
        state.options.firmwareElfPath = selected.file;
      } else if (preferred && firmware.some(file => file.toLowerCase() === preferred.toLowerCase())) {
        state.options.firmwareElfPath = firmware.find(file => file.toLowerCase() === preferred.toLowerCase());
      }
    }
    const options = this.options();
    const preview = await w.call('previewDebug', options, epoch, progress);
    const config = preview.configuration;
    if (config?.request !== options.debugMode) throw new Error(this.t('调试模式必须为 launch 或 attach。'));
    if (preview.changes?.length) {
      w.preview(preview, 'writeDebug');
      if (!await w.applyPreview(epoch, progress, false, false)) return;
    }
    await w.rememberBuildSelection();
    w.assertSession(epoch);
    if (vscode.debug.activeDebugSession) throw new Error(this.t('请先结束当前调试会话。'));
    // The saved F5 entry keeps its preLaunchTask. This invocation already built
    // successfully and uses the ELF returned for the just-completed build.
    const configuration = { ...config };
    delete configuration.preLaunchTask;
    const started = await vscode.debug.startDebugging(w.folder.workspaceFolder || w.folder, configuration);
    state.status = this.t(started ? '调试会话已启动。' : '调试会话未启动，请查看调试控制台。');
    return { success: started };
  }
  async execute(action, message, epoch, progress) {
    const w = this.workbench, state = w.state;
    switch (action) {
      case 'writeClangd': case 'writeDebug': {
        const options = { ...this.options(), disableWorkspaceIntelliSense: false };
        let preview = await w.call(action === 'writeClangd' ? 'previewClangd' : 'previewDebug', options, epoch, progress);
        if (action === 'writeClangd' && preview.compileDatabaseFound === false) {
          const active = state.options.buildDirectory || state.inspection?.project?.buildDirectory;
          if (active && path.resolve(w.folder.uri.fsPath, active).toLowerCase() !== path.dirname(preview.compileDatabasePath).toLowerCase())
            throw new Error(this.t('指定目录没有编译数据库，请选择已有数据库或清空目录以使用当前构建目录。'));
          const generated = await this.execute('prepareCompileDatabase', {}, epoch, progress);
          if (!generated?.success) return;
          preview = await w.call('previewClangd', options, epoch, progress);
          if (!preview.compileDatabaseFound) throw new Error(this.t('编译数据库生成失败，请查看输出。'));
        }
        w.preview(preview, action); if (preview.changes?.length) return w.applyPreview(epoch, progress, false);
        state.status = this.t('当前配置已是最新，没有需要写入的文件。'); return;
      }
      case 'openClangdExtension': return vscode.commands.executeCommand('workbench.extensions.search', '@id:' + CLANGD);
      case 'prepareCompileDatabase': {
        const result = await w.call('prepareCompileDatabase', { ...this.options(), explicitConfigure: true }, epoch, progress); state.compileDatabase = result;
        if (result.output) w.output.appendLine(result.output); state.status = this.t(result.success ? '编译数据库已生成。' : '编译数据库生成失败，请查看输出。'); return result;
      }
      case 'startDebug': return this.startDebug(epoch, progress);
      case 'inspectSync': return this.inspectSync();
      case 'previewSync': return this.syncPreview(epoch, progress);
      case 'writeSync': { const preview = await this.syncPreview(epoch, progress); if (!preview) return; if (preview.changes?.length && !await w.applyPreview(epoch, progress)) return; const root = w.folder.uri.fsPath; this.baselines.delete(root); await this.baseline(); return; }
      case 'probeCheck': return this.probeCheck(epoch, progress);
      case 'pickMapFile': { const selected = await vscode.window.showOpenDialog({ canSelectMany: false, filters: { MAP: ['map'] }, defaultUri: w.folder.uri }); w.assertSession(epoch); if (selected?.length) { if (!selected[0].fsPath.toLowerCase().startsWith(w.folder.uri.fsPath.toLowerCase() + path.sep)) throw new Error(this.t('请选择当前工程内的 MAP 文件。')); state.options.mapFilePath = selected[0].fsPath; } return; }
      case 'analyzeFirmware': state.advancedAnalysis = await w.call('analyzeFirmware', this.options(), epoch, progress); return state.advancedAnalysis;
      case 'hardFault': state.hardFault = await w.call('hardFault', { ...this.options(), registers: decodeRegisters(state.options.hardFaultRegisters) }, epoch, progress); return state.hardFault;
      case 'rtos': {
        const endpoint = /^(localhost|127\.0\.0\.1|\[::1\]):([0-9]{1,5})$/i.exec(state.options.gdbEndpoint || 'localhost:3333');
        if (!endpoint || Number(endpoint[2]) < 1 || Number(endpoint[2]) > 65535) throw new Error(this.t('GDB 地址仅接受 localhost、127.0.0.1 或 [::1] 与有效端口。'));
        if (!await this.confirmHardware(epoch, '将连接现有 GDB Server 并暂停目标，以读取 FreeRTOS 任务快照？')) return;
        state.rtos = undefined; state.rtos = await w.call('rtos', { ...this.options(), gdbHost: endpoint[1], gdbPort: Number(endpoint[2]), explicitHardware: true }, epoch, progress); return state.rtos;
      }
      case 'listSerial': state.serial = await this.serial.list(); return;
      case 'connectSerial': state.serial = await this.serial.connect(state.options.serialPort || '', Number(state.options.serialBaud || 115200), state.options.serialMode || 'ASCII'); return { success: true };
      case 'disconnectSerial': state.serial = await this.serial.disconnect(); return;
      case 'sendSerial': if (!this.serial.state.connected) throw new Error(this.t('请先连接串口。')); state.serial = await this.serial.send(state.options.serialText || '', state.options.serialMode || 'ASCII', state.options.serialNewline || 'none'); return { success: true };
      case 'exportSerial': return this.saveText(this.serial.export(), 'serial-log.txt', epoch);
      case 'exportReport': {
        const text = createFaultReport({ version: w.context.extension.packageJSON.version, language: w.language, configuration: state.options.configuration, probe: state.options.probeType,
          toolVersions: state.inspection?.versions?.map(item => ({ tool: item.key, version: item.version })), buildSuccess: state.buildResult?.success, flashSuccess: state.flashResult?.success, problems: state.buildResult?.problems || state.verification?.problems }, w.output.text, w.folder.uri.fsPath, process.env.USERPROFILE);
        const document = await vscode.workspace.openTextDocument({ language: 'markdown', content: text }); await vscode.window.showTextDocument(document, { preview: true });
        const answer = await vscode.window.showInformationMessage(this.t('故障报告已脱敏并打开预览。请检查内容，确认后保存；不会自动上传。'), { modal: true }, this.t('保存报告'));
        w.assertSession(epoch); if (answer === this.t('保存报告')) { const savedPath = await this.saveText(document.getText(), 'stm32-diagnostic-report.md', epoch); state.report = { path: savedPath, redacted: true }; return savedPath ? { success: true } : undefined; } return;
      }
      case 'toggleTelemetry': {
        const enabled = message.enabled === true;
        if (enabled) { const accepted = await this.confirmTelemetry(); w.assertSession(epoch); if (!accepted) return; }
        await this.statistics.enable(enabled); state.telemetry = this.statistics.snapshot(); return;
      }
    }
  }
  async saveText(text, filename, epoch) { const w = this.workbench; const uri = await vscode.window.showSaveDialog({ defaultUri: vscode.Uri.file(path.join(w.folder.uri.fsPath, filename)) }); w.assertSession(epoch); if (uri) { if (fs.existsSync(uri.fsPath)) { const answer = await vscode.window.showWarningMessage(this.t('目标文件已存在，确认覆盖？'), { modal: true }, this.t('确认覆盖')); w.assertSession(epoch); if (answer !== this.t('确认覆盖')) return; } await vscode.workspace.fs.writeFile(uri, Buffer.from(text, 'utf8')); return uri.fsPath; } }
  dispose() { clearTimeout(this.syncTimer); this.serial.dispose(); this.statistics.dispose(); this.baselines.clear(); }
}
module.exports = { DevelopmentTools, ACTIONS, decodeRegisters };
