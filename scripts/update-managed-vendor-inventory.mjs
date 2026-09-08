// Derived source inventories for the two small pinned managed dependencies.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {execFileSync} from 'node:child_process';

const specs = {
  sshnet: {root:'SSH.NET-7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e', hash:'e3c305e2bf41d00f7aba51b9cdd151567dfe32c616ad322561ac59e3d6a4593b', patch:'rfc1929-authentication.patch'},
  sharpcompress: {root:'sharpcompress-67bd9289f99dc77e1b65730a08e8213405921488', hash:'9a3a4d57b279243ce24332fbb342c152b5c286172fa57881d9536e83c777afeb', patch:'uncached-zip-enumeration.patch'},
};
const [name, archive] = process.argv.slice(2);
const spec = specs[name];
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
if (!spec || !archive || hash(fs.readFileSync(archive)) !== spec.hash) throw new Error('Expected an exact supported vendor and pinned archive.');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const vendor = path.join(repository, 'vendor', name);
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'ghostshell-managed-vendor-inventory-'));
const excludedFiles = new Set(['SOURCE-SNAPSHOT.sha256','packages.lock.json',
  ...['linux-x64','linux-arm64','osx-x64','osx-arm64','win-x64'].map(rid => 'packages.'+rid+'.lock.json')]);
const files = directory => fs.readdirSync(directory, {withFileTypes:true}).flatMap(entry => {
  const full = path.join(directory,entry.name);
  if (entry.isSymbolicLink()) throw new Error('Source inventory refuses symbolic links.');
  if (entry.isDirectory() && ['bin','obj'].includes(path.relative(vendor,full))) return [];
  return entry.isDirectory() ? files(full) : [full];
}).sort();
try {
  execFileSync('/usr/bin/tar', ['-xzf', path.resolve(archive), '-C', scratch]);
  const originalRoot = path.join(scratch,spec.root);
  const imported = files(path.join(vendor,'upstream'));
  const upstreamLines = imported.map(file => {
    const relative = path.relative(path.join(vendor,'upstream'),file);
    return hash(fs.readFileSync(path.join(originalRoot,relative)))+'  upstream/'+relative.split(path.sep).join('/');
  });
  execFileSync('/usr/bin/patch', ['--batch','-p1','-i',path.join(vendor,spec.patch)], {cwd:originalRoot});
  for (const file of imported) {
    const original = path.join(originalRoot,path.relative(path.join(vendor,'upstream'),file));
    if (!fs.readFileSync(file).equals(fs.readFileSync(original))) throw new Error('Imported source differs from pinned source plus recorded patch.');
  }
  fs.writeFileSync(path.join(vendor,'UPSTREAM-SOURCE.sha256'),upstreamLines.sort().join('\n')+'\n');
  const inventory = files(vendor).filter(file => !excludedFiles.has(path.relative(vendor,file)))
    .map(file => hash(fs.readFileSync(file))+'  '+path.relative(vendor,file).split(path.sep).join('/'));
  fs.writeFileSync(path.join(vendor,'SOURCE-SNAPSHOT.sha256'),inventory.join('\n')+'\n');
  process.stdout.write('Verified pinned archive and recorded patch; regenerated '+name+' source inventories.\n');
} finally {
  fs.rmSync(scratch,{recursive:true});
}
