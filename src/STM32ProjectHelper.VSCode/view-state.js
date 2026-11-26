'use strict';
const bytes = value => typeof value === 'string' ? Buffer.byteLength(value, 'base64') : 0;
const changeSummary = change => change.kind ? { path: change.path, kind: change.kind, beforeSize: change.beforeSize, afterSize: change.afterSize } : ({ path: change.path, kind: change.before === null ? 'add' : change.after === null ? 'delete' : 'modify', beforeSize: bytes(change.before), afterSize: bytes(change.after) });
function previewSummary(preview) {
  if (!preview) return undefined;
  const { changes, toolFingerprint, repairPlan, ...metadata } = preview;
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
  if (page === 'overview') return { ...common, inspection: state.inspection ? { project: { chip: state.inspection.project?.chip } } : undefined,
    verification: resultSummary(state.verification), buildResult: resultSummary(state.buildResult), flashResult: resultSummary(state.flashResult) };
  if (page === 'configure') return { ...common, inspection: state.inspection };
  if (page === 'sources') return { ...common, inspection: state.inspection ? { targets: state.inspection.targets } : undefined, folder: state.folder, scan: state.scan, selectedFiles: state.selectedFiles };
  if (page === 'import') return { ...common, sourceFiles: state.sourceFiles, importPreview: state.importPreview };
  if (page === 'conversion') return { ...common, inspection: state.inspection ? { configurations: state.inspection.configurations, project: { configurePresets: state.inspection.project?.configurePresets } } : undefined, conversion: state.conversion, sync: state.sync };
  if (page === 'tools') return { ...common, inspection: state.inspection, toolInventory: state.toolInventory, repair: state.repair && { ...state.repair, output: undefined } };
  if (page === 'history') return { ...common, history: state.history ? { records: (state.history.records || []).map(record => ({ id: record.id, label: record.label, createdUtc: record.createdUtc, restoredUtc: record.restoredUtc, status: record.status, changes: (record.changes || []).map(changeSummary) })) } : undefined };
  if (page === 'development') return { ...common, buildResult: resultSummary(state.buildResult), verification: resultSummary(state.verification), probeCheck: resultSummary(state.probeCheck) };
  if (page === 'intelligence') return { ...common, inspection: state.inspection, compileDatabase: resultSummary(state.compileDatabase), intelligenceNotice: state.intelligenceNotice };
  if (page === 'analysis') return { ...common, buildResult: resultSummary(state.buildResult), verification: resultSummary(state.verification), memory: state.memory, advancedAnalysis: resultSummary(state.advancedAnalysis), hardFault: resultSummary(state.hardFault), rtos: resultSummary(state.rtos), report: state.report };
  if (page === 'devices') return { ...common, serial: state.serial };
  if (page === 'preferences') return { ...common, telemetry: state.telemetry };
  return common;
}
module.exports = { viewState, changeSummary, previewSummary };
