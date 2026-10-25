'use strict';
const crypto = require('node:crypto');
const publisherProject = require('./publisher-telemetry.json');
const FEATURES = new Set(['build', 'flash', 'verify', 'writeClangd', 'writeDebug', 'startDebug', 'writeSync', 'probeCheck', 'analyzeFirmware', 'hardFault', 'rtos', 'connectSerial', 'sendSerial', 'exportReport']);
function ingestionHost(value) {
  const url = new URL(value || 'https://us.i.posthog.com');
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash || url.pathname !== '/' || !/\./.test(url.hostname)) throw new Error('PostHog 地址需要是不含路径或凭据的 HTTPS 域名。');
  return url.origin;
}
class UsageStatistics {
  constructor(context, allowed, project = publisherProject) {
    this.context = context; this.allowed = allowed; this.inflight = new Set();
    try {
      if (/^phc_[A-Za-z0-9_-]{8,200}$/.test(project.projectToken || '')) {
        this.project = { host: ingestionHost(project.host), token: project.projectToken };
        this.projectId = crypto.createHash('sha256').update(this.project.host + '\n' + this.project.token).digest('hex');
      }
    } catch { /* Invalid developer build configuration disables collection. */ }
    // Consent for a user's own project never authorizes sending to the publisher.
    const cleanup = this.context.secrets?.delete?.('usage.projectToken');
    cleanup?.catch(() => {});
  }
  snapshot() { return { enabled: !!this.project && this.context.globalState.get('usage.publisherConsent') === this.projectId,
    configured: !!this.project, host: this.project?.host, recipient: 'developer', allowed: this.allowed() }; }
  async promptOnce(ask) {
    if (this.disposed || !this.project || !this.allowed() || this.snapshot().enabled || this.context.globalState.get('usage.publisherPrompt') === this.projectId) return;
    if (!this.startupPrompt) this.startupPrompt = (async () => {
      const accepted = await ask();
      if (!this.disposed) await this.enable(accepted === true && this.allowed());
    })();
    return this.startupPrompt;
  }
  async enable(enabled) {
    if (enabled && !this.project) throw new Error('此版本尚未配置开发者的统计项目，暂时不能启用。');
    if (this.projectId) await this.context.globalState.update('usage.publisherPrompt', this.projectId);
    await this.context.globalState.update('usage.publisherConsent', enabled ? this.projectId : undefined);
    if (!enabled) { for (const controller of this.inflight) controller.abort(); this.identity = undefined; if (this.projectId) await this.context.globalState.update('usage.publisherId.' + this.projectId, undefined); }
  }
  async capture(feature, success, language, version) {
    if (!FEATURES.has(feature) || !this.snapshot().enabled || !this.allowed() || this.inflight.size >= 2) return;
    try {
      const identityKey = 'usage.publisherId.' + this.projectId;
      const id = this.identity ||= this.context.globalState.get(identityKey) || crypto.randomUUID();
      await this.context.globalState.update(identityKey, id);
      if (!this.snapshot().enabled || !this.allowed()) { await this.context.globalState.update(identityKey, undefined); return; }
      const controller = new AbortController(); this.inflight.add(controller); const timer = setTimeout(() => controller.abort(), 4000);
      try {
        await fetch(this.project.host + '/capture/', { method: 'POST', redirect: 'error', signal: controller.signal,
          headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ api_key: this.project.token, event: 'stm32_feature_result', distinct_id: id,
            properties: { distinct_id: id, feature, success: success === true, language: language === 'zh-CN' ? 'zh-CN' : 'en', version,
              platform: 'win32-x64', $geoip_disable: true, $process_person_profile: false }, timestamp: new Date().toISOString() }) });
      } finally { clearTimeout(timer); this.inflight.delete(controller); }
    } catch { /* Statistics never block development operations or show network errors. */ }
  }
  dispose() { this.disposed = true; for (const controller of this.inflight) controller.abort(); this.inflight.clear(); }
}
module.exports = { UsageStatistics, ingestionHost };
