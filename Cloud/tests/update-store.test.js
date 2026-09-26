const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {publishVerified,publishTrialVerified,disable,disableTrial,hashFile}=require('../update-store');
function fixture(fn){const root=fs.mkdtempSync(path.join(os.tmpdir(),'tor-publish-'));try{fn(root);}finally{fs.rmSync(root,{recursive:true,force:true});}}
const options=source=>({source,version:'0.7.33.550',revision:'R55',sha256:hashFile(source),signer_thumbprint:'A'.repeat(40)});
test('invalid hash preserves active update',()=>fixture(root=>{const s=path.join(root,'in.exe'),d=path.join(root,'updates');fs.writeFileSync(s,'old');const first=publishVerified(d,options(s));fs.writeFileSync(s,'new');assert.throws(()=>publishVerified(d,{...options(s),sha256:'0'.repeat(64)}));assert.deepEqual(JSON.parse(fs.readFileSync(path.join(d,'manifest.json'))),first);assert.equal(fs.readFileSync(path.join(d,first.filename),'utf8'),'old');}));
test('new publication preserves old installer and disable changes manifest',()=>fixture(root=>{const s=path.join(root,'in.exe'),d=path.join(root,'updates');fs.writeFileSync(s,'first');const first=publishVerified(d,options(s));fs.writeFileSync(s,'second');const second=publishVerified(d,options(s));assert.notEqual(first.filename,second.filename);assert.equal(hashFile(path.join(d,first.filename)),first.sha256);disable(d);assert.equal(JSON.parse(fs.readFileSync(path.join(d,'manifest.json'))).enabled,false);}));
test('lock and absent signature metadata prevent publication',()=>fixture(root=>{const s=path.join(root,'in.exe'),d=path.join(root,'updates');fs.writeFileSync(s,'first');const first=publishVerified(d,options(s));fs.writeFileSync(path.join(d,'.publish.lock'),'busy');assert.throws(()=>publishVerified(d,options(s)));assert.throws(()=>publishVerified(d,{...options(s),signer_thumbprint:''}));assert.deepEqual(JSON.parse(fs.readFileSync(path.join(d,'manifest.json'))),first);}));

test('trial publication is separate, seven-day and independently disableable',()=>fixture(root=>{const s=path.join(root,'demo.exe'),d=path.join(root,'updates');fs.writeFileSync(s,'demo');const trial=publishTrialVerified(d,options(s));assert.equal(trial.trial_days,7);assert.match(trial.filename,/^TOR-POS-Demo-Setup-[A-F0-9]{64}\.exe$/);assert.equal(fs.existsSync(path.join(d,'manifest.json')),false,'trial publishing never overwrites commercial update manifest');disableTrial(d);assert.equal(JSON.parse(fs.readFileSync(path.join(d,'trial-manifest.json'))).enabled,false);}));

// C-2: each product can have its own update channel; the shared one is untouched.
test('C-2 an edition publication gets its own manifest and product setup name',()=>fixture(root=>{
 const s=path.join(root,'in.exe'),d=path.join(root,'updates');fs.writeFileSync(s,'shared');const shared=publishVerified(d,options(s));
 fs.writeFileSync(s,'restaurant');const r=publishVerified(d,{...options(s),edition:'restaurant'});
 assert.deepEqual(r.editions,['RESTAURANT']);assert.match(r.filename,/^TOR-Restaurant-Setup-[A-F0-9]{64}\.exe$/);
 assert.deepEqual(JSON.parse(fs.readFileSync(path.join(d,'manifest-RESTAURANT.json'))),r);
 assert.deepEqual(JSON.parse(fs.readFileSync(path.join(d,'manifest.json'))),shared,'the shared KIOSK/IMBISS channel stays as it was');
 assert.throws(()=>publishVerified(d,{...options(s),edition:'TISCH'}),/KIOSK, IMBISS oder RESTAURANT/);
 disable(d,'RESTAURANT');assert.equal(JSON.parse(fs.readFileSync(path.join(d,'manifest-RESTAURANT.json'))).enabled,false);
 assert.equal(JSON.parse(fs.readFileSync(path.join(d,'manifest.json'))).enabled,true,'disabling one edition leaves the shared channel on');
}));
