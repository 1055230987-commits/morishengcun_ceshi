const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '../..');
const dryRun = process.argv.includes('--dry-run');
const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const destination = path.join(root, 'OptimizationReview', 'validation-projects', stamp + '-' + process.pid);
try {
  for (const folder of ['Assets', 'Packages', 'ProjectSettings']) {
    if (!fs.existsSync(path.join(root, folder))) throw Error('缺少 Unity 工程目录：' + folder);
  }
  console.log('隔离副本：' + destination);
  console.log('复制 Assets、Packages、ProjectSettings；不复制 Library、Git、日志或旧验证副本。');
  if (!dryRun) {
    fs.mkdirSync(path.dirname(destination), { recursive: true });
    fs.mkdirSync(destination); // Refuse overwrite; every run creates a new copy.
    for (const folder of ['Assets', 'Packages', 'ProjectSettings']) {
      fs.cpSync(path.join(root, folder), path.join(destination, folder), { recursive: true, errorOnExist: true, force: false });
    }
  }
  console.log(dryRun ? 'DRY RUN：未复制。' : '请用工程对应版本的 Unity 打开副本，等待包解析成功后运行编辑器回归菜单。');
} catch (error) { console.error(error.message); process.exitCode = 1; }
