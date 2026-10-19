const fs = require('fs'), path = require('path'), crypto = require('crypto');
const out = 'OptimizationReview/validation';
fs.mkdirSync(out, {recursive:true});
function walk(dir) {
    return fs.readdirSync(dir,{withFileTypes:true}).flatMap(e =>
        e.isDirectory() ? walk(path.join(dir,e.name)) : [path.join(dir,e.name).replace(/\\/g,'/')]);
}
// Give new Unity source assets stable metadata before copying the project.
for (const file of walk('Assets/_Project').filter(p=>p.endsWith('.cs'))) {
    if (!fs.existsSync(file+'.meta')) {
        fs.writeFileSync(file+'.meta', 'fileFormatVersion: 2\nguid: '+crypto.randomBytes(16).toString('hex')+
            '\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n');
    }
}
const dir = 'Library/Bee/artifacts/1900b0aE.dag';
for (const name of ['Game.Runtime','Game.Editor']) {
    let rsp=fs.readFileSync(dir+'/'+name+'.rsp','utf8');
    rsp=rsp.split(/\r?\n/).filter(line=>{
        const source=line.match(/^"(.+\.cs)"$/);
        return !source || fs.existsSync(source[1]);
    }).join('\n');
    rsp=rsp.replace(/^-out:.*$/m, '-out:"'+out+'/'+name+'.dll"')
        .replace(/^-refout:.*$/m, '-refout:"'+out+'/'+name+'.ref.dll"');
    if(name==='Game.Editor') rsp=rsp.replaceAll(dir+'/Game.Runtime.ref.dll',out+'/Game.Runtime.ref.dll');
    for(const file of walk('Assets/_Project').filter(p=>p.endsWith('.cs') &&
        (name==='Game.Editor' ? p.includes('/Editor/') : !p.includes('/Editor/')))) {
        if(!rsp.includes('"'+file+'"')) rsp+='\n"'+file+'"\n';
    }
    fs.writeFileSync(out+'/'+name+'.rsp',rsp);
}
