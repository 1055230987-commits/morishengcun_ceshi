const fs = require('fs'), path = require('path'), assert = require('assert');
const root = 'OptimizationReview/lighting-contrast-backup-2026-10-12';
const lighting = 'Assets/_Project/World/Environment/DayNightLightingController.cs';
const post = 'Assets/_Project/World/Environment/DayNightPostProcessing.cs';
const editor = 'Assets/_Project/Editor/EnvironmentTools/DayNightSceneTools.cs';
const scene = 'Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity';
const files = [lighting, post, editor, scene];
const original = Object.fromEntries(files.map(p => [p, fs.readFileSync(p, 'utf8')]));
const output = {...original};
function replace(p, before, after) {
    assert.strictEqual(output[p].split(before).length, 2, 'Expected one match: ' + before);
    output[p] = output[p].replace(before, after);
}
for (const [name, oldValue, newValue] of [
    ['_dayAmbientIntensity', '0.30', '0.18'],
    ['_minAmbientIntensity', '0.08', '0.025'],
    ['_moonMaxIntensity', '0.18', '0.045'],
    ['_nightExposure', '0.5', '0.22']
]) {
    replace(lighting, name + ' = ' + oldValue + 'f;', name + ' = ' + newValue + 'f;');
    replace(scene, '  ' + name + ': ' + Number(oldValue) + '\n', '  ' + name + ': ' + Number(newValue) + '\n');
}
replace(post, '_nightPostExposure = -0.15f;', '_nightPostExposure = -0.4f;');
replace(scene, '  _nightPostExposure: -0.15\n', '  _nightPostExposure: -0.4\n');
replace(editor, '"月亮", true, 0.18f,', '"月亮", true, 0.045f,');
replace(editor, 'light.shadowStrength = 0.85f;', 'light.shadowStrength = 1f;');
const split = s => s.split(/(?=^--- !u!)/m);
const docId = s => s.match(/^--- !u!\d+ &(-?\d+)/)?.[1] || 'header';
let docs = split(output[scene]);
for (const lightId of ['1822174046', '676011038']) {
    const i = docs.findIndex(d => docId(d) === lightId);
    assert(i >= 0 && docs[i].includes('    m_Strength: 0.85\n'));
    docs[i] = docs[i].replace('    m_Strength: 0.85\n', '    m_Strength: 1\n');
}
output[scene] = docs.join('');
const oldDocs = split(original[scene]);
assert.deepStrictEqual(docs.map(docId), oldDocs.map(docId));
const allowed = ['1897647661', '1897647663', '1822174046', '676011038'];
const changed = docs.filter((d, i) => d !== oldDocs[i]).map(docId);
assert(changed.length === 4 && changed.every(id => allowed.includes(id)));
for (const p of files) {
    const backup = path.join(root, p);
    assert(!fs.existsSync(backup), 'Backup already exists: ' + backup);
}
for (const p of files) {
    const backup = path.join(root, p);
    fs.mkdirSync(path.dirname(backup), {recursive: true});
    fs.copyFileSync(p, backup);
    fs.writeFileSync(p, output[p]);
}
console.log(JSON.stringify({status: 'PASS', changedFiles: files, changedSceneDocuments: changed,
    sceneIdentityAndOrderPreserved: true, backup: root}, null, 2));
