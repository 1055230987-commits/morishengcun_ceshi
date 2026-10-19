// Optional local diagnostic compiler setup; Unity's normal import/build remains authoritative.
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '../..');
const args = process.argv.slice(2);
const dryRun = args.includes('--dry-run');
const artifactOption = args.indexOf('--artifacts');
const artifacts = artifactOption >= 0 ? path.resolve(root, args[artifactOption + 1]) : null;
const output = path.join(root, 'OptimizationReview/validation');
function walk(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}
function latestRsp(assembly) {
  if (artifacts) {
    const file = path.join(artifacts, assembly + '.rsp');
    if (!fs.existsSync(file)) throw Error('Missing response file: ' + file);
    return file;
  }
  const matches = walk(path.join(root, 'Library/Bee/artifacts'))
    .filter(file => path.basename(file) === assembly + '.rsp')
    .sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
  if (!matches.length) throw Error('先用对应版本 Unity 导入并编译项目，再生成本机编译参数：' + assembly);
  return matches[0];
}
try {
  const project = path.join(root, 'Assets/_Project');
  const sources = walk(project).filter(file => file.endsWith('.cs'));
  const missingMetadata = sources.filter(file => !fs.existsSync(file + '.meta'));
  if (missingMetadata.length) throw Error('请先让 Unity 生成 .meta；工具不会自动生成 GUID：\n' + missingMetadata.join('\n'));
  const plans = ['Game.Runtime', 'Game.Editor'].map(assembly => ({ assembly, rsp: latestRsp(assembly) }));
  for (const plan of plans) {
    let content = fs.readFileSync(plan.rsp, 'utf8');
    content = content.split(/\r?\n/).filter(line => {
      const match = line.match(/^"(.+\.cs)"$/);
      return !match || fs.existsSync(path.resolve(root, match[1]));
    }).join('\n');
    const destination = 'OptimizationReview/validation/' + plan.assembly;
    content = content.replace(/^-out:.*$/m, '-out:"' + destination + '.dll"')
      .replace(/^-refout:.*$/m, '-refout:"' + destination + '.ref.dll"');
    if (plan.assembly === 'Game.Editor') {
      content = content.replace(/^-r:"[^"\r\n]*Game\.Runtime\.ref\.dll"$/gm,
        '-r:"OptimizationReview/validation/Game.Runtime.ref.dll"');
      // Cached compiler arguments may predate the editor assembly's Input System/NavMesh references.
      for (const dependency of ['Unity.InputSystem', 'Unity.AI.Navigation']) {
        if (!content.includes('/' + dependency + '.dll"')) {
          const dll = 'Library/ScriptAssemblies/' + dependency + '.dll';
          if (!fs.existsSync(path.join(root, dll))) throw Error('请先完成 Unity 导入，缺少：' + dll);
          content += '\n-r:"' + dll + '"\n';
        }
      }
    }
    for (const full of sources) {
      const relative = path.relative(root, full).replace(/\\/g, '/');
      const editor = relative.includes('/Editor/');
      if (editor !== (plan.assembly === 'Game.Editor')) continue;
      if (!content.includes('"' + relative + '"')) content += '\n"' + relative + '"\n';
    }
    console.log(plan.assembly + ': ' + path.relative(root, plan.rsp) + ' -> ' + destination + '.rsp');
    if (!dryRun) {
      fs.mkdirSync(output, { recursive: true });
      fs.writeFileSync(path.join(output, plan.assembly + '.rsp'), content);
    }
  }
  console.log(dryRun ? 'DRY RUN：未写文件。' : '已生成本机参数；请从工程根目录依次编译 Runtime、Editor。这不是完整打包。');
} catch (error) { console.error(error.message); process.exitCode = 1; }
