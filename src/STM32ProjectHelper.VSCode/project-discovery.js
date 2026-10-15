'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const SKIP = new Set(['.git', '.vs', '.vscode', '.cache', '.cmake', 'node_modules', 'build', 'cmakefiles', 'bin', 'obj', 'drivers', 'middlewares', 'cmsis', 'third_party', '.pio']);
const CHIP = /\bSTM32(?:WBA|[A-Z]{1,2})[0-9][A-Z0-9]{2,12}\b/i;

async function readSmall(file) {
  try { const stat = await fs.stat(file); return stat.isFile() && stat.size <= 1024 * 1024 ? await fs.readFile(file, 'utf8') : ''; }
  catch { return ''; }
}
function contains(root, file) {
  const relative = path.relative(root, file);
  return relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}

async function discoverProjects(workspaceRoot, { signal, maximumDirectories = 6000 } = {}) {
  const root = path.resolve(workspaceRoot), queue = [root], found = new Map();
  let visited = 0;
  const add = (directory, evidence, chip) => {
    directory = path.resolve(directory);
    if (!contains(root, directory)) return;
    const previous = found.get(directory.toLowerCase());
    if (previous) { if (!previous.evidence.includes(evidence)) previous.evidence.push(evidence); return; }
    found.set(directory.toLowerCase(), { root: directory, evidence: [evidence], chip: chip || undefined });
  };
  while (queue.length && visited++ < maximumDirectories) {
    if (signal?.aborted) break;
    const directory = queue.shift();
    let entries; try { entries = await fs.readdir(directory, { withFileTypes: true }); } catch { continue; }
    for (const entry of entries) {
      if (signal?.aborted) break;
      const file = path.join(directory, entry.name);
      if (entry.isSymbolicLink()) continue;
      if (entry.isDirectory()) { if (!SKIP.has(entry.name.toLowerCase())) queue.push(file); continue; }
      if (!entry.isFile()) continue;
      const lower = entry.name.toLowerCase();
      if (lower.endsWith('.ioc')) {
        const content = await readSmall(file);
        const device = content.match(/^\s*Mcu\.(?:CPN|Name|UserName)\s*=\s*(STM32[^\r\n]*)/im);
        const chip = device?.[1].match(CHIP)?.[0];
        if (chip) add(directory, path.relative(root, file), chip.toUpperCase());
      } else if (lower.endsWith('.uvprojx')) {
        const content = await readSmall(file);
        const device = content.match(/<Device>\s*(STM32[^<]*)<\/Device>/i)?.[1].match(CHIP)?.[0];
        if (device) add(/^(?:mdk-arm|keil|mdk)$/i.test(path.basename(directory)) ? path.dirname(directory) : directory, path.relative(root, file), device.toUpperCase());
      } else if (lower === 'cmakelists.txt') {
        const content = (await readSmall(file)).replace(/#\[(=*)\[[\s\S]*?\]\1\]/g, '').replace(/#.*$/gm, '');
        const chip = content.match(CHIP)?.[0];
        if (chip || /(?:cmake[\\/]stm32cubemx|stm32[a-z0-9]+_(?:hal|ll)|startup[_-]stm32[a-z0-9]+)/i.test(content)) add(directory, path.relative(root, file), chip?.toUpperCase());
      } else if (/^startup[_-]stm32[a-z0-9]+\.(?:s|asm)$/i.test(lower)) {
        let candidate = directory;
        while (contains(root, candidate)) {
          if (await readSmall(path.join(candidate, 'CMakeLists.txt'))) { add(candidate, path.relative(root, file), entry.name.match(CHIP)?.[0]?.toUpperCase()); break; }
          if (candidate === root) break;
          candidate = path.dirname(candidate);
        }
      }
    }
  }
  return [...found.values()].sort((a, b) => a.root.localeCompare(b.root));
}

function nearestProject(projects, activeFile) {
  return activeFile ? projects.filter(project => contains(project.root, activeFile)).sort((a, b) => b.root.length - a.root.length)[0] : undefined;
}
module.exports = { discoverProjects, nearestProject, contains };
