import test from 'node:test';
import assert from 'node:assert/strict';
import * as THREE from '../../src/MesControlAgv.Wpf/DigitalTwin/Web/vendor/three/build/three.module.min.js';
import {frameCamera,topUp,overviewDirection} from '../../src/MesControlAgv.Wpf/DigitalTwin/Web/camera-framing.mjs';

function setup(top,aspect=2) {
  const camera=top?new THREE.OrthographicCamera():new THREE.PerspectiveCamera(30,aspect,.005,1000);
  camera.aspect=aspect;camera.up.copy(top?topUp:new THREE.Vector3(0,1,0));
  const controls={target:new THREE.Vector3(),update(){camera.lookAt(this.target);camera.updateMatrixWorld(true);}};
  const model=new THREE.Mesh(new THREE.BoxGeometry(5.5,1.9,7),new THREE.MeshBasicMaterial());
  model.position.set(17.3,.95,-7.15);
  const ground=new THREE.Box3(new THREE.Vector3(8,-.04,-16),new THREE.Vector3(28,-.04,4));
  return {camera,controls,model,ground};
}
function assertInFrame(camera,box) {
  for(const x of [box.min.x,box.max.x])for(const y of [box.min.y,box.max.y])for(const z of [box.min.z,box.max.z]){
    const p=new THREE.Vector3(x,y,z).project(camera);
    assert.ok(Math.abs(p.x)<1 && Math.abs(p.y)<1 && Math.abs(p.z)<1,JSON.stringify(p));
  }
}
test('overview makes CAD long Z axis horizontal with no world transform',()=>{
  const {camera,controls,model,ground}=setup(false);
  const original=model.position.clone();
  frameCamera(camera,controls,model,[ground]);
  assert.ok(camera.position.clone().sub(controls.target).normalize().distanceTo(overviewDirection)<1e-12);
  const a=new THREE.Vector3(17,0,-8).project(camera),b=new THREE.Vector3(17,0,-4).project(camera);
  assert.ok(Math.abs(a.y-b.y)<1e-12);
  assert.ok(model.position.equals(original));assertInFrame(camera,ground);
});
test('top is orthographic and includes both model and operating bounds at wide and narrow aspects',()=>{
  for(const aspect of [.5,1,3.5]){
    const {camera,controls,model,ground}=setup(true,aspect);
    frameCamera(camera,controls,model,[ground]);
    assertInFrame(camera,ground);assertInFrame(camera,new THREE.Box3().setFromObject(model));
    const floor=new THREE.Vector3(17,0,-8).project(camera),raised=new THREE.Vector3(17,1.9,-8).project(camera);
    assert.ok(Math.abs(floor.x-raised.x)<1e-12 && Math.abs(floor.y-raised.y)<1e-12);
    assert.ok(camera.position.clone().sub(controls.target).normalize().distanceTo(new THREE.Vector3(0,1,0))<1e-12);
  }
});
test('refit after resize or pose snap preserves chosen camera direction and projection',()=>{
  for(const top of [false,true]){
    const {camera,controls,model}=setup(top);
    frameCamera(camera,controls,model);
    if(!top){camera.position.copy(controls.target).add(new THREE.Vector3(4,5,6));controls.update();}
    const direction=camera.position.clone().sub(controls.target).normalize();
    model.position.x+=2;camera.aspect=.7;
    frameCamera(camera,controls,model,[],true);
    assert.ok(camera.position.clone().sub(controls.target).normalize().distanceTo(direction)<1e-12);
    assert.equal(!!camera.isOrthographicCamera,top);
    assertInFrame(camera,new THREE.Box3().setFromObject(model));
  }
});
