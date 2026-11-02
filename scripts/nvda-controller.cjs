const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const pinned={
  'nvdaControllerClient.dll':'7EF3AAA056C8B3B37DB9F8CD82DA94AD6254F1726184F5FC17D8C90510F54908',
  'nvdaController.h':'749E6D40616F6AFD2409C5EE1A9260CB65014805C446A60327B1C7A276A41923',
  'LICENSE.txt':'36B6D3FA47916943FD5FEC313C584784946047EC1337A78B440E5992CB595F89',
  'nvda-2026.3-source.zip':'18C72C4D97BFFAED69704E6FA808992C56EDBBD106E91BF27529DD59F1265054'
};
const packageRoot=process.argv[2],root=packageRoot?path.join(packageRoot,'nvda'):path.resolve(__dirname,'../desktop/ThirdParty/NvdaController/2026.3');
for(const [name,expected] of Object.entries(pinned)){
  const packagedName=packageRoot&&/\.(h|zip)$/.test(name)?name+'.txt':name;
  const bytes=fs.readFileSync(path.join(root,packagedName));
  if(crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase()!==expected)throw Error('NVDA upstream file verification failed: '+packagedName);
}
for(const name of ['README.md','UPSTREAM-README.md','PROVENANCE.json'])if(!fs.existsSync(path.join(root,name)))throw Error('NVDA notices missing: '+name);
const library=fs.readFileSync(path.join(root,'nvdaControllerClient.dll'));
if(library.toString('ascii',0,2)!=='MZ'||library.readUInt16LE(library.readUInt32LE(0x3c)+4)!==0x8664)throw Error('The NVDA controller must be the official x64 library.');
console.log('Verified NVDA x64 controller, license, notices and complete corresponding source.');
