const fs = require('fs'), path = require('path');
const stage = path.resolve('OptimizationReview/UnityValidation');
fs.mkdirSync(stage,{recursive:true});
for(const dir of ['Assets','Packages','ProjectSettings'])
    fs.cpSync(dir,path.join(stage,dir),{recursive:true});
console.log(stage);
console.log('Package resolution must succeed before running -executeMethod Game.EditorTools.OptimizationRegressionChecks.RunBatch.');
