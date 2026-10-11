'use strict';
const { spawn } = require('child_process');
const readline = require('readline');
const crypto = require('crypto');
const COMMANDS = new Set(['inspect', 'previewTasks', 'applyChanges', 'verify', 'scanSources', 'previewSources', 'previewImport', 'importFiles', 'inspectConversion', 'previewConversion', 'history', 'previewRestore', 'restore', 'tools', 'selectTool', 'repair', 'importTool', 'activateTool']);

class HostBridge {
  constructor(executable, output) { this.executable = executable; this.output = output; this.active = new Set(); }
  request(command, root, payload, token, progress) {
    if (!COMMANDS.has(command)) return Promise.reject(new Error('不支持的后端操作。'));
    if (token?.isCancellationRequested) return Promise.reject(new Error('操作已取消。'));
    return new Promise((resolve, reject) => {
      const id = crypto.randomUUID();
      const child = spawn(this.executable, [], { cwd: root, windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
      this.active.add(child);
      let terminal, failure, cancelTimer, settled = false;
      const lines = readline.createInterface({ input: child.stdout });
      const cancel = () => {
        if (settled) return;
        failure = new Error('操作已取消。');
        if (!child.stdin.destroyed) child.stdin.write('{"cancel":true}\n', () => {});
        cancelTimer = setTimeout(() => child.kill(), 2500);
      };
      const subscription = token?.onCancellationRequested(cancel);
      const finish = (error) => {
        if (settled) return;
        settled = true; clearTimeout(cancelTimer); subscription?.dispose(); lines.close(); this.active.delete(child);
        if (error || failure) reject(failure || error); else if (terminal !== undefined) resolve(terminal); else reject(new Error('本地后端未返回结果。请查看输出日志。'));
      };
      child.on('error', error => finish(new Error('无法启动本地后端：' + error.message)));
      child.stdin.on('error', error => { if (!terminal && !failure) failure = error; });
      child.stderr.on('data', data => this.output.append(data.toString()));
      lines.on('line', line => {
        if (!line.trim()) return;
        let envelope;
        try { envelope = JSON.parse(line); } catch { this.output.appendLine(line); return; }
        if (envelope.id !== id) return;
        if (envelope.type === 'progress') {
          const info = envelope.data || {}; this.output.appendLine([info.stage, info.message].filter(Boolean).join('：')); progress?.(info);
        } else if (envelope.type === 'result') terminal = envelope.data;
        else if (envelope.type === 'error') { failure = new Error(envelope.message || '本地后端操作失败。'); failure.data = envelope.data; }
      });
      child.on('close', code => finish(code && !failure ? new Error('本地后端退出代码：' + code) : undefined));
      this.output.appendLine('[' + command + '] ' + root);
      child.stdin.write(JSON.stringify({ id, command, root, payload: payload || {} }) + '\n', error => { if (error && !failure) failure = error; });
    });
  }
  dispose() { for (const child of this.active) child.kill(); this.active.clear(); }
}
module.exports = { HostBridge };
