const fs = require('fs');
const scenePath = 'Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity';
const sceneGuid = '42fdf746228006941b3093c67abfcbf6';
let scene = fs.readFileSync(scenePath, 'utf8').replace(/\r\n/g, '\n');
let blocks = scene.split(/(?=^--- !u!)/m);
const objects = new Map(), transforms = new Map(), parentByGo = new Map(), interactables = new Set();
const counts = new Map(), remap = [];
function match(s, re) { const m = s.match(re); return m ? m[1] : null; }
for (const block of blocks) {
    const header = block.match(/^--- !u!(\d+) &(-?\d+)/);
    if (!header) continue;
    const type = +header[1], id = header[2];
    const go = match(block, /m_GameObject: \{fileID: (-?\d+)\}/);
    objects.set(id, {type, go, block});
    if (type === 4) transforms.set(id, {go, parent: match(block, /m_Father: \{fileID: (-?\d+)\}/)});
    if (/m_EditorClassIdentifier: .*::Game.World.(InteractableTree|InteractableCrate|FloorTransition)\s/.test(block))
        interactables.add(go);
    const objectId = match(block, /^  _objectId: (.*)$/m);
    if (objectId !== null) counts.set(objectId, (counts.get(objectId) || 0) + 1);
}
for (const transform of transforms.values())
    parentByGo.set(transform.go, transforms.get(transform.parent)?.go);
const colliderGos = new Set();
for (const {type, go} of objects.values()) {
    if (![65, 135, 136, 64, 143, 146, 154].includes(type)) continue;
    let ancestor = go;
    while (ancestor) {
        if (interactables.has(ancestor)) { colliderGos.add(go); break; }
        ancestor = parentByGo.get(ancestor);
    }
}
blocks = blocks.map(block => {
    const header = block.match(/^--- !u!(\d+) &(-?\d+)/);
    if (!header) return block;
    const type = +header[1], id = header[2];
    const old = match(block, /^  _objectId: (.*)$/m);
    if (old !== null && (counts.get(old) > 1 || old.trim() === '')) {
        const original = old.startsWith('"') ? JSON.parse(old) : old;
        // 一次性迁移使用已保存的局部 fileID 作后缀，结果写回资产；运行时不生成 ID。
        const next = sceneGuid + '/' + original + '/instance-' + id;
        block = block.replace(/^  _objectId: .*$/m, '  _objectId: ' + JSON.stringify(next));
        remap.push({fileID:id, previous:original, current:next});
    }
    if (type === 1 && colliderGos.has(id))
        block = block.replace(/^  m_Layer: \d+$/m, '  m_Layer: 8');
    if (block.includes('::Game.Player.InteractionProbe'))
        block = block.replace(/(_interactableMask:\n    serializedVersion: 2\n    m_Bits:) \d+/, '$1 256');
    return block.replace(/_logPlayerTrace: 1/g, '_logPlayerTrace: 0');
});
fs.writeFileSync(scenePath, blocks.join('').replace(/\n/g, '\r\n'));
fs.writeFileSync('OptimizationReview/id-migration.json', JSON.stringify(remap,null,2));
let tags = fs.readFileSync('ProjectSettings/TagManager.asset','utf8').replace(/\r\n/g,'\n');
const start = tags.indexOf('  layers:\n'), end = tags.indexOf('  m_SortingLayers:', start);
const lines = tags.slice(start,end).split('\n');
if (lines[9] !== '  - ' && lines[9] !== '  - Interactable') throw Error('Layer 8 already used');
lines[9] = '  - Interactable';
tags = tags.slice(0,start) + lines.join('\n') + tags.slice(end);
fs.writeFileSync('ProjectSettings/TagManager.asset',tags.replace(/\n/g,'\r\n'));
console.log(JSON.stringify({changedIds:remap.length, interactables:interactables.size, colliderObjectsOnLayer8:colliderGos.size}));
