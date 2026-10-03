import http from 'node:http';
import {readFile,writeFile} from 'node:fs/promises';
const assets = {'/':'index.html','/_ui/app.js':'app.js','/_ui/style.css':'style.css','/_ui/password.css':'password.css','/_ui/cloud.svg':'cloud.svg'};
const types = {html:'text/html; charset=utf-8',js:'text/javascript; charset=utf-8',css:'text/css; charset=utf-8',svg:'image/svg+xml'};
const secureHosts=new Set((process.env.YIZUKA_SECURE_HOSTS || 'cloud.17yizuka.com').split(',').map(s=>s.trim()).filter(Boolean));
const allowedHosts=new Set(['127.0.0.1:3924','localhost:3924',...secureHosts]);
function isSecureHost(host){return secureHosts.has(host||'');}
function bridgeSecureCookie(cookie=''){if(!cookie||/(?:^|;\s*)cppws=/.test(cookie))return cookie;const match=cookie.match(/(?:^|;\s*)cppwd=([^;]*)/);return match?cookie+'; cppws='+match[1]:cookie;}
function readBody(req,limit=1024){return new Promise((resolve,reject)=>{let size=0;const chunks=[];req.on('data',chunk=>{size+=chunk.length;if(size>limit){reject(Error('too large'));req.destroy();return;}chunks.push(chunk);});req.on('end',()=>resolve(Buffer.concat(chunks)));req.on('error',reject);});}
function sessionInfo(req){return new Promise((resolve,reject)=>{const secure=isSecureHost(req.headers.host);const headers={accept:'application/json','x-forwarded-for':'127.0.0.1','x-forwarded-proto':secure?'https':'http'};if(req.headers.cookie)headers.cookie=secure?bridgeSecureCookie(req.headers.cookie):req.headers.cookie;if(req.headers.authorization)headers.authorization=req.headers.authorization;const check=http.request({hostname:'127.0.0.1',port:3923,path:'/?ls',method:'GET',headers},up=>{const chunks=[];up.on('data',c=>chunks.push(c));up.on('end',()=>{try{resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));}catch{reject(Error('invalid session'));}});});check.on('error',reject);check.end();});}
function backendLogin(username,password,secure){return new Promise((resolve,reject)=>{const boundary='----Yizuka'+Date.now().toString(36);const field=(name,value)=>`--${boundary}\r\nContent-Disposition: form-data; name="${name}"\r\n\r\n${value}\r\n`;const body=Buffer.from(field('act','login')+field('uname',username)+field('cppwd',password)+`--${boundary}--\r\n`);const login=http.request({hostname:'127.0.0.1',port:3923,path:'/',method:'POST',headers:{'content-type':`multipart/form-data; boundary=${boundary}`,'content-length':body.length,'x-forwarded-for':'127.0.0.1','x-forwarded-proto':secure?'https':'http'}},up=>{const chunks=[];up.on('data',c=>chunks.push(c));up.on('end',()=>{const text=Buffer.concat(chunks).toString('utf8');resolve({ok:up.statusCode===200&&text.includes('hi '+username),cookies:up.headers['set-cookie']||[]});});});login.on('error',reject);login.end(body);});}
http.createServer(async(req,res)=>{
  const url=new URL(req.url,'http://localhost');
  if (!allowedHosts.has(req.headers.host)) {res.writeHead(403);return res.end('Invalid host');}
  if (!['GET','HEAD','OPTIONS'].includes(req.method) && req.headers.origin && req.headers.origin!==`https://${req.headers.host}` && req.headers.origin!==`http://${req.headers.host}`) {res.writeHead(403);return res.end('Invalid origin');}
  if (url.pathname==='/_ui/login' && req.method==='POST') {
    try {
      const body=JSON.parse((await readBody(req)).toString('utf8'));const username=typeof body.username==='string'?body.username.trim():'';const password=typeof body.password==='string'?body.password.trim():'';
      if(username.length<1||username.length>64||password.length<1||password.length>64){res.writeHead(400);return res.end('{"ok":false}');}
      const result=await backendLogin(username,password,isSecureHost(req.headers.host));if(!result.ok){res.writeHead(401,{'Content-Type':'application/json','Cache-Control':'no-store'});return res.end('{"ok":false}');}
      res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store','Set-Cookie':result.cookies,'X-Content-Type-Options':'nosniff'});return res.end('{"ok":true}');
    } catch {res.writeHead(500);return res.end('{"ok":false}');}
  }
  if (url.pathname==='/_ui/save-password' && req.method==='POST') {
    try {
      const session=await sessionInfo(req);
      if(session.acct!=='admin'||!session.perms?.includes('read')){res.writeHead(403);return res.end('Not authenticated');}
      const body=JSON.parse((await readBody(req)).toString('utf8'));
      const next=typeof body.newPassword==='string'?body.newPassword:'';
      if(next.length<10||next.length>64||next!==next.trim()||/[\r\n]/.test(next)){res.writeHead(400);return res.end('Invalid password');}
      const infoUrl=new URL('./登录信息.txt',import.meta.url);const current=await readFile(infoUrl,'utf8');
      if(!/^密码：.*$/m.test(current))throw Error('password record missing');
      await writeFile(infoUrl,current.replace(/^密码：.*$/m,'密码：'+next),'utf8');
      res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});return res.end('{"ok":true}');
    } catch {res.writeHead(500);return res.end('Unable to update password record');}
  }
  if (assets[url.pathname] && !url.search && ['GET','HEAD'].includes(req.method)) {
    try {
      const file=assets[url.pathname];const data=await readFile(new URL('./ui/'+file,import.meta.url));
      res.writeHead(200,{'Content-Type':types[file.split('.').pop()],'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Referrer-Policy':'same-origin','Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'"});
      return res.end(req.method==='HEAD'?undefined:data);
    } catch {res.writeHead(500);return res.end('UI unavailable');}
  }
  const headers={...req.headers};
  for(const key of Object.keys(headers)) if(key.startsWith('x-forwarded-') || key.startsWith('cf-') || key==='forwarded') delete headers[key];
  const secure=isSecureHost(req.headers.host);headers['x-forwarded-for']='127.0.0.1';headers['x-forwarded-proto']=secure?'https':'http';
  if(secure&&headers.cookie)headers.cookie=bridgeSecureCookie(headers.cookie);
  const upstream=http.request({hostname:'127.0.0.1',port:3923,path:req.url,method:req.method,headers},r=>{
    if(r.statusCode>=400){const cookieNames=(req.headers.cookie||'').split(';').map(x=>x.split('=')[0].trim()).filter(Boolean).join(',')||'-';console.error(`[proxy] ${req.method} ${req.url} -> ${r.statusCode}; cookies=${cookieNames}; origin=${req.headers.origin||'-'}; host=${req.headers.host||'-'}`);}
    const thumbnail=req.method==='GET'&&url.searchParams.has('th')&&r.statusCode===200&&!String(r.headers['content-type']||'').toLowerCase().startsWith('image/svg+xml');
    const h={...r.headers,'cache-control':thumbnail?'private, max-age=86400':'no-store','x-content-type-options':'nosniff'};
    if(thumbnail) h.vary='Cookie, Authorization';
    if (/\.(html?|svg|xml)(?:\?|$)/i.test(req.url)) {h['content-disposition']='attachment'; h['content-security-policy']="sandbox";}
    res.writeHead(r.statusCode,h);r.pipe(res);
  });
  upstream.on('error',()=>{if(!res.headersSent)res.writeHead(502);res.end('File service unavailable');});
  req.on('aborted',()=>upstream.destroy());req.pipe(upstream);
}).listen(3924,'127.0.0.1',()=>console.log('Fluent cloud UI listening on 127.0.0.1:3924'));
