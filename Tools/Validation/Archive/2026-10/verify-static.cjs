const fs = require('fs'), path=require('path');
function assert(ok,message){if(!ok)throw Error(message);}
const file='Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity';
const scene=fs.readFileSync(file,'utf8');
const ids=[...scene.matchAll(/^  _objectId: (.*)$/gm)].map(m=>m[1].trim());
assert(ids.length===298,'Unexpected WorldObjectId count');
assert(ids.every(x=>x.length>0) && new Set(ids).size===ids.length,'Empty/duplicate ID');
const documents=[...scene.matchAll(/^--- !u!\d+ &(-?\d+)/gm)].map(m=>m[1]);
assert(new Set(documents).size===documents.length,'Duplicate YAML document fileID');
let missing=[];
for(const match of scene.matchAll(/\{fileID: (-?\d+)\}/g)){
    if(match[1]!=='0'&&!documents.includes(match[1])) missing.push(match[1]);
}
assert(missing.length===0,'Dangling internal references: '+missing.slice(0,5));
assert(!scene.includes('_logPlayerTrace: 1'),'Trace logging remains enabled');
assert(/_interactableMask:\r?\n    serializedVersion: 2\r?\n    m_Bits: 256/.test(scene),'Incorrect interaction mask');
const build=fs.readFileSync('ProjectSettings/EditorBuildSettings.asset','utf8');
assert(build.includes('path: '+file),'Wrong startup scene');
assert(build.includes('guid: 42fdf746228006941b3093c67abfcbf6'),'Wrong startup GUID');
const tags=fs.readFileSync('ProjectSettings/TagManager.asset','utf8');
assert(tags.split('  layers:')[1].split('  m_SortingLayers:')[0].trimEnd().split(/\r?\n/)[9]==='  - Interactable','Layer 8 not named');
const original=fs.readFileSync('OptimizationReview/backup-2026-10-10/'+file,'utf8');
const oldDocs=[...original.matchAll(/^--- !u!\d+ &(-?\d+)/gm)].map(m=>m[1]);
assert(JSON.stringify(documents)===JSON.stringify(oldDocs),'Scene object identity/order changed');
const versions=fs.readFileSync('Packages/manifest.json','utf8');
assert(versions===fs.readFileSync('OptimizationReview/backup-2026-10-10/Packages/manifest.json','utf8'),'Main project dependencies changed');
let changes=[];
function walk(dir){for(const entry of fs.readdirSync(dir,{withFileTypes:true})){
    const p=path.join(dir,entry.name);
    if(entry.isDirectory())walk(p);
    else{
        const old=path.join('OptimizationReview/backup-2026-10-10',p);
        if(!fs.existsSync(old)) changes.push({path:p,status:'added'});
        else if(!fs.readFileSync(p).equals(fs.readFileSync(old)))changes.push({path:p,status:'modified'});
    }
}}
for(const dir of ['Assets','ProjectSettings','Packages'])walk(dir);
function deleted(dir){
    for(const e of fs.readdirSync(dir,{withFileTypes:true})){
        const p=path.join(dir,e.name);
        if(e.isDirectory())deleted(p);
        else {
            const current=path.relative('OptimizationReview/backup-2026-10-10',p);
            if(!fs.existsSync(current))changes.push({path:current,status:'deleted'});
        }
    }
}
for(const dir of ['Assets','ProjectSettings','Packages'])
    deleted(path.join('OptimizationReview/backup-2026-10-10',dir));
fs.writeFileSync('OptimizationReview/changed-files.json',JSON.stringify(changes,null,2));
console.log(JSON.stringify({status:'PASS',uniqueIds:ids.length,sceneDocuments:documents.length,danglingReferences:missing.length,changedFiles:changes.length},null,2));
