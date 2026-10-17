'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { resolveLanguage, createTranslator } = require('../media/i18n.js');
const ui = require('../media/ui-messages.js');
const controller = require('../locales/controller.json');
const { request } = require('./host-client.cjs');
const { createProject } = require('./fixtures.cjs');
const extensionRoot = path.resolve(__dirname, '..');
const placeholders = text => [...text.matchAll(/\{\d+\}/g)].map(match => match[0]).sort();

test('language resolution and catalog formatting preserve user arguments', () => {
  for (const language of ['zh', 'zh-CN', 'zh-tw', 'zh_HK']) assert.equal(resolveLanguage('auto', language), 'zh-CN');
  for (const language of ['en', 'en-GB', 'de', 'ja']) assert.equal(resolveLanguage('auto', language), 'en');
  assert.equal(resolveLanguage('en', 'zh-CN'), 'en');
  assert.equal(resolveLanguage('zh-CN', 'en'), 'zh-CN');
  const chinesePath = 'D:\\工程\\编译\\main.c';
  assert.equal(createTranslator({ '文件 {0}': 'File {0}' }, 'en')('文件 {0}', chinesePath), 'File ' + chinesePath);
  for (const catalog of [ui, controller]) for (const [source, english] of Object.entries(catalog)) {
    assert.ok(english.trim(), source);
    assert.deepEqual(placeholders(source), placeholders(english), source);
  }
});

test('manifest and all native/webview captions have bilingual resources', () => {
  const manifests = ['package.nls.json', 'package.nls.zh-cn.json', 'package.nls.zh.json'].map(file => JSON.parse(fs.readFileSync(path.join(extensionRoot, file), 'utf8')));
  for (const catalog of manifests.slice(1)) assert.deepEqual(Object.keys(catalog).sort(), Object.keys(manifests[0]).sort());
  const metadata = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.json'), 'utf8'));
  for (const match of JSON.stringify(metadata).matchAll(/%([^%]+)%/g)) for (const catalog of manifests) assert.ok(catalog[match[1]], match[1]);
  assert.equal(metadata.contributes.configuration.properties['stm32ProjectHelper.language'].scope, 'application');
  for (const [file, catalog] of [['extension.js', controller], ['bridge.js', controller], ['development-tools.js', controller], ['media/workbench.js', ui]]) {
    const code = fs.readFileSync(path.join(extensionRoot, file), 'utf8');
    for (const match of code.matchAll(/\bt\('((?:\\.|[^'\\])*)'/g)) {
      const source = match[1].replace(/\\'/g, "'").replace(/\\n/g, '\n');
      if (/[\u3400-\u9fff]/.test(source)) assert.ok(Object.hasOwn(catalog, source), `${file}: ${source}`);
    }
  }
  const entries = fs.readFileSync(path.resolve(extensionRoot, '..', 'CMakeDapLink.ExtensionHost', 'DisplayMessages.tsv'), 'utf8').split(/\r?\n/).filter(line => line && !line.startsWith('#'));
  for (const line of entries) {
    const [source, english, helperArguments = ''] = line.split('\t');
    assert.ok(english?.trim(), source);
    assert.deepEqual([...new Set(placeholders(source))], [...new Set(placeholders(english))], source);
    for (const index of helperArguments.split(',').filter(Boolean)) assert.ok(source.includes(`{${index}}`), source);
  }
});

test('English tasks, Chinese history, locale replacement and raw build output work together', { timeout: 180000 }, async () => {
  const temporary = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'stm32-bilingual-flow-')));
  const root = path.join(temporary, '软件工程');
  const options = createProject(root);
  const taskFile = path.join(root, '.vscode', 'tasks.json');
  const original = fs.readFileSync(taskFile);
  fs.appendFileSync(path.join(root, 'CMakeLists.txt'), '\nadd_custom_target(raw_output ALL COMMAND ${CMAKE_COMMAND} -E echo "编译")\n');
  try {
    const events = [];
    const english = await request(root, 'previewTasks', { ...options, language: 'en' }, events);
    assert.equal(english.label, 'Configure VS Code build and flash tasks');
    assert.equal(english.canonicalLabel, '配置 VS Code 编译与烧录任务');
    assert.ok(events.some(event => event.type === 'progress' && event.data.stage === 'Operation complete'));
    assert.deepEqual(fs.readFileSync(taskFile), original);
    // This also accepts localized captions from older plugin clients.
    const saved = await request(root, 'applyChanges', { changes: english.changes, label: english.label, language: 'en' });
    const history = await request(root, 'history', { language: 'zh-CN' });
    assert.equal(history.records.find(record => record.id === saved.record.id).label, english.canonicalLabel);
    const enHistory = await request(root, 'history', { language: 'en' });
    assert.equal(enHistory.records.find(record => record.id === saved.record.id).label, english.label);
    const restore = await request(root, 'previewRestore', { id: saved.record.id, language: 'en' });
    assert.equal(restore.label, 'Restore · ' + english.label);
    const chinese = await request(root, 'previewTasks', { ...options, language: 'zh-CN' });
    await request(root, 'applyChanges', { changes: chinese.changes, label: chinese.canonicalLabel });
    const text = fs.readFileSync(taskFile, 'utf8');
    assert.ok(text.includes('// User task remains unchanged.'));
    assert.equal((text.match(/"label"/g) || []).length, 4);
    assert.ok(text.includes('一键编译') && text.includes('一键烧录(DAPLINK)'));
    assert.ok(!text.includes('One-click Build'));
    const result = await request(root, 'verify', { ...options, language: 'en', checkOpenOcd: false });
    assert.equal(result.success, true, result.output);
    assert.ok(result.firmware[0].startsWith(root + path.sep));
    assert.match(result.output, /CMake configure/);
    assert.match(result.output, /(?:^|\r?\n)编译(?:\r?\n|$)/, 'External output stays verbatim, even if it matches a helper caption.');
  } finally {
    const resolved = path.resolve(temporary);
    assert.ok(resolved.startsWith(fs.realpathSync.native(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('stm32-bilingual-flow-'));
    fs.rmSync(resolved, { recursive: true, force: true });
  }
});
