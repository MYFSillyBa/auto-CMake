'use strict';
const { spawn } = require('node:child_process');
const path = require('node:path');
const readline = require('node:readline');
const crypto = require('node:crypto');

function request(root, command, payload = {}, events = []) {
  const executable = path.resolve(__dirname, '..', 'server', 'STM32ProjectHelper.ExtensionHost.exe');
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    const child = spawn(executable, ['--stdio'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    const timer = setTimeout(() => { child.kill(); reject(new Error(`后台请求超时：${command}`)); }, 180000);
    let result, error, stderr = '';
    child.stderr.on('data', chunk => { stderr += chunk.toString(); });
    const reader = readline.createInterface({ input: child.stdout });
    reader.on('line', line => {
      try {
        const event = JSON.parse(line);
        if (event.id !== id) return;
        events.push(event);
        if (event.type === 'result') { result = event.data; child.stdin.end(); }
        if (event.type === 'error') { error = event.message; child.stdin.end(); }
      } catch (ex) { error = `无效后台输出：${line}\n${ex.message}`; }
    });
    child.on('error', ex => { clearTimeout(timer); reject(ex); });
    child.on('close', code => {
      clearTimeout(timer); reader.close();
      if (error || code !== 0 || result === undefined) reject(new Error(error || stderr || `后台退出 ${code}`));
      else resolve(result);
    });
    child.stdin.write(JSON.stringify({ id, command, root, payload }) + '\n');
  });
}
module.exports = { request };
