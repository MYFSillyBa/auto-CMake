'use strict';
const bytes = value => typeof value === 'string' ? Buffer.byteLength(value, 'base64') : 0;
const changeSummary = change => change.kind ? { path: change.path, kind: change.kind, beforeSize: change.beforeSize, afterSize: change.afterSize } : ({ path: change.path, kind: change.before === null ? 'add' : change.after === null ? 'delete' : 'modify', beforeSize: bytes(change.before), afterSize: bytes(change.after) });
function previewSummary(preview) {
  if (!preview) return undefined;
  const { changes, toolFingerprint, ...metadata } = preview;
  return { ...metadata, changes: (changes || []).map(changeSummary) };
}
function resultSummary(result) {
  if (!result) return undefined;
  const { output, record, ...summary } = result;
  return { ...summary, record: record ? { id: record.id, label: record.label, createdUtc: record.createdUtc, changes: (record.changes || []).map(changeSummary) } : undefined };
}
function viewState(state, page) {
  const common = { options: state.options, status: state.status, percent: state.percent || 0, fixedConfiguration: state.fixedConfiguration,
    savedFilesNotice: state.savedFilesNotice, staleToolsNotice: state.staleToolsNotice, syncNotice: state.syncNotice, wizard: state.wizard, preview: previewSummary(state.preview),
    sourceRevision: state.sourceRevision || 0, selectionRevision: state.selectionRevision || 0 };
  if (page === 'configure') return { ...common, inspection: state.inspection, verification: resultSummary(state.verification), buildResult: resultSummary(state.buildResult), flashResult: resultSummary(state.flashResult), memory: state.memory };
  if (page === 'sources') return { ...common, inspection: state.inspection ? { targets: state.inspection.targets } : undefined, folder: state.folder, scan: state.scan, selectedFiles: state.selectedFiles };
  if (page === 'import') return { ...common, sourceFiles: state.sourceFiles, importPreview: state.importPreview };
  if (page === 'conversion') return { ...common, inspection: state.inspection ? { configurations: state.inspection.configurations, project: { configurePresets: state.inspection.project?.configurePresets } } : undefined, conversion: state.conversion };
  if (page === 'tools') return { ...common, inspection: state.inspection, toolInventory: state.toolInventory, repair: state.repair && { ...state.repair, output: undefined } };
  if (page === 'history') return { ...common, history: state.history ? { records: (state.history.records || []).map(record => ({ id: record.id, label: record.label, createdUtc: record.createdUtc, restoredUtc: record.restoredUtc, status: record.status, changes: (record.changes || []).map(changeSummary) })) } : undefined };
  if (page === 'development') return { ...common, inspection: state.inspection, buildResult: resultSummary(state.buildResult), verification: resultSummary(state.verification), sync: state.sync, probeCheck: resultSummary(state.probeCheck), compileDatabase: resultSummary(state.compileDatabase) };
  if (page === 'analysis') return { ...common, buildResult: resultSummary(state.buildResult), verification: resultSummary(state.verification), advancedAnalysis: resultSummary(state.advancedAnalysis), hardFault: resultSummary(state.hardFault), rtos: resultSummary(state.rtos), report: state.report };
  if (page === 'devices') return { ...common, serial: state.serial, telemetry: state.telemetry, report: state.report };
  return common;
}
module.exports = { viewState, changeSummary, previewSummary };
