'use strict';
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.STM32I18n = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, () => {
  const resolveLanguage = (preference, editorLanguage) => {
    if (preference === 'zh-CN' || preference === 'en') return preference;
    return /^zh(?:[-_]|$)/i.test(editorLanguage || '') ? 'zh-CN' : 'en';
  };
  const createTranslator = (catalog, language) => (source, ...args) => {
    const value = language === 'en' && Object.prototype.hasOwnProperty.call(catalog || {}, source) ? catalog[source] : source;
    return String(value ?? '').replace(/\{(\d+)\}/g, (match, index) => Number(index) < args.length ? String(args[Number(index)]) : match);
  };
  return Object.freeze({ resolveLanguage, createTranslator });
});
