import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {readGlb,decodePrimitive,geometryHash,components,sha} from '../../scripts/digital-twin/model-tools/glb-mesh.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const model=await readGlb(path.join(root,'src/MesControlAgv.Wpf/DigitalTwin/Web/lab606.glb'));
test('Cleaned GLB has exact reviewed identity and a complete retained scene',()=>{
  assert.equal(sha(model.bytes),'acb34c95e5c53c713d0a4c5246f90bb179c90ae9371626307621522c24df0e5a');
  assert.equal(model.doc.meshes.reduce((n,m)=>n+model.doc.accessors[m.primitives[0].indices].count/3,0),1098558);
  assert.deepEqual(model.doc.nodes.map(n=>n.name),['environment','agv-composite-a','d160-a','autosampler-a','decapper-a']);
  assert.deepEqual(model.doc.nodes[1].translation,[16.58011245727539,-.02500000037252903,-4.67792820930481]);
  assert.deepEqual(model.doc.nodes[4].translation,[18.17017364501953,.8398727178573608,-6.623072624206543]);
});
test('Live robot and both instruments retain their original compressed geometry bytes',()=>{
  const expected=['735cf2da035265294819f8513f812bcbcf0795604f0c725886f4813b753e353d',
    'f1e4d0fba8fc05231e339674d5ab8c9d0cc2fa901d41ea72802a727972f5916e',
    '1946817484c497d6d8e1d86526226b494302e02b0a5037889de13937846c49dd'];
  for(let i=1;i<=3;i++){const p=model.doc.meshes[model.doc.nodes[i].mesh].primitives[0];
    const v=model.doc.bufferViews[p.extensions.KHR_draco_mesh_compression.bufferView];
    assert.equal(sha(model.bin.subarray(v.byteOffset,v.byteOffset+v.byteLength)),expected[i-1]);}
});
test('Static robot removed without changing any retained environment triangle or normal',async()=>{
  const mesh=await decodePrimitive(model,model.doc.meshes[0].primitives[0]);
  assert.equal(geometryHash(mesh),'262569efe7085e4b53b0bd8feb0ca7dab4f6d6a97ba5dd7f569d5e712a34f50d');
  for(let i=0;i<mesh.positions.length;i+=3){
    const [x,y,z]=mesh.positions.subarray(i,i+3);
    assert.ok(!(x>=15.5&&x<=16.45&&y>=-.04&&y<=1.5&&z>=-9.15&&z<=-8.05),'Unexpected geometry remains in redundant robot region');
  }
});
test('Only the reviewed box component is absent; remaining workstation geometry is exact',async()=>{
  const n=model.doc.nodes.find(n=>n.name==='decapper-a'),mesh=await decodePrimitive(model,model.doc.meshes[n.mesh].primitives[0]);
  assert.equal(geometryHash(mesh),'2ec54016c8930a9075198388f0e18c44e745f94ce7cb5a80fd27ae17eaf3f39f');
  assert.equal(mesh.indices.length/3,192066);
  assert.ok(!components(mesh,n.translation).some(g=>g.triangles===288&&Math.abs(g.min[0]-17.810136795043945)<1e-7));
});
