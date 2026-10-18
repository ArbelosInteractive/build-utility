import { createRequire } from 'node:module';
import { resolve, relative, isAbsolute, sep } from 'node:path';
import { createReadStream } from 'node:fs';
import { readFile, writeFile, stat, lstat, readdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
async function hashFile(path) { const h=createHash('sha256'); for await (const c of createReadStream(path)) h.update(c); return h.digest('hex'); }
let input=''; for await (const chunk of process.stdin) input+=chunk;
try {
  const c=JSON.parse(input);
  const endpoint=new URL(c.endpoint);
  const local=['127.0.0.1','localhost','[::1]'].includes(endpoint.hostname);
  if(endpoint.search || endpoint.username || endpoint.password || (endpoint.protocol!=='https:' && !(c.localEmulator && local && endpoint.protocol==='http:'))) throw new Error('InvalidEndpoint');
  if(!/^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$/.test(c.container) || !/^[a-zA-Z0-9._/-]+$/.test(c.releasePrefix) || c.releasePrefix.split('/').some(p=>!p || p==='.' || p==='..')) throw new Error('InvalidDestination');
  if(!['publish','verify'].includes(c.action)) throw new Error('InvalidAction');
  const require=createRequire(resolve(c.dependenciesDirectory,'package.json'));
  const { BlobServiceClient, StorageSharedKeyCredential }=require('@azure/storage-blob');
  const key=c.localEmulator && local ? Buffer.alloc(32,7).toString('base64') : process.env[c.keyEnvironmentVariable];
  if(!key) throw new Error('MissingKeyEnvironmentVariable');
  const service=new BlobServiceClient(endpoint.href.replace(/\/$/,''),new StorageSharedKeyCredential(c.account,key),{retryOptions:{maxTries:3}});
  const container=service.getContainerClient(c.container);
  const manifestBytes=await readFile(c.manifestPath);
  const manifest=JSON.parse(manifestBytes);
  const build=JSON.parse(await readFile(c.buildReceiptPath,'utf8'));
  const inventoryBytes=await readFile(c.inventoryPath);
  const inventory=JSON.parse(inventoryBytes);
  if(!build.success || build.manifestSha256!==sha(manifestBytes) || build.releaseId!==manifest.releaseId) throw new Error('BuildReceiptDoesNotMatchManifest');
  if(inventory.schemaVersion!==1 || !Array.isArray(inventory.files) || !inventory.files.length) throw new Error('InvalidInventory');
  try { await stat(c.receiptPath); throw new Error('ReceiptAlreadyExists'); } catch(error) { if(error.code!=='ENOENT') throw error; }
  const seen=new Set(); const root=resolve(c.outputDirectory);
  const receiptRelative=relative(root,resolve(c.receiptPath));
  if(!receiptRelative || (!receiptRelative.startsWith('..'+sep) && receiptRelative!=='..' && !isAbsolute(receiptRelative))) throw new Error('ReceiptMustBeOutsideOutput');
  const onDisk=[];
  async function walk(directory) {
    if((await lstat(directory)).isSymbolicLink()) throw new Error('SymbolicLinksUnsupported');
    for(const entry of await readdir(directory,{withFileTypes:true})) {
      const path=resolve(directory,entry.name);
      if(entry.isSymbolicLink()) throw new Error('SymbolicLinksUnsupported');
      if(entry.isDirectory()) await walk(path);
      else if(entry.isFile()) onDisk.push(relative(root,path).split(sep).join('/'));
      else throw new Error('UnsupportedArtifactType');
    }
  }
  await walk(root);
  for(const f of inventory.files) {
    if(typeof f.path!=='string' || seen.has(f.path) || !/^[a-f0-9]{64}$/.test(f.sha256)) throw new Error('InvalidInventoryEntry');
    seen.add(f.path);
    const path=resolve(root,f.path); const rel=relative(root,path);
    if(!rel || rel.startsWith('..'+sep) || isAbsolute(rel) || f.path.includes('\\') || f.path.startsWith('/')) throw new Error('UnsafeArtifactPath');
    if((await stat(path)).size!==f.bytes || await hashFile(path)!==f.sha256) throw new Error('LocalArtifactChanged');
  }
  if(onDisk.length!==seen.size || onDisk.some(p=>!seen.has(p))) throw new Error('InventoryDoesNotCoverOutput');
  if(c.action==='publish') await container.createIfNotExists(c.publicBlobRead ? {access:'blob'} : {});
  const rank=f=>f.path.endsWith('.hash')?2:/\.(json|bin)$/.test(f.path)?1:0;
  const files=[...inventory.files].sort((a,b)=>rank(a)-rank(b)||a.path.localeCompare(b.path));
  const verified=[];
  for(const f of files) {
    const blob=container.getBlockBlobClient(c.releasePrefix+'/'+f.path);
    if(c.action==='publish' && !await blob.exists()) await blob.uploadFile(resolve(root,f.path),{conditions:{ifNoneMatch:'*'},metadata:{sha256:f.sha256}});
    const response=await blob.download();
    const hash=createHash('sha256'); let bytes=0;
    for await(const chunk of response.readableStreamBody) { hash.update(chunk); bytes+=chunk.length; }
    if(bytes!==f.bytes || hash.digest('hex')!==f.sha256) throw new Error('RemoteArtifactMismatch');
    verified.push({path:f.path,bytes:f.bytes,sha256:f.sha256});
  }
  const receipt={schemaVersion:1,releaseId:manifest.releaseId,manifestSha256:sha(manifestBytes),inventorySha256:sha(inventoryBytes),
    endpoint:endpoint.href,container:c.container,releasePrefix:c.releasePrefix,environment:c.environment,
    verifiedUtc:new Date().toISOString(),uploaded:c.action==='publish',remoteVerified:true,storePublished:false,files:verified};
  await writeFile(c.receiptPath,JSON.stringify(receipt,null,2),{flag:'wx'});
  console.log(JSON.stringify({success:true,receiptPath:c.receiptPath,verifiedFiles:verified.length,storePublished:false}));
} catch(error) {
  console.error(JSON.stringify({success:false,error:error.code || (/^[A-Za-z]+$/.test(error.message||'')?error.message:'PublicationFailed'),storePublished:false}));
  process.exitCode=1;
}
