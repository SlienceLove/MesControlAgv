import fs from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {createRequire} from 'node:module';
const require=createRequire(import.meta.url);
export const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
export async function readGlb(file) {
  const bytes=await fs.readFile(file);
  if(bytes.readUInt32LE(0)!==0x46546c67||bytes.readUInt32LE(4)!==2||bytes.readUInt32LE(8)!==bytes.length)throw Error('Invalid GLB');
  const length=bytes.readUInt32LE(12);
  return {bytes,doc:JSON.parse(bytes.subarray(20,20+length)),bin:bytes.subarray(28+length)};
}
let decoderModule;
export async function decodePrimitive(glb,primitive) {
  decoderModule??=await require('draco3d').createDecoderModule({});
  const d=decoderModule,decoder=new d.Decoder(),mesh=new d.Mesh(),buffer=new d.DecoderBuffer();
  const extension=primitive.extensions.KHR_draco_mesh_compression,view=glb.doc.bufferViews[extension.bufferView];
  const bytes=glb.bin.subarray(view.byteOffset||0,(view.byteOffset||0)+view.byteLength);
  try {
    buffer.Init(bytes,bytes.length);const status=decoder.DecodeBufferToMesh(buffer,mesh);
    try{if(!status.ok())throw Error(status.error_msg());}finally{d.destroy(status);}
    const count=mesh.num_points(),triangles=mesh.num_faces();
    const attributes={};
    for(const name of ['POSITION','NORMAL']) {
      const attr=decoder.GetAttributeByUniqueId(mesh,extension.attributes[name]);
      const ptr=d._malloc(count*12);
      try{if(!decoder.GetAttributeDataArrayForAllPoints(mesh,attr,d.DT_FLOAT32,count*12,ptr))throw Error('Attribute decode failed');
        attributes[name]=new Float32Array(d.HEAPF32.buffer,ptr,count*3).slice();}finally{d._free(ptr);}
    }
    const ptr=d._malloc(triangles*12);let indices;
    try{if(!decoder.GetTrianglesUInt32Array(mesh,triangles*12,ptr))throw Error('Index decode failed');
      indices=new Uint32Array(d.HEAPU32.buffer,ptr,triangles*3).slice();}finally{d._free(ptr);}
    return {positions:attributes.POSITION,normals:attributes.NORMAL,indices};
  }finally{d.destroy(buffer);d.destroy(mesh);d.destroy(decoder);}
}
export function bounds(positions,indices) {
  const min=[Infinity,Infinity,Infinity],max=[-Infinity,-Infinity,-Infinity];
  for(const v of indices)for(let a=0;a<3;a++){const p=positions[v*3+a];min[a]=Math.min(min[a],p);max[a]=Math.max(max[a],p);}
  return {min,max};
}
export function components(mesh,translation=[0,0,0]) {
  const {positions:p,indices}=mesh,n=p.length/3,parent=Int32Array.from({length:n},(_,i)=>i),vertices=new Map();
  function find(i){while(parent[i]!==i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
  function join(a,b){a=find(a);b=find(b);if(a!==b)parent[b]=a;}
  for(let i=0;i<n;i++){const key=`${p[i*3]},${p[i*3+1]},${p[i*3+2]}`;if(vertices.has(key))join(i,vertices.get(key));else vertices.set(key,i);}
  for(let i=0;i<indices.length;i+=3){join(indices[i],indices[i+1]);join(indices[i],indices[i+2]);}
  const groups=new Map();
  for(let i=0;i<indices.length;i+=3){const id=find(indices[i]);let g=groups.get(id);if(!g){g={id,triangles:0,min:[Infinity,Infinity,Infinity],max:[-Infinity,-Infinity,-Infinity],faces:[]};groups.set(id,g);}
    g.triangles++;g.faces.push(i/3);for(let k=0;k<3;k++)for(let a=0;a<3;a++){const v=p[indices[i+k]*3+a]+translation[a];g.min[a]=Math.min(g.min[a],v);g.max[a]=Math.max(g.max[a],v);}}
  return [...groups.values()];
}
export function meshSubset(mesh,faces) {
  const remap=new Int32Array(mesh.positions.length/3).fill(-1),vertices=[];
  const indices=new Uint32Array(faces.length*3);
  for(let i=0;i<faces.length;i++)for(let k=0;k<3;k++){
    const source=mesh.indices[faces[i]*3+k];
    if(remap[source]<0){remap[source]=vertices.length;vertices.push(source);}indices[i*3+k]=remap[source];
  }
  const positions=new Float32Array(vertices.length*3),normals=new Float32Array(vertices.length*3);
  for(let i=0;i<vertices.length;i++)for(let a=0;a<3;a++){
    positions[i*3+a]=mesh.positions[vertices[i]*3+a];normals[i*3+a]=mesh.normals[vertices[i]*3+a];
  }
  return {positions,normals,indices};
}
export function geometryHash(mesh) {
  // Canonical oriented triangle multiset, including each corner's exact normals.
  // Draco may reorder/weld indices; cyclic corner rotation is equivalent, reversal is not.
  const keys=[],corner=Array.from({length:3},()=>Buffer.allocUnsafe(24)),triangle=Buffer.allocUnsafe(72);
  for(let i=0;i<mesh.indices.length;i+=3){
    for(let k=0;k<3;k++)for(let a=0;a<3;a++){
      const v=mesh.indices[i+k]*3+a;corner[k].writeFloatLE(mesh.positions[v],a*4);corner[k].writeFloatLE(mesh.normals[v],12+a*4);
    }
    let first=0;for(let k=1;k<3;k++){
      for(let c=0;c<3;c++){const comparison=Buffer.compare(corner[(k+c)%3],corner[(first+c)%3]);if(comparison<0){first=k;break;}if(comparison>0)break;}
    }
    for(let k=0;k<3;k++)corner[(first+k)%3].copy(triangle,k*24);
    keys.push(sha(triangle));
  }
  const hash=createHash('sha256');for(const key of keys.sort())hash.update(key);return hash.digest('hex');
}
export async function encodeLossless(mesh) {
  const d=await require('draco3d').createEncoderModule({});
  const builder=new d.MeshBuilder(),target=new d.Mesh(),encoder=new d.Encoder(),data=new d.DracoInt8Array();
  try{
    if(!builder.AddFacesToMesh(target,mesh.indices.length/3,mesh.indices))throw Error('Cannot encode faces');
    const attributes={POSITION:builder.AddFloatAttributeToMesh(target,d.POSITION,mesh.positions.length/3,3,mesh.positions),
      NORMAL:builder.AddFloatAttributeToMesh(target,d.NORMAL,mesh.normals.length/3,3,mesh.normals)};
    encoder.SetSpeedOptions(5,5);
    // Sequential encoding also retains existing degenerate faces; Edgebreaker
    // would silently drop them, violating this edit's exact retained-face proof.
    encoder.SetEncodingMethod(d.MESH_SEQUENTIAL_ENCODING);
    encoder.SetAttributeQuantization(d.POSITION,0);encoder.SetAttributeQuantization(d.NORMAL,0);
    const size=encoder.EncodeMeshToDracoBuffer(target,data);if(size<=0)throw Error('Encoding failed');
    const bytes=Buffer.alloc(size);for(let i=0;i<size;i++)bytes[i]=data.GetValue(i);
    return {bytes,attributes};
  }finally{d.destroy(data);d.destroy(encoder);d.destroy(target);d.destroy(builder);}
}
export function glbBytes(doc,bin) {
  let json=Buffer.from(JSON.stringify(doc));json=Buffer.concat([json,Buffer.alloc((4-json.length%4)%4,32)]);
  bin=Buffer.concat([bin,Buffer.alloc((4-bin.length%4)%4)]);
  const header=Buffer.alloc(20),binaryHeader=Buffer.alloc(8);
  header.writeUInt32LE(0x46546c67);header.writeUInt32LE(2,4);header.writeUInt32LE(28+json.length+bin.length,8);
  header.writeUInt32LE(json.length,12);header.writeUInt32LE(0x4e4f534a,16);
  binaryHeader.writeUInt32LE(bin.length);binaryHeader.writeUInt32LE(0x004e4942,4);
  return Buffer.concat([header,json,binaryHeader,bin]);
}
