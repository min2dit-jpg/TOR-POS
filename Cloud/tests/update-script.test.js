'use strict';
// deploy/update-cloud.sh against a stand-in systemctl that runs server.js in
// the background. Every scenario gets its own installation, port and service
// name, so the cases cannot leak into each other.
const {test,after}=require('node:test');
const assert=require('node:assert/strict');
const {spawnSync}=require('node:child_process');
const {mkdtempSync,rmSync,cpSync,writeFileSync,readFileSync,existsSync,chmodSync,mkdirSync,appendFileSync,readdirSync}=require('node:fs');
const {tmpdir,userInfo}=require('node:os');
const path=require('node:path');
const {DatabaseSync}=require('node:sqlite');

const skip=process.platform==='win32'||spawnSync('bash',['-c','command -v curl && command -v flock']).status!==0;
const zipSkip=skip||spawnSync('bash',['-c','command -v zip && command -v unzip']).status!==0;
const cloud=path.join(__dirname,'..');
const version=readFileSync(path.join(cloud,'server.js'),'utf8').match(/const CLOUD_VERSION='([^']+)'/)[1];
const OLD='0.13.9';
const top=skip?null:mkdtempSync(path.join(tmpdir(),'tor-cloud-update-'));
const running=[];
after(()=>{if(skip)return;for(const f of running)f.stop();rmSync(top,{recursive:true,force:true});});

function copyCloud(to,patch){
  cpSync(cloud,to,{recursive:true,filter:src=>!/[\\/](node_modules|data|updates|tests)([\\/]|$)/.test(src.slice(cloud.length))});
  if(patch)writeFileSync(path.join(to,'server.js'),patch(readFileSync(path.join(to,'server.js'),'utf8')));
  return to;
}
const withVersion=v=>s=>s.replace(/const CLOUD_VERSION='[^']+'/,`const CLOUD_VERSION='${v}'`);

// One installation of TOR Cloud OLD, running, with its own stand-in systemctl.
function fixture(name,{dir='opt/tor-pos-cloud',service='tor-pos-cloud'}={}){
  const root=path.join(top,name);
  const install=path.join(root,dir);
  copyCloud(install,withVersion(OLD));
  for(const d of ['data','updates','node_modules'])mkdirSync(path.join(install,d),{recursive:true});
  writeFileSync(path.join(install,'node_modules','kept.txt'),'modul');
  writeFileSync(path.join(install,'updates','manifest.json'),'{"kept":true}');
  const port=String(20000+Math.floor(Math.random()*20000));
  const env=path.join(root,'tor-pos-cloud.env');
  writeFileSync(env,`HOST=127.0.0.1\nPORT=${port}\nTOR_CLOUD_DB=${path.join(install,'data','tor-cloud.db')}\nTOR_CLOUD_UPDATES=${path.join(install,'updates')}\nTOR_CLOUD_DEMO=true\n`);
  const bin=path.join(root,'bin');mkdirSync(bin);
  const pid=path.join(root,'pid'),calls=path.join(root,'systemctl.log');
  // A package whose server.js contains FAIL-TO-START makes "start" fail, as a
  // broken unit or missing ExecStart binary would.
  writeFileSync(path.join(bin,'systemctl'),[
    '#!/bin/bash',
    `echo "$*" >> '${calls}'`,
    `[ "$2" = '${service}' ] || { echo "unbekannter Dienst $2" >&2; exit 5; }`,
    'case "$1" in',
    ` stop) if [ -f '${pid}' ]; then kill "$(cat '${pid}')" 2>/dev/null; for i in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$(cat '${pid}')" 2>/dev/null && sleep 0.5; done; rm -f '${pid}'; fi;;`,
    ` start) grep -q FAIL-TO-START '${install}/server.js' && exit 1; cd '${install}'; set -a; . '${env}'; set +a; nohup node server.js >>'${path.join(root,'service.log')}' 2>&1 & echo $! > '${pid}';;`,
    'esac','exit 0',''].join('\n'));
  chmodSync(path.join(bin,'systemctl'),0o755);
  const health=()=>{const r=spawnSync('curl',['-fsS','--max-time','3',`http://127.0.0.1:${port}/api/health`],{encoding:'utf8'});return r.status===0?JSON.parse(r.stdout).version:null;};
  const f={root,install,env,service,health,
    calls:()=>existsSync(calls)?readFileSync(calls,'utf8'):'',
    stop:()=>spawnSync(path.join(bin,'systemctl'),['stop',service]),
    backups:()=>readdirSync(path.dirname(install)).filter(n=>n.startsWith(path.basename(install)+'.sicherung-')).sort(),
    run:(source,extra={})=>spawnSync('bash',[path.join(cloud,'deploy','update-cloud.sh'),source],{encoding:'utf8',timeout:120000,
      env:{...process.env,PATH:bin+path.delimiter+process.env.PATH,TOR_CLOUD_DIR:install,TOR_CLOUD_ENV:env,TOR_CLOUD_SERVICE:service,
        TOR_CLOUD_USER:userInfo().username,TOR_CLOUD_UPDATE_TEST:'1',TOR_CLOUD_HEALTH_WAIT:'15',TOR_CLOUD_SETTLE:'1',...extra}})};
  running.push(f);
  spawnSync(path.join(bin,'systemctl'),['start',service]);
  for(let i=0;i<40&&health()!==OLD;i++)spawnSync('sleep',['0.25']);
  assert.equal(health(),OLD,'the old version runs before the update');
  // Customer data in the live database and next to it.
  const db=new DatabaseSync(path.join(install,'data','tor-cloud.db'));db.exec('PRAGMA busy_timeout=5000;');
  db.exec("CREATE TABLE IF NOT EXISTS update_test_marker(v TEXT); INSERT INTO update_test_marker VALUES('kundendaten');");db.close();
  writeFileSync(path.join(install,'data','marker.txt'),'kundendaten');
  writeFileSync(calls,''); // only what the update script does from here on
  return f;
}
function customerDataKept(f){
  assert.equal(readFileSync(path.join(f.install,'data','marker.txt'),'utf8'),'kundendaten','data/ is never touched');
  const db=new DatabaseSync(path.join(f.install,'data','tor-cloud.db'),{readOnly:true});
  try{assert.equal(db.prepare('SELECT v FROM update_test_marker').get().v,'kundendaten','the database keeps its rows');}finally{db.close();}
  assert.equal(readFileSync(path.join(f.install,'node_modules','kept.txt'),'utf8'),'modul','node_modules/ stays');
  assert.equal(readFileSync(path.join(f.install,'updates','manifest.json'),'utf8'),'{"kept":true}','updates/ stays');
}

test(`update ${OLD} -> ${version} from a folder: new code runs, data kept, full backup written`,{skip},()=>{
  const f=fixture('ok');
  const r=f.run(copyCloud(path.join(f.root,'new')));
  assert.equal(r.status,0,r.stdout+r.stderr);
  assert.equal(f.health(),version);
  customerDataKept(f);
  const [backup]=f.backups();
  assert.ok(backup,'a backup exists');
  const saved=path.join(path.dirname(f.install),backup);
  assert.match(readFileSync(path.join(saved,'server.js'),'utf8'),new RegExp(`CLOUD_VERSION='${OLD.replace(/\./g,'\\.')}'`),'backup holds the old code');
  assert.ok(existsSync(path.join(saved,'data','tor-cloud.db')),'backup holds the database');
  assert.match(f.calls(),/^stop tor-pos-cloud\nstart tor-pos-cloud\n$/);
});

test('custom TOR_CLOUD_DIR and TOR_CLOUD_SERVICE are honoured',{skip},()=>{
  const f=fixture('custom',{dir:'srv/kunde a/cloud',service:'tor-cloud-test@1'});
  const r=f.run(copyCloud(path.join(f.root,'new')));
  assert.equal(r.status,0,r.stdout+r.stderr);
  assert.equal(f.health(),version);
  assert.match(f.calls(),/^stop tor-cloud-test@1\nstart tor-cloud-test@1\n$/,'only the configured service is touched');
  assert.equal(f.backups().length,1,'the backup sits next to the custom folder');
  customerDataKept(f);
});

test('a GitHub ZIP (<repo>/Cloud) and a ZIP of the Cloud folder itself are accepted',{skip:zipSkip},()=>{
  const f=fixture('zip');
  const repo=path.join(f.root,'zip-src','TOR-POS-main');copyCloud(path.join(repo,'Cloud'));mkdirSync(path.join(repo,'Desktop'));
  const gh=path.join(f.root,'TOR-POS-main.zip');
  assert.equal(spawnSync('zip',['-qr',gh,'TOR-POS-main'],{cwd:path.join(f.root,'zip-src')}).status,0);
  let r=f.run(gh);
  assert.equal(r.status,0,r.stdout+r.stderr);assert.equal(f.health(),version);

  const flat=copyCloud(path.join(f.root,'flat'),withVersion('0.14.1-flat'));
  const flatZip=path.join(f.root,'cloud-flat.zip');
  assert.equal(spawnSync('zip',['-qr',flatZip,'.'],{cwd:flat}).status,0);
  r=f.run(flatZip);
  assert.equal(r.status,0,r.stdout+r.stderr);assert.equal(f.health(),'0.14.1-flat');
  customerDataKept(f);

  const empty=path.join(f.root,'empty.zip');writeFileSync(path.join(f.root,'readme.txt'),'x');
  spawnSync('zip',['-q',empty,'readme.txt'],{cwd:f.root});
  r=f.run(empty);
  assert.notEqual(r.status,0);assert.match(r.stderr,/kein TOR-Cloud-Ordner/);
});

test('broken JS anywhere in the package is refused before the service is touched',{skip},()=>{
  const f=fixture('syntax');
  for(const file of ['validation.js','qr-v6.js','tools/provision.js']){
    const bad=copyCloud(path.join(f.root,'bad-'+file.replace('/','-')));
    appendFileSync(path.join(bad,file),'\nthis is not js(\n');
    const r=f.run(bad);
    assert.equal(r.status,1);assert.match(r.stderr,new RegExp(`Syntaxfehler in ${file.replace(/[./]/g,'\\$&')}`));
  }
  assert.equal(f.calls(),'','systemctl was never called');
  assert.equal(f.health(),OLD);assert.equal(f.backups().length,0);
});

test('service start fails -> old version restored and verified',{skip},()=>{
  const f=fixture('start-fails');
  const bad=copyCloud(path.join(f.root,'new'),s=>s+'\n// FAIL-TO-START\n');
  const r=f.run(bad);
  assert.equal(r.status,1,r.stdout+r.stderr);
  assert.match(r.stderr,/Dienst startet nicht/);
  assert.match(r.stderr,new RegExp(`Zurückgesetzt: TOR Cloud läuft wieder mit ${OLD.replace(/\./g,'\\.')}`));
  assert.equal(f.health(),OLD);
  assert.ok(!readFileSync(path.join(f.install,'server.js'),'utf8').includes('FAIL-TO-START'));
  customerDataKept(f);
});

test('new version crashes at startup -> rollback to the old version',{skip},()=>{
  const f=fixture('crash');
  const crash=copyCloud(path.join(f.root,'crash'),s=>s.replace(/const CLOUD_VERSION='[^']+';/,"const CLOUD_VERSION='9.9.9'; throw new Error('boom');"));
  const r=f.run(crash,{TOR_CLOUD_HEALTH_WAIT:'5'});
  assert.equal(r.status,1);assert.match(r.stderr,/Keine gültige Antwort/);
  assert.match(r.stderr,/Zurückgesetzt: TOR Cloud läuft wieder mit 0\.13\.9/);
  assert.equal(f.health(),OLD);customerDataKept(f);
});

test('health endpoint reports another version -> rollback',{skip},()=>{
  const f=fixture('wrong-version');
  const liar=copyCloud(path.join(f.root,'liar'),s=>s.replace("version:CLOUD_VERSION, demo:DEMO","version:'0.0.0-falsch', demo:DEMO"));
  assert.ok(readFileSync(path.join(liar,'server.js'),'utf8').includes("0.0.0-falsch"),'fixture patch applied');
  const r=f.run(liar);
  assert.equal(r.status,1);assert.match(r.stderr,new RegExp(`meldet 0\\.0\\.0-falsch statt ${version.replace(/\./g,'\\.')}`));
  assert.equal(f.health(),OLD);customerDataKept(f);
});

test('a version that answers once and then dies is caught and rolled back',{skip},()=>{
  const f=fixture('flaky');
  const flaky=copyCloud(path.join(f.root,'flaky'),s=>s+"\nsetTimeout(()=>process.exit(1),1500).unref?.();\n");
  const r=f.run(flaky,{TOR_CLOUD_SETTLE:'3'});
  assert.equal(r.status,1,r.stdout+r.stderr);assert.match(r.stderr,/nicht mehr stabil/);
  assert.equal(f.health(),OLD);customerDataKept(f);
});

test('backup retention keeps the newest TOR_CLOUD_KEEP backups',{skip},()=>{
  const f=fixture('retention');
  const base=path.basename(f.install),parent=path.dirname(f.install);
  for(const stamp of ['20200101-000000','20200102-000000','20200103-000000','20200104-000000'])
    mkdirSync(path.join(parent,`${base}.sicherung-${stamp}`));
  const r=f.run(copyCloud(path.join(f.root,'new')),{TOR_CLOUD_KEEP:'3'});
  assert.equal(r.status,0,r.stdout+r.stderr);
  const left=f.backups();
  assert.equal(left.length,3);
  assert.deepEqual(left.slice(0,2),[`${base}.sicherung-20200103-000000`,`${base}.sicherung-20200104-000000`]);
  assert.ok(!left[2].includes('2020'),'the backup of this update is kept');
});

test('bad input is refused without touching anything',{skip},()=>{
  const f=fixture('input');
  const good=copyCloud(path.join(f.root,'new'));
  for(const [source,extra,msg] of [
    [good,{TOR_CLOUD_KEEP:'0'},/TOR_CLOUD_KEEP muss eine Zahl ab 1/],
    [good,{TOR_CLOUD_KEEP:'abc'},/TOR_CLOUD_KEEP muss eine Zahl ab 1/],
    [good,{TOR_CLOUD_SERVICE:'x; rm -rf /'},/unzulässige Zeichen/],
    [good,{TOR_CLOUD_USER:'kein-solcher-benutzer-xyz'},/Dienstbenutzer kein-solcher-benutzer-xyz existiert nicht/],
    [good,{TOR_CLOUD_DIR:path.join(f.root,'fehlt')},/Erstinstallation mit deploy\/install-cloud\.sh/],
    [f.install,{},/nicht im Installationsordner/],
    [path.join(f.root,'gibt-es-nicht'),{},/weder ein Ordner noch eine \.zip-Datei/]
  ]){
    const r=f.run(source,extra);
    assert.equal(r.status,1,`${JSON.stringify(extra)}: ${r.stdout}${r.stderr}`);assert.match(r.stderr,msg);
  }
  assert.equal(f.calls(),'');assert.equal(f.health(),OLD);assert.equal(f.backups().length,0);
});

test('a second update while one is running is refused',{skip},()=>{
  const f=fixture('lock');
  const hold=require('node:child_process').spawn('flock',[`${f.install}.update.lock`,'sleep','5']);
  spawnSync('sleep',['0.5']);
  try{
    const r=f.run(copyCloud(path.join(f.root,'new')));
    assert.equal(r.status,1);assert.match(r.stderr,/anderes Update läuft bereits/);
    assert.equal(f.calls(),'');assert.equal(f.health(),OLD);
  }finally{hold.kill();}
});

test('the backup can be applied as a source to go back one version',{skip},()=>{
  const f=fixture('back');
  assert.equal(f.run(copyCloud(path.join(f.root,'new'))).status,0);
  assert.equal(f.health(),version);
  const [backup]=f.backups();
  const r=f.run(path.join(path.dirname(f.install),backup));
  assert.equal(r.status,0,r.stdout+r.stderr);
  assert.equal(f.health(),OLD);customerDataKept(f);
});
