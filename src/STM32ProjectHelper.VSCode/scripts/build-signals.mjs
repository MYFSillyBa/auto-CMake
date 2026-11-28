import { build } from 'esbuild';
import { copyFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const extension = fileURLToPath(new URL('..', import.meta.url));
const output = path.join(extension, 'signal-dock', 'dist');
await mkdir(output, { recursive: true });
await build({
  absWorkingDir: extension,
  entryPoints: { app: 'signal-dock/web/app.mjs' },
  outdir: output,
  bundle: true,
  format: 'iife',
  platform: 'browser',
  target: 'chrome128',
  minify: true,
  legalComments: 'linked'
});
await Promise.all([
  copyFile(path.join(extension, 'signal-dock/web/index.html'), path.join(output, 'index.html')),
  copyFile(path.join(extension, 'node_modules/three/LICENSE'), path.join(output, 'THREE-LICENSE.txt')),
  copyFile(path.join(extension, 'node_modules/html-to-image/LICENSE'), path.join(output, 'HTML-TO-IMAGE-LICENSE.txt'))
]);
