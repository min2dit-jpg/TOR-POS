'use strict';
// deploy/apply-caddyfile.sh, apply-c1-caddy.sh, install-cloud.sh and
// preflight-cloud.sh against stand-in caddy/systemctl binaries: nothing reaches
// the running Caddy unless it validated, a failed reload puts the previous
// file back, patches are idempotent, and a first install only starts once the
// environment file is complete.
const {test,after}=require('node:test');
const assert=require('node:assert/strict');
const {spawnSync}=require('node:child_process');
const {mkdtempSync,rmSync,cpSync,writeFileSync,readFileSync,existsSync,chmodSync,mkdirSync,readdirSync,statSync}=require('node:fs');
const {tmpdir,userInfo}=require('node:os');
const path=require('node:path');

const skip=process.platform==='win32'||spawnSync('bash',['-c','command -v curl']).status!==0;
const cloud=path.join(__dirname,'..');
const deploy=path.join(cloud,'deploy');
const example=readFileSync(path.join(deploy,'Caddyfile.example'),'utf8');
const top=skip?null:mkdtempSync(path.join(tmpdir(),'tor-cloud-deploy-'));
const stops=[];
after(()=>{if(skip)return;for(const s of stops)s();rmSync(top,{recursive:true,force:true});});

// ---------------------------------------------------------------- Caddy
function caddyBench(name){
  const dir=path.join(top,name);const bin=path.join(dir,'bin');mkdirSync(bin,{recursive:true});
  const calls=path.join(dir,'calls.log');
  // caddy validate fails for a file containing INVALID; reload fails while
  // RELOAD_FAILS exists.
  writeFileSync(path.join(bin,'caddy'),`#!/bin/bash\necho "caddy $*" >> '${calls}'\n[ "$1" = validate ] || exit 0\ngrep -q INVALID "$3" && { echo "Error: adapting config" >&2; exit 1; }\nexit 0\n`);
  writeFileSync(path.join(bin,'systemctl'),`#!/bin/bash\necho "systemctl $*" >> '${calls}'\n[ -f '${path.join(dir,'RELOAD_FAILS')}' ] && [ "$1" = reload ] && exit 1\nexit 0\n`);
  chmodSync(path.join(bin,'caddy'),0o755);chmodSync(path.join(bin,'systemctl'),0o755);
  const caddyfile=path.join(dir,'Caddyfile');
  return {dir,caddyfile,
    calls:()=>existsSync(calls)?readFileSync(calls,'utf8'):'',
    reset:()=>writeFileSync(calls,''),
    backups:()=>readdirSync(dir).filter(n=>n.startsWith('Caddyfile.vor-')),
    failReload:on=>on?writeFileSync(path.join(dir,'RELOAD_FAILS'),''):rmSync(path.join(dir,'RELOAD_FAILS'),{force:true}),
    run:(script,...args)=>spawnSync('bash',[path.join(deploy,script),...args],{encoding:'utf8',env:{...process.env,PATH:bin+path.delimiter+process.env.PATH}})};
}
const preC1=example.replace(/\t# C-1:[\s\S]*?request_body @notTorMail \{\n\t\tmax_size 2MB\n\t\}/,'\trequest_body {\n\t\tmax_size 2MB\n\t}');

test('apply-c1-caddy.sh: backup, validate, reload once; a second run changes nothing',{skip},()=>{
  const b=caddyBench('c1');writeFileSync(b.caddyfile,preC1);
  const r=b.run('apply-c1-caddy.sh',b.caddyfile);
  assert.equal(r.status,0,r.stdout+r.stderr);
  const patched=readFileSync(b.caddyfile,'utf8');
  assert.equal((patched.match(/@torMail path \/api\/v1\/devices\/mail\/send/g)||[]).length,1);
  assert.equal(b.backups().length,1);assert.equal(readFileSync(path.join(b.dir,b.backups()[0]),'utf8'),preC1,'the backup is the file before');
  assert.match(b.calls(),/caddy validate[\s\S]*systemctl reload caddy/);
  b.reset();
  const again=b.run('apply-c1-caddy.sh',b.caddyfile);
  assert.equal(again.status,0);assert.match(again.stdout,/Bereits angepasst/);
  assert.equal(readFileSync(b.caddyfile,'utf8'),patched,'no duplicate block');
  assert.equal(b.backups().length,1,'no second backup');
  assert.equal(b.calls(),'','neither validate nor reload on the second run');
});

test('apply-c1-caddy.sh: an unexpected Caddyfile is refused and the running one stays',{skip},()=>{
  const b=caddyBench('c1-odd');
  const odd=preC1.replace('\trequest_body {\n\t\tmax_size 2MB\n\t}','\trequest_body {\n\t\tmax_size 2MB\n\t}\n\trequest_body {\n\t\tmax_size 5MB\n\t}');
  writeFileSync(b.caddyfile,odd);
  const r=b.run('apply-c1-caddy.sh',b.caddyfile);
  assert.equal(r.status,1);assert.match(r.stderr,/genau einen/);
  assert.equal(readFileSync(b.caddyfile,'utf8'),odd);assert.equal(b.calls(),'');assert.equal(b.backups().length,0);
});

test('apply-caddyfile.sh: an invalid config never reaches the running file and nothing reloads',{skip},()=>{
  const b=caddyBench('invalid');writeFileSync(b.caddyfile,example);
  const bad=path.join(b.dir,'new');writeFileSync(bad,example+'\nINVALID {\n');
  const r=b.run('apply-caddyfile.sh',bad,b.caddyfile);
  assert.equal(r.status,1);assert.match(r.stderr,/ungültig - die laufende Caddyfile wurde NICHT verändert/);
  assert.equal(readFileSync(b.caddyfile,'utf8'),example);
  assert.doesNotMatch(b.calls(),/reload/);assert.equal(b.backups().length,0);
  assert.deepEqual(readdirSync(b.dir).filter(n=>n.startsWith('.Caddyfile.pruefung')),[],'the check copy is removed');
});

test('apply-caddyfile.sh: a failed reload restores the previous file and reloads it',{skip},()=>{
  const b=caddyBench('reload');writeFileSync(b.caddyfile,example);
  const next=path.join(b.dir,'new');writeFileSync(next,example.replace('max_size 16KB','max_size 8KB'));
  b.failReload(true);
  const r=b.run('apply-caddyfile.sh',next,b.caddyfile);
  assert.equal(r.status,1);assert.match(r.stderr,/vorherige Caddyfile ist wiederhergestellt/);
  assert.equal(readFileSync(b.caddyfile,'utf8'),example);
  assert.equal((b.calls().match(/systemctl reload caddy/g)||[]).length,2,'reload, then reload of the restored file');
  b.failReload(false);b.reset();
  const ok=b.run('apply-caddyfile.sh',next,b.caddyfile);
  assert.equal(ok.status,0,ok.stdout+ok.stderr);assert.match(readFileSync(b.caddyfile,'utf8'),/max_size 8KB/);
  b.reset();
  const same=b.run('apply-caddyfile.sh',next,b.caddyfile);
  assert.equal(same.status,0);assert.match(same.stdout,/Unverändert/);assert.equal(b.calls(),'');
});

test('Caddyfile.example: TLS, proxy to 127.0.0.1:8787, forwarded address overwritten, strict receipt limit',()=>{
  const site=name=>{const start=example.indexOf(`${name} {`);const rest=example.slice(start);return rest.slice(0,rest.search(/\n}\n/)+2);};
  for(const name of ['api.torpos.de','bon.torpos.de']){
    const s=site(name);
    assert.match(s,/protocols tls1\.2 tls1\.3/,`${name}: TLS 1.2+`);
    assert.match(s,/reverse_proxy 127\.0\.0\.1:8787 \{\n\t\theader_up X-Forwarded-For \{remote_host\}/,`${name}: overwrites X-Forwarded-For`);
    assert.match(s,/Strict-Transport-Security/,`${name}: HSTS`);
  }
  assert.match(site('bon.torpos.de'),/request_body \{\n\t\tmax_size 16KB\n\t\}/,'the receipt domain takes no uploads');
  assert.doesNotMatch(site('bon.torpos.de'),/^\s*log\b/m,'the receipt token must not land in access logs');
  const real=spawnSync('bash',['-c','command -v caddy']).status===0;
  if(real){
    const r=spawnSync('caddy',['validate','--config',path.join(deploy,'Caddyfile.example'),'--adapter','caddyfile'],{encoding:'utf8'});
    assert.equal(r.status,0,r.stderr);
  }
});

// ---------------------------------------------------------------- install + preflight
function installBench(name){
  const root=path.join(top,name);mkdirSync(root,{recursive:true});
  const install=path.join(root,'opt','tor-cloud');const env=path.join(root,'etc','tor-pos-cloud.env');
  const unitDir=path.join(root,'systemd');const backups=path.join(root,'backups');
  const bin=path.join(root,'bin');mkdirSync(bin);mkdirSync(path.dirname(env),{recursive:true});
  const pid=path.join(root,'pid'),calls=path.join(root,'systemctl.log');
  const port=String(20000+Math.floor(Math.random()*20000));
  writeFileSync(path.join(bin,'systemctl'),[
    '#!/bin/bash',`echo "$*" >> '${calls}'`,
    'case "$1" in',
    ` is-active) [ -f '${pid}' ] && kill -0 "$(cat '${pid}')" 2>/dev/null; exit $?;;`,
    ` start) cd '${install}'; set -a; . '${env}'; set +a; nohup node server.js >>'${path.join(root,'service.log')}' 2>&1 & echo $! > '${pid}';;`,
    ` stop) [ -f '${pid}' ] && kill "$(cat '${pid}')" 2>/dev/null; sleep 1; rm -f '${pid}';;`,
    'esac','exit 0',''].join('\n'));
  chmodSync(path.join(bin,'systemctl'),0o755);
  const vars={PATH:bin+path.delimiter+process.env.PATH,TOR_CLOUD_DIR:install,TOR_CLOUD_ENV:env,TOR_CLOUD_USER:userInfo().username,
    TOR_CLOUD_UNIT_DIR:unitDir,TOR_CLOUD_BACKUP_DIR:backups,TOR_CLOUD_UPDATE_TEST:'1',TOR_CLOUD_HEALTH_WAIT:'20',TOR_CLOUD_SERVICE:'tor-pos-cloud'};
  const b={root,install,env,unitDir,backups,port,calls:()=>existsSync(calls)?readFileSync(calls,'utf8'):'',
    runInstall:(extra={})=>spawnSync('bash',[path.join(deploy,'install-cloud.sh'),cloud],{encoding:'utf8',timeout:90000,env:{...process.env,...vars,...extra}}),
    preflight:(extra={})=>spawnSync('bash',[path.join(deploy,'preflight-cloud.sh')],{encoding:'utf8',timeout:30000,
      env:{...process.env,...vars,TOR_CLOUD_UNIT:path.join(unitDir,'tor-pos-cloud.service'),TOR_CLOUD_ENV_OWNER:userInfo().username,...extra}}),
    fillEnv:()=>{
      let text=readFileSync(env,'utf8');
      const set=(k,v)=>{text=new RegExp(`^${k}=`,'m').test(text)?text.replace(new RegExp(`^${k}=.*$`,'m'),`${k}=${v}`):text+`\n${k}=${v}\n`;};
      set('PORT',port);set('TOR_CLOUD_TOTP_KEY','k3Vq8ZrT1mYp6Lw2Nc9Hs4Jd7Fg0Bx5Qa');
      set('TOR_CLOUD_PUBLIC_URL','https://api.example.test');set('TOR_CLOUD_RECEIPT_URL','https://bon.example.test');
      writeFileSync(env,text);
    },
    stop:()=>spawnSync(path.join(bin,'systemctl'),['stop','tor-pos-cloud'])};
  stops.push(b.stop);
  return b;
}

test('install-cloud.sh: first run installs but does not start until the env file is complete; second run starts live',{skip},()=>{
  const b=installBench('install');
  const first=b.runInstall();
  assert.equal(first.status,3,first.stdout+first.stderr);
  assert.match(first.stdout,/Installiert, aber NICHT gestartet/);
  assert.match(first.stdout,/FEHLER +Platzhalter noch nicht ersetzt: TOR_CLOUD_TOTP_KEY/);
  assert.ok(existsSync(path.join(b.install,'server.js')));
  assert.ok(!existsSync(path.join(b.install,'tests')),'tests are not deployed');
  for(const d of [path.join(b.install,'data'),path.join(b.install,'updates'),b.backups])assert.ok(statSync(d).isDirectory(),d);
  assert.equal((statSync(b.env).mode&0o777).toString(8),'600','env file is chmod 600');
  const env=readFileSync(b.env,'utf8');
  assert.match(env,/^TOR_CLOUD_DEMO=false$/m);assert.match(env,/^COOKIE_SECURE=true$/m);
  assert.ok(env.includes(`TOR_CLOUD_DB=${b.install}/data/tor-cloud.db`),'paths follow TOR_CLOUD_DIR');
  const unit=readFileSync(path.join(b.unitDir,'tor-pos-cloud.service'),'utf8');
  assert.ok(unit.includes(`WorkingDirectory=${b.install}`));assert.ok(unit.includes(`EnvironmentFile=${b.env}`));
  assert.ok(unit.includes(`ReadWritePaths=${b.install}/data ${b.install}/updates ${b.backups}`));
  assert.ok(unit.includes(`User=${userInfo().username}`));
  assert.doesNotMatch(b.calls(),/^start/m,'not started with placeholders');

  b.fillEnv();
  const second=b.runInstall();
  assert.equal(second.status,0,second.stdout+second.stderr);
  assert.match(second.stdout,/bleibt unverändert/);assert.match(second.stdout,/Fertig: TOR Cloud 0\.14\.0 läuft/);
  const health=JSON.parse(spawnSync('curl',['-fsS',`http://127.0.0.1:${b.port}/api/health`],{encoding:'utf8'}).stdout);
  assert.equal(health.demo,false);assert.equal(health.version,'0.14.0');

  const pre=b.preflight({TOR_CLOUD_SKIP_CADDY:'1'});
  assert.equal(pre.status,0,pre.stdout);
  assert.match(pre.stdout,/OK +\/api\/health antwortet im Livemodus/);

  const spaced=b.runInstall({TOR_CLOUD_DIR:path.join(b.root,'opt','tor cloud')});
  assert.equal(spaced.status,1);assert.match(spaced.stderr,/Leerzeichen oder Sonderzeichen/);
  assert.ok(!existsSync(path.join(b.root,'opt','tor cloud')),'nothing created for a refused path');

  const third=b.runInstall();
  assert.equal(third.status,1);assert.match(third.stderr,/läuft bereits - für eine neue Version update-cloud\.sh/);
});

test('preflight-cloud.sh reports every unsafe production setting',{skip},()=>{
  const b=installBench('preflight');
  assert.equal(b.runInstall().status,3);
  writeFileSync(b.env,readFileSync(b.env,'utf8')
    .replace(/^TOR_CLOUD_DEMO=.*$/m,'TOR_CLOUD_DEMO=true').replace(/^COOKIE_SECURE=.*$/m,'COOKIE_SECURE=false')
    .replace(/^TOR_CLOUD_TRUST_PROXY=.*$/m,'TOR_CLOUD_TRUST_PROXY=false').replace(/^HOST=.*$/m,'HOST=0.0.0.0')
    .replace(/^TOR_CLOUD_PUBLIC_URL=.*$/m,'TOR_CLOUD_PUBLIC_URL=http://api.example.test')
    .replace(/^TOR_CLOUD_RECEIPT_URL=.*$/m,'TOR_CLOUD_RECEIPT_URL=https://api.example.test'));
  chmodSync(b.env,0o644);rmSync(b.backups,{recursive:true});
  const r=b.preflight({PATH:path.join(b.root,'bin')+path.delimiter+'/usr/bin:/bin'});
  assert.equal(r.status,1);
  for(const msg of [/Rechte 644 - nötig: chmod 600/,/Platzhalter noch nicht ersetzt/,/TOR_CLOUD_DEMO muss false sein/,/COOKIE_SECURE muss true sein/,
    /TOR_CLOUD_TRUST_PROXY muss hinter Caddy true sein/,/HOST=0\.0\.0\.0/,/TOR_CLOUD_PUBLIC_URL muss https/,/Kassenbon-Domain und API-Domain müssen verschieden sein/,
    /Sicherungsordner .* fehlt/,/ReadWritePaths-Ordner .* fehlt/])
    assert.match(r.stdout,msg);
  assert.match(r.stdout,/Ergebnis: \d+ FEHLER - nicht live schalten/);
});
