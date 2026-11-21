const https = require('https');
const fs = require('fs');
function get(path){return new Promise((ok,no)=>{https.get({host:'api.github.com',path,headers:{'User-Agent':'iconbox-sync','Accept':'application/vnd.github+json','Authorization':'Bearer '+process.env.GH_TOKEN}},r=>{let d='';r.on('data',c=>d+=c);r.on('end',()=>ok({status:r.statusCode,body:d}));}).on('error',no);});}
(async()=>{
  const out=[];
  const r=await get('/repos/FlowerSea111222/IconBox/git/trees/main?recursive=1');
  const j=JSON.parse(r.body);
  fs.writeFileSync(process.argv[2], JSON.stringify({truncated:j.truncated,count:(j.tree||[]).length,entries:(j.tree||[]).map(e=>({p:e.path,s:e.sha,t:e.type}))}));
  console.log('status',r.statusCode,'count',(j.tree||[]).length,'truncated',j.truncated);
})();