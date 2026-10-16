import fs from 'node:fs/promises';
import path from 'node:path';
import {readGlb,decodePrimitive,components,meshSubset,bounds,geometryHash,encodeLossless,glbBytes,sha} from './glb-mesh.mjs';

const [source,output]=process.argv.slice(2);
if(!source||!output||path.resolve(source)===path.resolve(output))throw Error('Usage: node remove-obsolete-models.mjs original.glb separate-output.glb');
const input=await readGlb(source),originalHash='96ca7f34c189c9c1add62c29441a7a9b16e67619c96659c259dca7ea8412671a';
if(sha(input.bytes)!==originalHash)throw Error('Unexpected source model; spatial review is tied to this exact asset');
const doc=structuredClone(input.doc),replacements=new Map(),changes=[],removedMeshes=[];
for(const name of ['environment','decapper-a']){
  const node=doc.nodes.find(n=>n.name===name),primitive=doc.meshes[node.mesh].primitives[0];
  if(doc.meshes[node.mesh].primitives.length!==1)throw Error('Expected single primitive');
  const mesh=await decodePrimitive(input,primitive),removed=[];let crossing=0;
  if(name==='environment'){
    const lo=[15.5,-.04,-9.15],hi=[16.45,1.5,-8.05];
    for(let f=0;f<mesh.indices.length/3;f++){
      const indices=mesh.indices.subarray(f*3,f*3+3),box=bounds(mesh.positions,indices);
      const inside=box.min.every((v,a)=>v>=lo[a])&&box.max.every((v,a)=>v<=hi[a]);
      const overlaps=box.min.every((v,a)=>v<=hi[a])&&box.max.every((v,a)=>v>=lo[a]);
      if(inside)removed.push(f);else if(overlaps)crossing++;
    }
    if(crossing!==0||removed.length<150000||removed.length>250000)throw Error(`Unsafe robot selection: ${removed.length} selected, ${crossing} straddling`);
  }else{
    // Exact connected component of the front box, not a crop through the body.
    const expectedMin=[17.810136795043945,.8400686534150736,-6.543854653835297];
    const expectedMax=[18.173513740301132,.9198497608304024,-6.287953197956085];
    const matches=components(mesh,node.translation).filter(g=>g.triangles===288&&
      g.min.every((v,a)=>Math.abs(v-expectedMin[a])<1e-7)&&g.max.every((v,a)=>Math.abs(v-expectedMax[a])<1e-7));
    if(matches.length!==1)throw Error('Front box connected component is not uniquely identified');
    removed.push(...matches[0].faces);
  }
  const mask=new Uint8Array(mesh.indices.length/3);for(const f of removed)mask[f]=1;
  const kept=[];for(let f=0;f<mask.length;f++)if(!mask[f])kept.push(f);
  const remaining=meshSubset(mesh,kept),deleted=meshSubset(mesh,removed);
  console.log(JSON.stringify({name,removed:removed.length,remaining:kept.length,crossing,deletedBounds:bounds(deleted.positions,deleted.indices)}));
  const hash=geometryHash(remaining),packed=await encodeLossless(remaining);
  const bv=primitive.extensions.KHR_draco_mesh_compression.bufferView;
  replacements.set(bv,packed.bytes);primitive.extensions.KHR_draco_mesh_compression.attributes=packed.attributes;
  const local=bounds(remaining.positions,remaining.indices);
  doc.accessors[primitive.attributes.POSITION].min=local.min;doc.accessors[primitive.attributes.POSITION].max=local.max;
  doc.accessors[primitive.indices].count=remaining.indices.length;
  const roundtrip=await decodePrimitive({doc:{bufferViews:[{byteOffset:0,byteLength:packed.bytes.length}]},bin:packed.bytes},
    {extensions:{KHR_draco_mesh_compression:{bufferView:0,attributes:packed.attributes}}});
  if(geometryHash(roundtrip)!==hash)throw Error(`Remaining position/normal triangles changed during compression: ${remaining.indices.length/3} -> ${roundtrip.indices.length/3}; bounds ${JSON.stringify(bounds(roundtrip.positions,roundtrip.indices))}`);
  doc.accessors[primitive.attributes.POSITION].count=roundtrip.positions.length/3;
  doc.accessors[primitive.attributes.NORMAL].count=roundtrip.normals.length/3;
  changes.push({node:name,removedTriangles:removed.length,remainingTriangles:kept.length,boundaryCrossings:crossing,
    removedLocalBounds:bounds(deleted.positions,deleted.indices),nodeTranslation:node.translation,
    retainedOrientedGeometrySha256:hash,losslessRoundtrip:true});
  removedMeshes.push({name,node,mesh:deleted});
}
let length=0;const parts=[],unchanged=[];
for(let i=0;i<doc.bufferViews.length;i++){
  const original=input.doc.bufferViews[i],old=input.bin.subarray(original.byteOffset||0,(original.byteOffset||0)+original.byteLength);
  const bytes=replacements.get(i)??old;
  if(!replacements.has(i))unchanged.push({bufferView:i,sha256:sha(bytes),bytes:bytes.length});
  doc.bufferViews[i].byteOffset=length;doc.bufferViews[i].byteLength=bytes.length;
  const padding=Buffer.alloc((4-bytes.length%4)%4);parts.push(bytes,padding);length+=bytes.length+padding.length;
}
doc.buffers[0].byteLength=length;
doc.asset.extras={...doc.asset.extras,cleanup:{sourceSha256:originalHash,removed:['static-rear-composite-robot','obsolete-front-workstation-box'],geometryTransformUnchanged:true}};
const result=glbBytes(doc,Buffer.concat(parts));
await fs.mkdir(path.dirname(output),{recursive:true});
await fs.writeFile(output,result,{flag:'wx'});
const report={sourceSha256:originalHash,outputSha256:sha(result),sourceBytes:input.bytes.length,outputBytes:result.length,
  trianglesBefore:input.doc.meshes.reduce((n,m)=>n+input.doc.accessors[m.primitives[0].indices].count/3,0),
  trianglesAfter:doc.meshes.reduce((n,m)=>n+doc.accessors[m.primitives[0].indices].count/3,0),
  changes,untouchedCompressedStreams:unchanged,nodesUnchanged:JSON.stringify(doc.nodes)===JSON.stringify(input.doc.nodes),
  materialsUnchanged:JSON.stringify(doc.materials)===JSON.stringify(input.doc.materials)};
await fs.writeFile(output+'.report.json',JSON.stringify(report,null,2),{flag:'wx'});
// Inspection-only GLB: the same scene, with only the chosen objects highlighted.
const highlight=structuredClone(input.doc),highlightParts=[input.bin];let offset=input.bin.length;
highlight.materials.push({name:'REMOVAL_REVIEW',pbrMetallicRoughness:{baseColorFactor:[1,.03,.03,1],metallicFactor:0,roughnessFactor:.85},doubleSided:true});
for(const item of removedMeshes){
  const attrs={};
  for(const [name,data,type,component] of [['POSITION',item.mesh.positions,'VEC3',5126],['NORMAL',item.mesh.normals,'VEC3',5126],['INDICES',item.mesh.indices,'SCALAR',5125]]){
    const bytes=Buffer.from(data.buffer,data.byteOffset,data.byteLength),view=highlight.bufferViews.length;
    highlight.bufferViews.push({buffer:0,byteOffset:offset,byteLength:bytes.length});highlightParts.push(bytes);offset+=bytes.length;
    attrs[name]=highlight.accessors.length;const accessor={bufferView:view,componentType:component,count:data.length/(type==='VEC3'?3:1),type};
    if(name==='POSITION')Object.assign(accessor,bounds(data,item.mesh.indices));highlight.accessors.push(accessor);
  }
  highlight.meshes[item.node.mesh].primitives.push({attributes:{POSITION:attrs.POSITION,NORMAL:attrs.NORMAL},indices:attrs.INDICES,material:highlight.materials.length-1});
}
highlight.buffers[0].byteLength=offset;
await fs.writeFile(output+'.highlight.glb',glbBytes(highlight,Buffer.concat(highlightParts)),{flag:'wx'});
console.log(JSON.stringify(report,null,2));
