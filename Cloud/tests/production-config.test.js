'use strict';
// Production preflight: live mode (TOR_CLOUD_DEMO=false) refuses to start with
// the leftovers of a half-finished /etc/tor-pos-cloud.env - a placeholder
// secret, an HTTPS portal without secure cookies, the demo database - and a
// complete live configuration starts without any demo data.
const {test,after}=require('node:test');
const assert=require('node:assert/strict');
const {spawn}=require('node:child_process');
const {mkdtempSync,rmSync,mkdirSync,readFileSync}=require('node:fs');
const {tmpdir}=require('node:os');
const path=require('node:path');

const cloud=path.join(__dirname,'..');
const root=mkdtempSync(path.join(tmpdir(),'tor-cloud-prodcfg-'));
after(()=>rmSync(root,{recursive:true,force:true}));

const example=readFileSync(path.join(cloud,'deploy','tor-pos-cloud.env.example'),'utf8');
const placeholder=/^TOR_CLOUD_TOTP_KEY=(.*)$/m.exec(example)[1];

function liveEnv(name,extra={}){
  const dir=path.join(root,name);mkdirSync(dir,{recursive:true});
  // Only what the test sets: nothing from the developer's shell leaks in.
  const env={PATH:process.env.PATH,HOST:'127.0.0.1',PORT:'0',TOR_CLOUD_DEMO:'false',COOKIE_SECURE:'true',
    TOR_CLOUD_PUBLIC_URL:'https://api.example.test',TOR_CLOUD_RECEIPT_URL:'https://bon.example.test',
    TOR_CLOUD_TRUST_PROXY:'true',TOR_CLOUD_TOTP_KEY:'k3Vq8ZrT1mYp6Lw2Nc9Hs4Jd7Fg0Bx5Qa',
    TOR_CLOUD_DB:path.join(dir,'tor-cloud.db'),TOR_CLOUD_UPDATES:path.join(dir,'updates')};
  for(const [k,v] of Object.entries(extra)){if(v===undefined)delete env[k];else env[k]=v;}
  return env;
}

// Starts server.js; resolves {base} once it listens or {code,log} when it exits.
function start(env){
  return new Promise(resolve=>{
    const child=spawn(process.execPath,['server.js'],{cwd:cloud,env});
    let log='';
    const onData=d=>{log+=d;const m=log.match(/http:\/\/127\.0\.0\.1:(\d+)/);if(m)resolve({child,base:m[0],log});};
    child.stdout.on('data',onData);child.stderr.on('data',d=>{log+=d;});
    child.once('exit',code=>resolve({code,log}));
    setTimeout(()=>{child.kill();resolve({code:'timeout',log});},15000).unref();
  });
}
async function stop(child){if(!child||child.exitCode!==null)return;const done=new Promise(r=>child.once('exit',r));child.kill('SIGTERM');await done;}

test('live mode refuses the placeholder secrets of tor-pos-cloud.env.example',async()=>{
  assert.ok(placeholder.length>=24,'the placeholder alone would pass the length check');
  for(const [name,value] of [['TOR_CLOUD_TOTP_KEY',placeholder],['TOR_CLOUD_GOOGLE_TOKEN_KEY','CHANGE-ME-CHANGE-ME-CHANGE-ME-CHANGE-ME'],['TOR_MAIL_SMTP_PASSWORD','PLACEHOLDER']]){
    const r=await start(liveEnv('placeholder-'+name,{[name]:value}));
    await stop(r.child);
    assert.notEqual(r.code,0,`${name}: server must not start`);
    assert.match(r.log,new RegExp(`${name} enthält noch den Platzhalter`));
  }
});

test('live mode with an HTTPS portal requires COOKIE_SECURE=true',async()=>{
  const r=await start(liveEnv('cookie',{COOKIE_SECURE:'false'}));
  await stop(r.child);
  assert.notEqual(r.code,0);
  assert.match(r.log,/COOKIE_SECURE=true/);
});

test('live mode requires a real TOTP key and refuses a public listener without secure cookies',async()=>{
  const missing=await start(liveEnv('no-totp',{TOR_CLOUD_TOTP_KEY:undefined}));
  await stop(missing.child);
  assert.notEqual(missing.code,0);assert.match(missing.log,/TOR_CLOUD_TOTP_KEY mit mindestens 24 Zeichen/);
  const open=await start(liveEnv('open',{HOST:'0.0.0.0',COOKIE_SECURE:'false',TOR_CLOUD_PUBLIC_URL:''}));
  await stop(open.child);
  assert.notEqual(open.code,0);assert.match(open.log,/Externer Betrieb benötigt sichere Cookies/);
});

test('the demo database cannot be opened in live mode',async()=>{
  const env=liveEnv('demo-db');
  const demo=await start({...env,TOR_CLOUD_DEMO:'true',TOR_CLOUD_PUBLIC_URL:'',TOR_CLOUD_RECEIPT_URL:'',COOKIE_SECURE:'false'});
  assert.ok(demo.base,demo.log);await stop(demo.child);
  const live=await start(env);
  await stop(live.child);
  assert.notEqual(live.code,0);
  assert.match(live.log,/Demodatenbank nur mit TOR_CLOUD_DEMO=true/);
});

test('a complete live configuration starts, reports 0.14.0 without demo and has no demo login',async()=>{
  const version=/const CLOUD_VERSION='([^']+)'/.exec(readFileSync(path.join(cloud,'server.js'),'utf8'))[1];
  const r=await start(liveEnv('live'));
  try{
    assert.ok(r.base,r.log);
    const health=await (await fetch(r.base+'/api/health')).json();
    assert.equal(health.ok,true);assert.equal(health.demo,false);assert.equal(health.version,version);
    const login=await fetch(r.base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({email:'demo@torpos.local',password:'TorDemo2026!'})});
    assert.notEqual(login.status,200,'no demo owner exists in live mode');
  }finally{await stop(r.child);}
  assert.equal(r.child.exitCode,0,'SIGTERM closes the server and database cleanly');
});
