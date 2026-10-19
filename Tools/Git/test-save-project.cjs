// Maintainer-only integration tests. All Git writes stay in ignored, newly created fixture repositories.
const fs = require('fs');
const path = require('path');
const cp = require('child_process');
const crypto = require('crypto');
const assert = require('assert/strict');
const root = path.resolve(__dirname, '../..');
const script = path.join(__dirname, 'Save-Project.ps1');
const testsRoot = path.join(root, 'OptimizationReview/git-workflow-tests', new Date().toISOString().replace(/[:.]/g, '-') + '-' + process.pid);
const dateFormat = new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit' });
const today = dateFormat.format(new Date()).replace(/-/g, '.');
const yesterday = dateFormat.format(new Date(Date.now() - 86400000)).replace(/-/g, '.');
function numberAtHead(cwd) { return git(cwd, 'tag', '--list', '--points-at', 'HEAD').split(/\r?\n/).find(tag => /^\d{4}\.\d{2}\.\d{2}\.\d{5}$/.test(tag)); }
fs.mkdirSync(testsRoot, { recursive: true });
let passed = 0;
function run(file, args, cwd, input) {
  return cp.spawnSync(file, args, { cwd, input, encoding: 'utf8', windowsHide: true,
    env: { ...process.env, GIT_TERMINAL_PROMPT: '0' }, timeout: 45000 });
}
function git(cwd, ...args) {
  const result = run('git', args, cwd);
  if (result.status !== 0) throw Error(args.join(' ') + '\n' + result.stderr);
  return result.stdout.trim();
}
function write(cwd, relative, text) {
  const destination = path.join(cwd, relative);
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  fs.writeFileSync(destination, text);
}
function fixture(name) {
  const cwd = path.join(testsRoot, name);
  fs.mkdirSync(cwd);
  for (const folder of ['Assets', 'Packages', 'ProjectSettings']) fs.mkdirSync(path.join(cwd, folder));
  write(cwd, 'ProjectSettings/ProjectVersion.txt', 'm_EditorVersion: 6000.3.10f1\n');
  write(cwd, '.gitignore', '/Library/\n/Temp/\n');
  git(cwd, 'init', '-q', '--initial-branch=main');
  git(cwd, 'config', 'user.name', 'Local validation');
  git(cwd, 'config', 'user.email', 'validation@example.invalid');
  git(cwd, 'add', '.');
  git(cwd, 'commit', '-qm', 'fixture baseline');
  return cwd;
}
function save(cwd, mode = 'Preview', extra = [], input) {
  const args = ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, '-ProjectPath', cwd, '-Mode', mode, ...extra];
  const result = run('powershell.exe', args, root, input);
  if (result.error) throw result.error;
  return result;
}
function check(ok, message, output = '') {
  if (!ok) throw Error(message + '\n' + output);
  passed++; console.log('PASS ' + message);
}
function staged(cwd) { return git(cwd, 'diff', '--cached', '--name-only'); }
function failedWithoutStaging(cwd, label) {
  const head = git(cwd, 'rev-parse', 'HEAD');
  const tags = git(cwd, 'tag', '--list');
  const result = save(cwd, 'Save', ['-Yes', '-Message', 'must not commit']);
  check(result.status !== 0 && git(cwd, 'rev-parse', 'HEAD') === head && !staged(cwd) && git(cwd, 'tag', '--list') === tags, label, result.stdout + result.stderr);
}
try {
  const basic = fixture('中文 空格路径');
  write(basic, 'notes.txt', 'example\n');
  write(basic, 'Library/cache.txt', 'generated\n');
  let head = git(basic, 'rev-parse', 'HEAD');
  let result = save(basic);
  check(result.status === 0 && !staged(basic) && git(basic, 'rev-parse', 'HEAD') === head && !result.stdout.includes('Library/cache.txt'),
    '预览不改提交和暂存区，生成目录不进入清单', result.stdout + result.stderr);
  result = save(basic, 'Interactive', [], '0\n');
  check(result.status === 0 && !staged(basic) && git(basic, 'rev-parse', 'HEAD') === head, '交互取消不暂存、不提交', result.stdout + result.stderr);
  result = save(basic, 'Save', ['-Message', 'cancel test'], 'N\n');
  check(result.status === 0 && !staged(basic) && git(basic, 'rev-parse', 'HEAD') === head, '最终确认取消不暂存', result.stdout + result.stderr);
  check(!git(basic, 'tag', '--list'), '预览和取消不消耗保存编号');
  const message = '修复背包 "拖动" $() ; \\ literal';
  result = save(basic, 'Save', ['-Yes', '-Message', message]);
  check(result.status === 0 && git(basic, 'show', '-s', '--format=%B') === '[' + today + '.00001] ' + message && !staged(basic), '日期编号加入标题，原始中文、引号和特殊字符保留', result.stdout + result.stderr);
  check(numberAtHead(basic) === today + '.00001' && result.stdout.includes('保存编号：' + today + '.00001'), '首次保存编号为北京时间日期加 00001，标签指向实际提交');
  head = git(basic, 'rev-parse', 'HEAD');
  result = save(basic, 'Save', ['-Yes']);
  check(result.status === 0 && git(basic, 'rev-parse', 'HEAD') === head, '没有新变化时不生成空提交', result.stdout + result.stderr);
  check(git(basic, 'tag', '--list') === today + '.00001', '无变化不生成新编号');
  write(basic, 'second.txt', 'second'); result = save(basic, 'Save', ['-Yes', '-Message', 'second']);
  check(result.status === 0 && numberAtHead(basic) === today + '.00002', '同一天第二次保存自动递增');
  git(basic, 'tag', '-d', today + '.00002');
  write(basic, 'third.txt', 'third'); result = save(basic, 'Save', ['-Yes', '-Message', 'third']);
  check(result.status === 0 && numberAtHead(basic) === today + '.00003', '误删编号标签后仍从提交标题判断已用序号');
  head = git(basic, 'rev-parse', 'HEAD');
  write(basic, 'pending.txt', 'pending\n');
  result = save(basic, 'SaveAndPush', ['-Yes', '-Message', 'no remote']);
  check(result.status !== 0 && git(basic, 'rev-parse', 'HEAD') === head && !staged(basic), '远程未配置时在暂存之前停止', result.stdout + result.stderr);
  git(basic, 'add', 'pending.txt');
  result = save(basic, 'Save', ['-Yes', '-Message', 'staged']);
  check(result.status !== 0 && staged(basic) === 'pending.txt' && git(basic, 'rev-parse', 'HEAD') === head, '已有人工暂存时保持原样并停止', result.stdout + result.stderr);

  const metadata = fixture('metadata');
  write(metadata, 'Assets/new.txt', 'asset');
  failedWithoutStaging(metadata, '缺少资源 .meta 时拒绝提交');
  write(metadata, 'Assets/new.txt.meta', 'fileFormatVersion: 2\nguid: ' + crypto.randomBytes(16).toString('hex') + '\n');
  result = save(metadata, 'Save', ['-Yes', '-Message', 'asset and meta']);
  check(result.status === 0 && git(metadata, 'ls-files', 'Assets/new.txt.meta') !== '', '资源与 .meta 配套提交', result.stdout + result.stderr);
  fs.unlinkSync(path.join(metadata, 'Assets/new.txt'));
  failedWithoutStaging(metadata, '删除资源却保留 .meta 时停止');
  fs.unlinkSync(path.join(metadata, 'Assets/new.txt.meta'));
  result = save(metadata, 'Save', ['-Yes', '-Message', 'delete asset and meta']);
  check(result.status === 0 && !git(metadata, 'ls-files', 'Assets'), '资源与 .meta 配套删除可正常提交', result.stdout + result.stderr);
  const orphan = fixture('orphan'); write(orphan, 'Assets/orphan.meta', 'guid: example');
  failedWithoutStaging(orphan, '孤立 .meta 被拒绝');
  const folder = fixture('folder'); write(folder, 'Assets/Props/test.txt', 'asset'); write(folder, 'Assets/Props/test.txt.meta', 'guid: example');
  failedWithoutStaging(folder, '缺少新文件夹 .meta 时停止');
  const secret = fixture('secret-name'); write(secret, '.env', 'EXAMPLE=not-a-real-secret');
  failedWithoutStaging(secret, '常见凭据文件名被拒绝');
  const token = fixture('token-pattern'); write(token, 'note.txt', 'ghp_' + 'a'.repeat(36));
  failedWithoutStaging(token, '普通文件里的令牌格式被拒绝');
  const detached = fixture('detached'); git(detached, 'checkout', '--detach', '-q'); write(detached, 'note.txt', 'pending');
  failedWithoutStaging(detached, '分离 HEAD 时停止');

  const newDay = fixture('new-day'); git(newDay, 'tag', yesterday + '.99999'); git(newDay, 'tag', today + '.00077.notes');
  write(newDay, 'note.txt', 'new day'); result = save(newDay, 'Save', ['-Yes', '-Message', 'new day']);
  check(result.status === 0 && numberAtHead(newDay) === today + '.00001', '前一天序号用满不影响新一天，不把非编号标签算入序号');
  const exhausted = fixture('exhausted'); git(exhausted, 'tag', today + '.99999'); write(exhausted, 'note.txt', 'pending');
  failedWithoutStaging(exhausted, '当天五位编号用满时在暂存前停止');
  const branches = fixture('branches'); git(branches, 'checkout', '-qb', 'side'); write(branches, 'side.txt', 'side');
  git(branches, 'add', '.'); git(branches, 'commit', '-qm', 'side'); git(branches, 'tag', today + '.00007'); git(branches, 'checkout', '-q', 'main');
  write(branches, 'main.txt', 'main'); result = save(branches, 'Save', ['-Yes', '-Message', 'main']);
  check(result.status === 0 && numberAtHead(branches) === today + '.00008', '仓库各分支共享当天编号，不产生重复标签');
  const branchRemote = path.join(testsRoot, 'branch-remote.git'); git(testsRoot, 'init', '--bare', '-q', branchRemote); git(branches, 'remote', 'add', 'origin', branchRemote);
  result = save(branches, 'SaveAndPush', ['-Yes']);
  check(result.status === 0 && !git(branchRemote, 'tag', '--list', today + '.00007') && git(branchRemote, 'tag', '--list', today + '.00008') !== '',
    '上传只带当前分支可达的编号，不上传其他分支的保存点', result.stdout + result.stderr);

  const network = fixture('local-push');
  const bare = path.join(testsRoot, 'remote.git'); git(testsRoot, 'init', '--bare', '-q', bare);
  git(network, 'remote', 'add', 'origin', bare); write(network, 'note.txt', 'first');
  result = save(network, 'SaveAndPush', ['-Yes', '-Message', 'push locally']);
  check(result.status === 0 && git(bare, 'rev-parse', 'refs/heads/main') === git(network, 'rev-parse', 'HEAD'), '向本地裸仓库正常推送当前分支', result.stdout + result.stderr);
  check(git(bare, 'rev-parse', 'refs/tags/' + today + '.00001') === git(network, 'rev-parse', 'HEAD'), '上传时分支与保存编号一起到达远程');
  head = git(network, 'rev-parse', 'HEAD'); result = save(network, 'SaveAndPush', ['-Yes']);
  check(result.status === 0 && git(network, 'rev-parse', 'HEAD') === head && git(network, 'tag', '--list') === today + '.00001', '无变化重复上传不新增提交或编号');
  const peer = path.join(testsRoot, 'peer'); git(testsRoot, 'clone', '-q', '--branch', 'main', bare, peer);
  git(peer, 'config', 'user.name', 'Local validation'); git(peer, 'config', 'user.email', 'validation@example.invalid');
  git(peer, 'tag', today + '.00008'); git(peer, 'push', '-q', 'origin', 'refs/tags/' + today + '.00008');
  write(network, 'remote-sequence.txt', 'remote sequence'); result = save(network, 'SaveAndPush', ['-Yes', '-Message', 'remote sequence']);
  check(result.status === 0 && numberAtHead(network) === today + '.00009', '保存并上传时参考远程当天序号，避开其他电脑已用的编号', result.stdout + result.stderr);
  git(peer, 'fetch', '-q', '--no-tags', 'origin'); git(peer, 'merge', '-q', '--ff-only', 'origin/main');
  write(peer, 'peer.txt', 'remote change'); git(peer, 'add', '.'); git(peer, 'commit', '-qm', 'peer change'); git(peer, 'push', '-q');
  const remoteHead = git(bare, 'rev-parse', 'refs/heads/main'); head = git(network, 'rev-parse', 'HEAD');
  write(network, 'local.txt', 'local change');
  result = save(network, 'SaveAndPush', ['-Yes', '-Message', 'retain local commit']);
  check(result.status !== 0 && git(network, 'rev-parse', 'HEAD') !== head && git(bare, 'rev-parse', 'refs/heads/main') === remoteHead && !staged(network),
    '远程拒绝时保留本地提交，不强推、不合并', result.stdout + result.stderr);
  check(numberAtHead(network) === today + '.00010' && !git(bare, 'tag', '--list', today + '.00010'), '推送失败保留本地编号，原子推送不单独上传失败保存的标签');

  const clash = fixture('tag-clash'), clashRemote = path.join(testsRoot, 'clash.git');
  git(testsRoot, 'init', '--bare', '-q', clashRemote); git(clash, 'remote', 'add', 'origin', clashRemote);
  git(clash, 'push', '-q', 'origin', 'HEAD:refs/heads/main');
  const originalRemote = git(clashRemote, 'rev-parse', 'refs/heads/main'); git(clashRemote, 'tag', today + '.00001', originalRemote);
  write(clash, 'offline.txt', 'offline'); result = save(clash, 'Save', ['-Yes', '-Message', 'offline']);
  check(result.status === 0 && numberAtHead(clash) === today + '.00001', '本地保存离线生成编号，不依赖远程服务');
  result = save(clash, 'SaveAndPush', ['-Yes']);
  check(result.status !== 0 && git(clashRemote, 'rev-parse', 'refs/heads/main') === originalRemote && git(clashRemote, 'rev-parse', 'refs/tags/' + today + '.00001') === originalRemote,
    '不同电脑出现同名编号冲突时整体停止，不覆盖远程编号或分支', result.stdout + result.stderr);
  console.log('RESULT passed=' + passed + ' failed=0');
  fs.writeFileSync(path.join(testsRoot, 'results.json'), JSON.stringify({ passed, failed: 0 }, null, 2));
} catch (error) {
  console.error(error.stack); process.exitCode = 1;
  fs.writeFileSync(path.join(testsRoot, 'results.json'), JSON.stringify({ passed, failed: 1, error: error.message }, null, 2));
}
console.log('保留的独立测试记录：' + testsRoot);
