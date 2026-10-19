const fs = require('fs'), assert = require('assert');
const scene = 'Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity';
const before = fs.readFileSync(scene, 'utf8');
const docs = before.split(/(?=^--- !u!)/m);
const id = d => d.match(/^--- !u!\d+ &(-?\d+)/)?.[1] || 'header';
const originalIds = docs.map(id);
let isolated = 0;
for (let i = 0; i < docs.length; i++) {
    const d = docs[i];
    if (!d.startsWith('--- !u!108') || !d.includes('m_Bits: 1073741824')) continue;
    // 旧预览灯先在磁盘上禁用；ExecuteAlways 的 Build 只恢复两盏有限范围点光。
    docs[i] = d.replace('  m_Enabled: 1', '  m_Enabled: 0')
        .replace('  m_Type: 1', '  m_Type: 2')
        .replace('  m_Range: 10', '  m_Range: 6')
        .replace('  m_RenderingLayerMask: 1', '  m_RenderingLayerMask: 2');
    isolated++;
}
assert.strictEqual(isolated, 35, 'Scene changed; re-audit preview lights before repair');
assert.deepStrictEqual(docs.map(id), originalIds);
const after = docs.join('');
fs.writeFileSync(scene, after);
console.log(JSON.stringify({isolatedLegacyPreviewLights: isolated, sceneDocumentIdsPreserved: true}, null, 2));
