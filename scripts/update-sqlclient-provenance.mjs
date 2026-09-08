// Regenerate only derived SqlClient source inventories and patches from the pinned archive.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {execFileSync, spawnSync} from 'node:child_process';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const vendor = path.join(repository, 'vendor/sqlclient');
const archive = process.argv[2];
const commit = 'b16dec0a5622fd5b3d5311191bac4cafadc43e60';
const expected = 'f4c2cfd1a7a48f5e4f0b6b97639e5c17730fca1eade31668732280b82df21870';
const excludedDirectories = new Set(['bin','obj','tests/bin','tests/obj',
  'upstream/src/Microsoft.Data.SqlClient/netcore/src/bin',
  'upstream/src/Microsoft.Data.SqlClient/netcore/src/obj']);
const lockNames = ['packages.lock.json',
  ...['linux-x64','linux-arm64','osx-x64','osx-arm64','win-x64'].map(rid => 'packages.'+rid+'.lock.json')];
const excludedFiles = new Set(['SOURCE-SNAPSHOT.sha256',
  ...['tests','upstream/src/Microsoft.Data.SqlClient/netcore/src']
    .flatMap(root => lockNames.map(name => root+'/'+name))]);
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
if (!archive || hash(fs.readFileSync(archive)) !== expected) throw new Error('Pinned source archive checksum mismatch.');
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'ghostshell-sqlclient-provenance-'));
const files = directory => fs.readdirSync(directory, {withFileTypes:true}).flatMap(entry => {
  const full = path.join(directory, entry.name);
  if (entry.isSymbolicLink()) throw new Error('Source inventory refuses symbolic links.');
  if (entry.isDirectory() && excludedDirectories.has(path.relative(vendor,full).split(path.sep).join('/'))) return [];
  return entry.isDirectory() ? files(full) : [full];
}).sort();
try {
  execFileSync('/usr/bin/tar', ['-xzf', path.resolve(archive), '-C', scratch]);
  const upstream = path.join(scratch, 'SqlClient-'+commit);
  const originalLines = [];
  const patches = {production:'', tests:''};
  for (const [directory, upstreamPrefix, group] of [
    ['upstream', '', 'production'],
    ['tests/upstream', 'src/Microsoft.Data.SqlClient/tests/tools/TDS', 'tests'],
  ]) {
    for (const current of files(path.join(vendor, directory))) {
      if (excludedFiles.has(path.relative(vendor,current).split(path.sep).join('/'))) continue;
      const relative = path.relative(path.join(vendor, directory), current);
      const original = path.join(upstream, upstreamPrefix, relative);
      const label = path.relative(vendor, current).split(path.sep).join('/');
      const present = fs.existsSync(original);
      if (present) originalLines.push(hash(fs.readFileSync(original))+'  '+label);
      if (present && fs.readFileSync(original).equals(fs.readFileSync(current))) continue;
      const result = spawnSync('/usr/bin/diff', ['-u', '--label', present ? 'a/'+label : '/dev/null',
        '--label', 'b/'+label, present ? original : '/dev/null', current], {encoding:'utf8', maxBuffer:8*1024*1024});
      if (result.status !== 0 && result.status !== 1) throw new Error('Source diff failed.');
      patches[group] += result.stdout;
    }
  }
  fs.writeFileSync(path.join(vendor,'UPSTREAM-SOURCE.sha256'), originalLines.sort().join('\n')+'\n');
  fs.writeFileSync(path.join(vendor,'routed-transport.patch'), patches.production);
  fs.writeFileSync(path.join(vendor,'tests/header-fragmentation.patch'), patches.tests);
  const inventory = files(vendor).filter(file => !excludedFiles.has(path.relative(vendor,file).split(path.sep).join('/')))
    .map(file => hash(fs.readFileSync(file))+'  '+path.relative(vendor,file).split(path.sep).join('/'));
  fs.writeFileSync(path.join(vendor,'SOURCE-SNAPSHOT.sha256'), inventory.join('\n')+'\n');
  process.stdout.write('Verified pinned source; generated SqlClient source inventories and separate production/test patches.\n');
} finally {
  fs.rmSync(scratch, {recursive:true});
}
