const fs = require('fs'), assert = require('assert');
const p = 'Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity';
const before = fs.readFileSync('OptimizationReview/lighting-root-cause-backup-2026-10-12/' + p, 'utf8');
const after = fs.readFileSync(p, 'utf8');
const split = s => s.split(/(?=^--- !u!)/m);
const id = d => d.match(/^--- !u!\d+ &(-?\d+)/)?.[1] || 'header';
const oldDocs = split(before), docs = split(after);
assert.deepStrictEqual(docs.map(id), oldDocs.map(id));
const changed = docs.filter((d, i) => d !== oldDocs[i]);
assert.strictEqual(changed.length, 35);
const transformByGo = new Map();
const goByTransform = new Map();
for (const d of docs.filter(d => /^--- !u!(4|224) /.test(d))) {
    const go = d.match(/m_GameObject: \{fileID: (\d+)\}/)[1];
    transformByGo.set(go, d); goByTransform.set(id(d), go);
}
const rigDoc = docs.find(d => d.includes('Game.Runtime::Game.Player.CharacterPreviewRig'));
const rigGo = rigDoc.match(/m_GameObject: \{fileID: (\d+)\}/)[1];
function isInPreview(go) {
    if (go === rigGo) return true;
    const t = transformByGo.get(go);
    const parent = t?.match(/m_Father: \{fileID: (\d+)\}/)?.[1];
    return parent && parent !== '0' ? isInPreview(goByTransform.get(parent)) : false;
}
for (const d of changed) {
    assert(d.startsWith('--- !u!108'), 'Changed non-light document');
    assert(isInPreview(d.match(/m_GameObject: \{fileID: (\d+)\}/)[1]), 'Changed non-preview light');
    assert(d.includes('  m_Enabled: 0\n') && d.includes('  m_Type: 2\n'));
    assert(d.includes('  m_Range: 6\n') && d.includes('  m_RenderingLayerMask: 2\n'));
}
const directions = docs.filter(d => d.startsWith('--- !u!108') && /^  m_Type: 1$/m.test(d));
assert.strictEqual(directions.length, 2, 'Only sun and moon should remain directional');
const ids = new Set(docs.map(id));
for (const m of after.matchAll(/\{fileID: (-?\d+)\}/g)) assert(m[1] === '0' || ids.has(m[1]));
const result = {status: 'PASS', sceneDocuments: ids.size - 1, isolatedLegacyPreviewLights: changed.length,
    remainingDirectionalLights: directions.length, unrelatedSceneDocumentsUnchanged: true,
    note: 'Static scene validation only. Native lifecycle result is recorded separately by LightingAuditTools.'};
fs.writeFileSync('OptimizationReview/lighting-isolation-validation.json', JSON.stringify(result, null, 2));
console.log(JSON.stringify(result, null, 2));
