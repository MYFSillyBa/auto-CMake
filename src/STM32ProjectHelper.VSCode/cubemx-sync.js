'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const RELEVANT = /(?:\.ioc|\.uvprojx|\.sct|\.ld|CMakeLists\.txt|CMakePresets\.json)$/i;
const SKIP = new Set(['.git', '.vscode', 'build', 'bin', 'obj', 'node_modules', '.cache', '.cmake', 'cmakefiles']);
async function fingerprintProject(root, signal) {
  const queue = [root], inventory = new Map(); let directories = 0, total = 0;
  while (queue.length && directories++ < 6000) {
    if (signal?.aborted) throw Object.assign(new Error('操作已取消。'), { cancelled: true });
    const directory = queue.shift(); let entries; try { entries = await fs.readdir(directory, { withFileTypes: true }); } catch { continue; }
    for (const entry of entries) {
      if (entry.isSymbolicLink()) continue;
      const file = path.join(directory, entry.name);
      if (entry.isDirectory()) { if (!SKIP.has(entry.name.toLowerCase())) queue.push(file); continue; }
      if (!entry.isFile() || !RELEVANT.test(entry.name)) continue;
      const stat = await fs.stat(file); if (stat.size > 4 * 1024 * 1024 || (total += stat.size) > 32 * 1024 * 1024) throw new Error('工程同步输入超过扫描大小限制。');
      inventory.set(path.relative(root, file).replace(/\\/g, '/'), crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex'));
    }
  }
  if (queue.length) throw new Error('工程同步目录过多，请选择更具体的工程根目录。');
  return inventory;
}
function projectDelta(before, after) { return [...new Set([...before.keys(), ...after.keys()])].filter(file => before.get(file) !== after.get(file)).sort(); }
module.exports = { fingerprintProject, projectDelta, RELEVANT };
