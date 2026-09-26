'use strict';
// deploy/update-cloud.sh against a stand-in systemctl: a good update replaces
// the code and keeps the database, a syntax error changes nothing, and a
// version that does not start is rolled back automatically.
const {test,before,after}=require('node:test');
const assert=require('node:assert/strict');
const {spawnSync}=require('node:child_process');
const {mkdtempSync,rmSync,cpSync,writeFileSync,readFileSync,existsSync,chmodSync,mkdirSync,appendFileSync}=require('node:fs');
const {tmpdir}=require('node:os');
const path=require('node:path');

const skip=process.platform==='win32'||spawnSync('bash',['-c','command -v curl']).status!==0;
const cloud=path.join(__dirname,'..');
const version=readFileSync(path.join(cloud,'server.js'),'utf8').match(/const CLOUD_VERSION='([^']+)'/)[1];
const port=String(20000+Math.floor(Math.random()*20000));
let root,install,env;

function copyCloud(to,patch){
  cpSync(cloud,to,{recursive:true,filter:src=>!/[\\/](node_modules|data|updates)([\\/]|$)/.test(src.slice(cloud.length))});
  if(patch)writeFileSync(path.join(to,'server.js'),patch(readFileSync(path.join(to,'server.js'),'utf8')));
}
function run(source,extra={}){
  return spawnSync('bash',[path.join(cloud,'deploy','update-cloud.sh'),source],{encoding:'utf8',timeout:90000,
    env:{...process.env,PATH:path.join(root,'bin')+path.delimiter+process.env.PATH,TOR_CLOUD_DIR:install,TOR_CLOUD_ENV:env,
      TOR_CLOUD_USER:require('node:os').userInfo().username,TOR_CLOUD_UPDATE_TEST:'1',TOR_CLOUD_HEALTH_WAIT:'15',...extra}});
}
function health(){
  const r=spawnSync('curl',['-fsS','--max-time','3',`http://127.0.0.1:${port}/api/health`],{encoding:'utf8'});
  return r.status===0?JSON.parse(r.stdout).version:null;
}

before(()=>{
  if(skip)return;
  root=mkdtempSync(path.join(tmpdir(),'tor-cloud-update-'));
  install=path.join(root,'opt','tor-pos-cloud');
  copyCloud(install,s=>s.replace(/const CLOUD_VERSION='[^']+'/,"const CLOUD_VERSION='0.0.1-alt'"));
  mkdirSync(path.join(install,'data'),{recursive:true});mkdirSync(path.join(install,'updates'),{recursive:true});
  env=path.join(root,'tor-pos-cloud.env');
  writeFileSync(env,`HOST=127.0.0.1\nPORT=${port}\nTOR_CLOUD_DB=${path.join(install,'data','tor-cloud.db')}\nTOR_CLOUD_UPDATES=${path.join(install,'updates')}\nTOR_CLOUD_DEMO=true\n`);
  mkdirSync(path.join(root,'bin'));
  const pid=path.join(root,'pid'),systemctl=path.join(root,'bin','systemctl');
  writeFileSync(systemctl,[
    '#!/bin/bash',
    `PID='${pid}'`,
    'case "$1" in',
    ' stop) if [ -f "$PID" ]; then kill "$(cat "$PID")" 2>/dev/null; for i in 1 2 3 4 5 6 7 8; do kill -0 "$(cat "$PID")" 2>/dev/null && sleep 1; done; rm -f "$PID"; fi;;',
    ` start) cd '${install}'; set -a; . '${env}'; set +a; nohup node server.js >'${path.join(root,'log')}' 2>&1 & echo $! > "$PID";;`,
    'esac','exit 0',''].join('\n'));
  chmodSync(systemctl,0o755);
  spawnSync(systemctl,['start']);
  for(let i=0;i<30&&health()!=='0.0.1-alt';i++)spawnSync('sleep',['0.5']);
});
after(()=>{if(skip)return;spawnSync(path.join(root,'bin','systemctl'),['stop']);rmSync(root,{recursive:true,force:true});});

test('update-cloud.sh: update, refused syntax error and automatic rollback',{skip},()=>{
  assert.equal(health(),'0.0.1-alt','the old version runs before the update');
  appendFileSync(path.join(install,'data','marker.txt'),'kundendaten');

  const good=path.join(root,'good');copyCloud(good);
  const ok=run(good);
  assert.equal(ok.status,0,ok.stdout+ok.stderr);
  assert.equal(health(),version);
  assert.equal(readFileSync(path.join(install,'data','marker.txt'),'utf8'),'kundendaten','data/ is never touched');
  assert.ok(existsSync(path.join(install,'data','tor-cloud.db')));

  const bad=path.join(root,'bad');copyCloud(bad);appendFileSync(path.join(bad,'validation.js'),'\nthis is not js(\n');
  const refused=run(bad);
  assert.notEqual(refused.status,0);assert.match(refused.stderr,/Syntaxfehler in validation\.js/);
  assert.equal(health(),version,'a syntax error stops before the service is touched');

  const crash=path.join(root,'crash');copyCloud(crash,s=>s.replace(/const CLOUD_VERSION='[^']+';/,"const CLOUD_VERSION='9.9.9'; throw new Error('boom');"));
  const rolled=run(crash,{TOR_CLOUD_HEALTH_WAIT:'6'});
  assert.equal(rolled.status,1);assert.match(rolled.stderr,/Zurückgesetzt: TOR Cloud läuft wieder mit/);
  assert.equal(health(),version,'the previous version runs again');
  assert.ok(!readFileSync(path.join(install,'server.js'),'utf8').includes('boom'));
  assert.equal(readFileSync(path.join(install,'data','marker.txt'),'utf8'),'kundendaten');
});
