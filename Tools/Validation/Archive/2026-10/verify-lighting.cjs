const fs = require('fs');
const assert = require('assert');
const scenePath = 'Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity';
const before = fs.readFileSync('OptimizationReview/lighting-backup-2026-10-12/' + scenePath, 'utf8');
const after = fs.readFileSync(scenePath, 'utf8');
const docs = s => s.split(/(?=^--- !u!)/m);
const id = d => d.match(/^--- !u!\d+ &(-?\d+)/)?.[1] || 'header';
const oldDocs = docs(before), newDocs = docs(after);
assert.deepStrictEqual(newDocs.map(id), oldDocs.map(id), 'Scene identity/order changed');
const changed = newDocs.filter((d, i) => d !== oldDocs[i]).map(id);
const allowed = ['1897647661', '1897647663', '676011038', '1822174046'];
assert(changed.every(x => allowed.includes(x)), 'Unexpected changed document: ' + changed);
const worldIds = s => [...s.matchAll(/^  _objectId: (.*)$/gm)].map(x => x[1]);
assert.deepStrictEqual(worldIds(after), worldIds(before), 'World IDs changed');
const ids = new Set(newDocs.map(id));
assert.strictEqual(ids.size, newDocs.length, 'Duplicate scene fileID');
for (const m of after.matchAll(/\{fileID: (-?\d+)\}/g))
    assert(m[1] === '0' || ids.has(m[1]), 'Dangling scene reference: ' + m[1]);
const profile = fs.readFileSync('Assets/_Project/Rendering/VolumeProfiles/DA_DayNight_Profile.asset', 'utf8');
const refs = [...profile.matchAll(/^  - \{fileID: (\d+)\}/gm)].map(x => x[1]);
assert.strictEqual(refs.length, 4);
const subIds = docs(profile).map(id);
for (const ref of refs) assert(ref !== '0' && subIds.includes(ref), 'Missing volume subasset');
for (const name of ['Tonemapping', 'ColorAdjustments', 'Bloom', 'Vignette']) {
    const meta = fs.readFileSync('Library/PackageCache/com.unity.render-pipelines.universal@37e0d4fc2503/Runtime/Overrides/' + name + '.cs.meta', 'utf8');
    assert(profile.includes(meta.match(/guid: (\w+)/)[1]), 'Wrong URP script reference: ' + name);
}
const result = {status: 'PASS', sceneDocuments: ids.size - 1, changedSceneDocuments: changed,
    worldObjectIdsPreserved: worldIds(after).length, profileSubassets: refs.length,
    scope: 'Compared with pre-edit 2026-10-12 backup. Historical 298-object test is outdated.'};
fs.writeFileSync('OptimizationReview/lighting-validation.json', JSON.stringify(result, null, 2));
console.log(JSON.stringify(result, null, 2));
