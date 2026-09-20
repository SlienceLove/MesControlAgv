import {test} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {validateMap,projectMap,validTransform,clipLine,curvePoints,MAP_MD5} from '../../src/MesControlAgv.Wpf/DigitalTwin/Web/map-ground-data.mjs';
const data=JSON.parse(fs.readFileSync(new URL('../../src/MesControlAgv.Wpf/DigitalTwin/Web/map-ground.json',import.meta.url)));
test('map fingerprint, metres, six exact station coordinates, and route sources',()=>{
  validateMap(data);assert.equal(data.md5,MAP_MD5);assert.equal(data.stations.length,6);assert.equal(data.routes.length,10);
  assert.deepEqual(data.stations.find(s=>s.id==='LM1').point,[0,.8]);
  assert.deepEqual(data.stations.find(s=>s.id==='LM7').point,[5.344,-1.174]);
  assert.deepEqual(data.stations.find(s=>s.id==='LM2').point,[6.148,-1.304]);
  for(const r of data.routes){assert.deepEqual(r.points[0],data.stations.find(s=>s.id===r.from).point);assert.deepEqual(r.points[3],data.stations.find(s=>s.id===r.to).point);}
});
test('wrong identity, malformed data, and unbounded workspace rejected',()=>{
  for(const d of [{...data,md5:'other'},{...data,unit:'mm'},{...data,bounds:[0,0,10000,1]},
    {...data,stations:[]},{...data,lines:[[NaN,0,0,1]]},{...data,scanPoints:[[Infinity,0]]}])assert.throws(()=>validateMap(d));
});
test('only finite, matching explicit transforms allowed',()=>{
  assert.equal(validTransform({md5:MAP_MD5,theta:.4,tx:16,tz:-4,floor:-.025}),true);
  assert.equal(validTransform(null),false);assert.equal(validTransform({md5:MAP_MD5,theta:NaN,tx:16,tz:-4,floor:0}),false);
});
test('projection matches C# rigid fit convention with Y up',()=>{
  const p=projectMap([2,3],{theta:.4,tx:16,tz:-4});
  assert.ok(Math.abs(p[0]-(Math.cos(.4)*2+Math.sin(.4)*3+16))<1e-10);
  assert.ok(Math.abs(p[1]-(Math.sin(.4)*2-Math.cos(.4)*3-4))<1e-10);
});
test('features outside display rectangle clipped, not clamped into fake walls',()=>{
  assert.deepEqual(clipLine([-2,0,2,0],[-1,-1,1,1]),[-1,0,1,0]);
  assert.equal(clipLine([-2,2,2,2],[-1,-1,1,1]),null);
  assert.deepEqual(clipLine([0,-2,0,2],[-1,-1,1,1]),[0,-1,0,1]);
});
test('curves preserve their start and end; finite samples',()=>{
  for(const r of data.routes){const samples=curvePoints(r.points);assert.equal(samples.length,33);
    assert.deepEqual(samples[0],r.points[0]);assert.deepEqual(samples.at(-1),r.points[3]);
    assert.ok(samples.every(p=>p.every(Number.isFinite)));}
});
import {referenceVisibility} from '../../src/MesControlAgv.Wpf/DigitalTwin/Web/map-ground-data.mjs';
test('map reference is the master gate; route toggles cannot leak hidden overlays',()=>{
  for(const groundEnabled of [false,true])for(const referenceEnabled of [false,true])
  for(const hasTransform of [false,true])for(const routesEnabled of [false,true]){
    const v=referenceVisibility({groundEnabled,referenceEnabled,hasTransform,routesEnabled});
    assert.equal(v.reference,referenceEnabled);
    assert.equal(v.overlay,groundEnabled&&referenceEnabled&&hasTransform);
    assert.equal(v.stations,v.overlay);
    assert.equal(v.routes,v.overlay&&routesEnabled);
    assert.equal(v.mapRoutes,referenceEnabled&&routesEnabled);
  }
});
