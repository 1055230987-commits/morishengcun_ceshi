// Maintainer-only integration tests. All Git writes stay in ignored, newly created fixture repositories.
const fs = require('fs');
const path = require('path');
const cp = require('child_process');
const crypto = require('crypto');
const assert = require('assert/strict');
const root = path.resolve(__dirname, '../..');
const script = path.join(__dirname, 'Save-Project.ps1');
const testsRoot = path.join(root, 'OptimizationReview/git-workflow-tests', new Date().toISOString().replace(/[:.]/g, '-') + '-' + process.pid);
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
  const result = save(cwd, 'Save', ['-Yes', '-Message', 'must not commit']);
  check(result.status !== 0 && git(cwd, 'rev-parse', 'HEAD') === head && !staged(cwd), label, result.stdout + result.stderr);
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
  const message = '修复背包 "拖动" $() ; \\ literal';
  result = save(basic, 'Save', ['-Yes', '-Message', message]);
  check(result.status === 0 && git(basic, 'show', '-s', '--format=%B') === message && !staged(basic), '中文、引号和特殊字符作为提交文字原样保存', result.stdout + result.stderr);
  head = git(basic, 'rev-parse', 'HEAD');
  result = save(basic, 'Save', ['-Yes']);
  check(result.status === 0 && git(basic, 'rev-parse', 'HEAD') === head, '没有新变化时不生成空提交', result.stdout + result.stderr);
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

  const network = fixture('local-push');
  const bare = path.join(testsRoot, 'remote.git'); git(testsRoot, 'init', '--bare', '-q', bare);
  git(network, 'remote', 'add', 'origin', bare); write(network, 'note.txt', 'first');
  result = save(network, 'SaveAndPush', ['-Yes', '-Message', 'push locally']);
  check(result.status === 0 && git(bare, 'rev-parse', 'refs/heads/main') === git(network, 'rev-parse', 'HEAD'), '向本地裸仓库正常推送当前分支', result.stdout + result.stderr);
  const peer = path.join(testsRoot, 'peer'); git(testsRoot, 'clone', '-q', '--branch', 'main', bare, peer);
  git(peer, 'config', 'user.name', 'Local validation'); git(peer, 'config', 'user.email', 'validation@example.invalid');
  write(peer, 'peer.txt', 'remote change'); git(peer, 'add', '.'); git(peer, 'commit', '-qm', 'peer change'); git(peer, 'push', '-q');
  const remoteHead = git(bare, 'rev-parse', 'refs/heads/main'); head = git(network, 'rev-parse', 'HEAD');
  write(network, 'local.txt', 'local change');
  result = save(network, 'SaveAndPush', ['-Yes', '-Message', 'retain local commit']);
  check(result.status !== 0 && git(network, 'rev-parse', 'HEAD') !== head && git(bare, 'rev-parse', 'refs/heads/main') === remoteHead && !staged(network),
    '远程拒绝时保留本地提交，不强推、不合并', result.stdout + result.stderr);
  console.log('RESULT passed=' + passed + ' failed=0');
  fs.writeFileSync(path.join(testsRoot, 'results.json'), JSON.stringify({ passed, failed: 0 }, null, 2));
} catch (error) {
  console.error(error.stack); process.exitCode = 1;
  fs.writeFileSync(path.join(testsRoot, 'results.json'), JSON.stringify({ passed, failed: 1, error: error.message }, null, 2));
}
console.log('保留的独立测试记录：' + testsRoot);
