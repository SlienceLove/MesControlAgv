import {readGlb,decodePrimitive,components} from './glb-mesh.mjs';
import fs from 'node:fs/promises';
import path from 'node:path';
const glb=await readGlb(process.argv[2]);
const report=[];
for(const name of ['environment','decapper-a']){
  const node=glb.doc.nodes.find(n=>n.name===name),mesh=await decodePrimitive(glb,glb.doc.meshes[node.mesh].primitives[0]);
  const list=components(mesh,node.translation);
  const relevant=list.filter(g=>name==='decapper-a'||g.min[0]>15.45&&g.max[0]<16.5&&g.min[2]>-9.3&&g.max[2]<-8||g.min[0]>17&&g.max[0]<18.65&&g.min[1]>.82&&g.max[1]<1.2&&g.min[2]>-7.1&&g.max[2]<-6.1);
  const entry={name,components:list.length,selected:relevant.length,groups:relevant.map(({faces,...g})=>g).sort((a,b)=>b.triangles-a.triangles)};
  report.push(entry);
  console.log(JSON.stringify({...entry,groups:entry.groups.filter(g=>g.triangles>10000||g.triangles<=200&&g.max[0]-g.min[0]>.1&&g.max[2]-g.min[2]>.1)},null,2));
}
if(process.argv[3]){await fs.mkdir(path.dirname(process.argv[3]),{recursive:true});await fs.writeFile(process.argv[3],JSON.stringify(report,null,2),{flag:'wx'});}
