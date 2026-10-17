'use strict';
const crypto = require('node:crypto');
const FEATURES = new Set(['build', 'flash', 'verify', 'writeClangd', 'writeDebug', 'startDebug', 'writeSync', 'probeCheck', 'analyzeFirmware', 'hardFault', 'rtos', 'connectSerial', 'sendSerial', 'exportReport']);
function ingestionHost(value) {
  const url = new URL(value || 'https://us.i.posthog.com');
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash || url.pathname !== '/' || !/\./.test(url.hostname)) throw new Error('PostHog 地址需要是不含路径或凭据的 HTTPS 域名。');
  return url.origin;
}
class UsageStatistics {
  constructor(context, allowed) { this.context = context; this.allowed = allowed; this.inflight = new Set(); }
  snapshot() { return { enabled: this.context.globalState.get('usage.enabled', false), configured: this.context.globalState.get('usage.configured', false), host: this.context.globalState.get('usage.host', 'https://us.i.posthog.com'), allowed: this.allowed() }; }
  async configure(host, key) {
    host = ingestionHost(host); if (typeof key !== 'string' || !/^phc_[A-Za-z0-9_-]{8,200}$/.test(key)) throw new Error('请填写 PostHog 项目令牌（phc_），不能使用个人 API 密钥。');
    await this.context.secrets.store('usage.projectToken', key); await this.context.globalState.update('usage.host', host); await this.context.globalState.update('usage.configured', true);
  }
  async enable(enabled) {
    if (enabled && !this.snapshot().configured) throw new Error('请先配置 PostHog 项目地址及令牌。');
    await this.context.globalState.update('usage.enabled', enabled);
    if (!enabled) { for (const controller of this.inflight) controller.abort(); await this.context.globalState.update('usage.anonymousId', undefined); }
  }
  async capture(feature, success, language, version) {
    if (!FEATURES.has(feature) || !this.snapshot().enabled || !this.allowed() || this.inflight.size >= 2) return;
    try {
      const key = await this.context.secrets.get('usage.projectToken'); if (!key || !this.snapshot().enabled || !this.allowed()) return;
      let id = this.context.globalState.get('usage.anonymousId'); if (!id) { id = crypto.randomUUID(); await this.context.globalState.update('usage.anonymousId', id); }
      if (!this.snapshot().enabled || !this.allowed()) return;
      const controller = new AbortController(); this.inflight.add(controller); const timer = setTimeout(() => controller.abort(), 4000);
      try {
        await fetch(ingestionHost(this.snapshot().host) + '/capture/', { method: 'POST', redirect: 'error', signal: controller.signal,
          headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ api_key: key, event: 'stm32_feature_result', distinct_id: id,
            properties: { distinct_id: id, feature, success: success === true, language: language === 'zh-CN' ? 'zh-CN' : 'en', version,
              platform: 'win32-x64', $geoip_disable: true, $process_person_profile: false }, timestamp: new Date().toISOString() }) });
      } finally { clearTimeout(timer); this.inflight.delete(controller); }
    } catch { /* Statistics never block development operations or show network errors. */ }
  }
  dispose() { for (const controller of this.inflight) controller.abort(); this.inflight.clear(); }
}
module.exports = { UsageStatistics, ingestionHost };
