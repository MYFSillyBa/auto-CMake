import { spawnSync } from 'node:child_process';
import { mkdirSync, existsSync, readFileSync, copyFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const extensionRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(extensionRoot, '..', '..');
const output = path.join(repositoryRoot, 'artifacts', 'vsix');
const server = path.join(extensionRoot, 'server');
const metadata = JSON.parse(readFileSync(path.join(extensionRoot, 'package.json'), 'utf8'));
if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('本地打包需要 Windows x64。');
mkdirSync(server, { recursive: true });
mkdirSync(output, { recursive: true });

function run(executable, args) {
  const result = spawnSync(executable, args, { cwd: extensionRoot, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}

run('dotnet', ['publish', path.join(repositoryRoot, 'src', 'CMakeDapLink.ExtensionHost'),
  '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
  '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true',
  '-p:PublishTrimmed=true',
  '-p:DebugType=None', '-p:DebugSymbols=false', '-o', server]);
const executable = path.join(server, 'STM32ProjectHelper.ExtensionHost.exe');
if (!existsSync(executable)) throw new Error('扩展后台未生成。');
copyFileSync(path.join(repositoryRoot, 'LICENSE'), path.join(extensionRoot, 'LICENSE'));
const vsix = path.join(output, `${metadata.name}-${metadata.version}-win32-x64.vsix`);
run(process.execPath, [path.join(extensionRoot, 'node_modules', '@vscode', 'vsce', 'vsce'),
  'package', '--target', 'win32-x64', '--out', vsix, '--allow-missing-repository']);
console.log(`本地扩展已生成：${vsix}`);
