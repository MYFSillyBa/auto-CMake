'use strict';
function redactReport(text, root = '', userProfile = '') {
  let result = String(text || '').slice(-2 * 1024 * 1024);
  for (const [value, replacement] of [[root, '<PROJECT>'], [userProfile, '<USER>']]) {
    if (!value) continue;
    for (const form of new Set([value, value.replace(/\\/g, '/'), value.replace(/\\/g, '\\\\')])) result = result.split(form).join(replacement);
  }
  return result
    .replace(/\b(?:[A-Za-z]:[\\/]|\\\\)[^\r\n"<>|]*/g, '<LOCAL_PATH>')
    .replace(/\b(?:Bearer\s+|(?:token|password|passwd|secret|api[_-]?key|authorization)\s*[=:]\s*)[^\s,;"}]+/gi, '<REDACTED>')
    .replace(/\b(?:phc_|phx_)[A-Za-z0-9_-]+/g, '<TOKEN>')
    .replace(/\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/gi, '<EMAIL>')
    .replace(/\b(?:\d{1,3}\.){3}\d{1,3}\b/g, '<IP>')
    .replace(/(serial\s*(?:number|no\.?|id)?\s*[:=]\s*)\S+/gi, '$1<DEVICE>');
}
function createFaultReport(snapshot, output, root, userProfile) {
  const zh = snapshot.language === 'zh-CN';
  const safe = { version: snapshot.version, language: snapshot.language, platform: 'Windows x64',
    configuration: snapshot.configuration, probe: snapshot.probe, toolVersions: snapshot.toolVersions,
    buildSuccess: snapshot.buildSuccess, flashSuccess: snapshot.flashSuccess, problems: snapshot.problems || [] };
  return redactReport((zh ? '# STM32 工程助手 — 故障报告\n\n' : '# STM32 Project Helper — Diagnostic Report\n\n') + new Date().toISOString() + '\n\n' +
    (zh ? '分享前请检查脱敏结果。此报告仅在本地生成，不会自动上传。\n\n## 环境与结果\n\n```json\n' : 'Review redaction before sharing. This report is generated locally and is not uploaded.\n\n## Environment and results\n\n```json\n') + JSON.stringify(safe, null, 2) +
    (zh ? '\n```\n\n## 工具输出（最近 2 MiB）\n\n```text\n' : '\n```\n\n## Tool output (last 2 MiB)\n\n```text\n') + String(output || '').slice(-2 * 1024 * 1024).replace(/```/g, "''' ") + '\n```\n', root, userProfile);
}
module.exports = { redactReport, createFaultReport };
