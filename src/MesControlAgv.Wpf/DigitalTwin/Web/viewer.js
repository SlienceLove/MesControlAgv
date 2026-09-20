import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { DRACOLoader } from 'three/addons/loaders/DRACOLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { PoseMotion, samePose } from './pose-motion.mjs';
import { createMapGround } from './map-ground.js';
import { frameCamera } from './camera-framing.mjs';

const ids = new Set(['agv-composite-a', 'd160-a', 'autosampler-a', 'decapper-a']);
const bridge = window.chrome?.webview;
// Native WebView keyboard focus may not bubble to the WPF window. These
// strictly presentation-only events also allow Esc to leave host fullscreen.
window.addEventListener('keydown', event => {
  if (event.repeat || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
  if (event.key === 'F11' || event.key === 'Escape') {
    event.preventDefault();
    post(event.key === 'F11' ? 'fullscreen-toggle' : 'fullscreen-exit');
  }
});
const notice = document.getElementById('notice');
const state = { ready: false, selectedId: null, error: null, disposed: false };
let renderer, controls, decoder, scene, camera, model, bounds, frame, mapGround;
let modelTriangles=0;
let overviewIncludesGround=false;
let fitTarget=null;
const objects = new Map(), baseColors = new Map();
const labels = new Map();
const tones = new Set(['unknown', 'normal', 'running', 'warning', 'fault']);
const motion = new PoseMotion();
let originalPose = null, pick = null;
let renderedFrames=0;
const markers = new Map();
function stopPose() { motion.stop(); }
function applyPose(object,p) {
  object.position.set(p.x,p.y,p.z);object.rotation.set(0,p.yaw,0);object.updateMatrixWorld(true);
}
function setPose(p) {
  const object = objects.get('agv-composite-a');
  if (!object || document.hidden) { stopPose(); return; }
  const before={x:object.position.x,y:object.position.y,z:object.position.z,yaw:object.rotation.y};
  const current=motion.push(p,performance.now(),before);
  if(!current)return;
  applyPose(object,current);
  if(p.snap===true) {
    fit(fitTarget ?? model,overviewIncludesGround,true);scheduleRender();return;
  }
  if(motion.active||!samePose(before,current))scheduleRender();
}
function advancePose() {
  if (!motion.active) return;
  const p=motion.sample(performance.now());
  const object = objects.get('agv-composite-a');
  if(p)applyPose(object,p);
  if(motion.active)scheduleRender();
}
function updateLabels() {
  mapGround?.updateLabels(camera);
  for (const [id, label] of labels) {
    const object = objects.get(id);
    const p = label.anchor.clone().applyMatrix4(object.matrixWorld).project(camera);
    label.element.hidden = !object.visible || p.z < -1 || p.z > 1 || Math.abs(p.x) > 1 || Math.abs(p.y) > 1;
    label.element.style.left = `${(p.x + 1) * innerWidth / 2}px`;
    label.element.style.top = `${(1 - p.y) * innerHeight / 2}px`;
  }
}
function telemetry(message) {
  if (typeof message.source === 'string') document.getElementById('source').textContent = message.source.slice(0, 160);
  if (!Array.isArray(message.devices)) return;
  for (const item of message.devices.slice(0, 2)) {
    const label = labels.get(item?.id);
    if (!label || !tones.has(item.tone) || typeof item.text !== 'string') continue;
    label.element.dataset.tone = item.tone;
    label.text.textContent = item.text.slice(0, 200);
  }
  scheduleRender();
}
function post(type, id) { bridge?.postMessage({ type, ...(type === 'selected' ? { id } : {}) }); }
function fail(error) {
  state.ready = false; state.error = String(error);
  notice.hidden = false; notice.textContent = '场景加载失败，请点击“重新加载”。';
  post('load-error');
}
function render() {
  if (!state.disposed && renderer && camera) { advancePose(); renderer.render(scene, camera); updateLabels();renderedFrames++; }
}
function scheduleRender() {
  if (frame || state.disposed || document.hidden) return;
  frame = requestAnimationFrame(() => { frame = null; render(); });
}
function useView() {
  controls?.dispose();
  const aspect=Math.max(innerWidth,1)/Math.max(innerHeight,1);
  camera=new THREE.PerspectiveCamera(30,aspect,.005,1000);
  camera.aspect=aspect;
  camera.up.set(0,1,0);
  controls=new OrbitControls(camera,renderer.domElement);
  controls.minDistance=.15;controls.maxDistance=100;controls.minZoom=.1;controls.maxZoom=100;
  controls.addEventListener('change',scheduleRender);
  document.getElementById('hint').textContent='摆正全景 · 拖动旋转 · 滚轮缩放 · 点击设备选择 · 橙色仅表示选中';
}
function fit(targetObject = fitTarget ?? model, includeGround = overviewIncludesGround, preserveDirection = true) {
  if (!model) return;
  fitTarget=targetObject;
  if(targetObject===model)overviewIncludesGround=includeGround;
  const groundBounds=targetObject===model&&includeGround&&mapGround?.inspect().enabled?mapGround.getBounds():null;
  const operatingBounds=targetObject===model?mapGround?.getOperatingBounds():null;
  frameCamera(camera,controls,targetObject,[groundBounds,operatingBounds].filter(Boolean),preserveDirection);
  scheduleRender();
}
function select(id) {
  if (id !== null && !ids.has(id)) return;
  state.selectedId=id;
  for (const [name,object] of objects) object.traverse(child => {
    if(child.isMesh)child.material.color.copy(name===id?new THREE.Color(0xf4b54a):baseColors.get(child));
  });
  scheduleRender(); post('selected',id);
}
function command(message) {
  if (!state.ready || !message || typeof message !== 'object') return;
  const id=message.id ?? state.selectedId, object=objects.get(id);
  switch(message.type) {
    case 'ground-calibration': mapGround?.setCalibration(message.transform,message.alignmentKind);break;
    case 'ground-toggle': mapGround?.toggleGround();fit();break;
    case 'map-reference-toggle': mapGround?.toggleReference();break;
    case 'routes-toggle': mapGround?.toggleRoutes();break;
    case 'equipment-overview': useView();fit(model,false,false);break;
    case 'pose': if (!pick) setPose(message); break;
    case 'pose-stop': stopPose(); break;
    case 'pose-reset': {
      stopPose(); const agv=objects.get('agv-composite-a');
      if (agv && originalPose) { agv.position.copy(originalPose.position); agv.quaternion.copy(originalPose.quaternion); }
      scheduleRender(); break;
    }
    case 'cal-pick':
      if (typeof message.token==='string' && /^[a-f0-9]{32}$/.test(message.token) &&
          typeof message.floor==='number' && Number.isFinite(message.floor) && Math.abs(message.floor)<=10) {
        stopPose(); pick={token:message.token,floor:message.floor};
        document.body.classList.add('picking');
        renderer.domElement.style.cursor='crosshair';
      } break;
    case 'cal-cancel': pick=null;document.body.classList.remove('picking');renderer.domElement.style.cursor=''; break;
    case 'telemetry': telemetry(message); break;
    case 'select': select(message.id ?? null); break;
    case 'reset': useView();fit(model,true,false); break;
    case 'focus': if(object){object.visible=true;fit(object);} break;
    case 'toggle': if(object){object.visible=!object.visible;scheduleRender();} break;
    case 'show-all': for(const object of objects.values())object.visible=true;select(null);fit(model);break;
  }
}
async function start() {
  renderer=new THREE.WebGLRenderer({antialias:true});
  renderer.setPixelRatio(Math.min(1.5,Math.max(1,window.devicePixelRatio||1)));
  renderer.setSize(Math.max(innerWidth,1),Math.max(innerHeight,1));
  renderer.setClearColor(0x2c3e50);renderer.outputColorSpace=THREE.SRGBColorSpace;
  renderer.toneMapping=THREE.ACESFilmicToneMapping;renderer.toneMappingExposure=.9;
  document.body.appendChild(renderer.domElement);
  scene=new THREE.Scene();scene.add(new THREE.HemisphereLight(0xeaf3ff,0x34445a,.7));
  const key=new THREE.DirectionalLight(0xfff7ec,2.3);key.position.set(3,6,4);scene.add(key);
  const fill=new THREE.DirectionalLight(0xd7e7ff,.3);fill.position.set(-4,2,-3);scene.add(fill);
  useView();
  decoder=new DRACOLoader();decoder.setDecoderPath('./vendor/three/examples/jsm/libs/draco/gltf/');decoder.setWorkerLimit(1);
  const loader=new GLTFLoader();loader.setDRACOLoader(decoder);
  const loaded=await loader.loadAsync('./lab606.glb');
  model=loaded.scene;scene.add(model);
  for(const id of ids) {
    const object=model.getObjectByName(id);if(!object)throw Error(`Missing model node: ${id}`);
    objects.set(id,object);
    if (id==='agv-composite-a') originalPose={position:object.position.clone(),quaternion:object.quaternion.clone()};
    object.traverse(child=>{
      if(!child.isMesh)return;
      child.material=child.material.clone();
      if(id==='agv-composite-a'){child.material.color.setHex(0x318de0);child.material.roughness=.55;}
      baseColors.set(child,child.material.color.clone());
    });
  }
  for (const [id, title] of [['agv-composite-a', '复合机器人'], ['decapper-a', '开盖／分液']]) {
    const object = objects.get(id);
    object.geometry.computeBoundingBox();
    const box = object.geometry.boundingBox;
    const anchor = box.getCenter(new THREE.Vector3()); anchor.y = box.max.y + .12;
    const element = document.createElement('button'); element.type = 'button'; element.className = 'device-label'; element.dataset.tone = 'unknown';
    const heading = document.createElement('strong'); heading.textContent = title;
    const text = document.createElement('span'); text.textContent = '等待状态';
    element.append(heading, text); element.addEventListener('click', () => select(id));
    document.getElementById('labels').appendChild(element); labels.set(id, {element, text, anchor});
  }
  bounds=new THREE.Box3().setFromObject(model,true);
  model.traverse(o=>{if(o.isMesh)modelTriangles+=(o.geometry.index?.count??o.geometry.attributes.position.count)/3;});
  mapGround=await createMapGround(scene,bounds,scheduleRender);
  fit(model,false,false);render();state.ready=true;notice.hidden=true;post('ready');
  const ray=new THREE.Raycaster(), pointer=new THREE.Vector2();let down=null;
  renderer.domElement.addEventListener('pointerdown',e=>{if(e.button===0)down=[e.clientX,e.clientY];});
  renderer.domElement.addEventListener('pointerup',e=>{
    if(e.button!==0||!down)return;
    const start=down;down=null;if(Math.hypot(e.clientX-start[0],e.clientY-start[1])>5)return;
    const rect=renderer.domElement.getBoundingClientRect();
    pointer.set((e.clientX-rect.left)/rect.width*2-1,-(e.clientY-rect.top)/rect.height*2+1);
    ray.setFromCamera(pointer,camera);
    if (pick) {
      const point=ray.ray.intersectPlane(new THREE.Plane(new THREE.Vector3(0,1,0),-pick.floor),new THREE.Vector3());
      if (point && Number.isFinite(point.x) && Number.isFinite(point.z) && Math.max(Math.abs(point.x),Math.abs(point.z))<=1000) {
        const previous=markers.get('picked');
        if(previous){scene.remove(previous);previous.geometry.dispose();previous.material.dispose();}
        const marker=new THREE.Mesh(new THREE.SphereGeometry(.055,12,8),new THREE.MeshBasicMaterial({color:0xffcc33}));
        marker.position.copy(point);scene.add(marker);markers.set('picked',marker);
        bridge?.postMessage({type:'cal-point',token:pick.token,x:point.x,z:point.z});
        pick=null;document.body.classList.remove('picking');renderer.domElement.style.cursor='';scheduleRender();
      }
      return;
    }
    const hit=ray.intersectObject(model,true).find(h=>{
      for(let o=h.object;o;o=o.parent)if(!o.visible)return false;return true;
    });
    let object=hit?.object;
    while(object&&!ids.has(object.name))object=object.parent;
    select(object?.name??null);
  });
  renderer.domElement.addEventListener('webglcontextlost',e=>{e.preventDefault();fail('WebGL context lost');});
}
bridge?.addEventListener('message',e=>command(e.data));
window.addEventListener('resize',()=>{
  if(!renderer)return;
  renderer.setSize(Math.max(innerWidth,1),Math.max(innerHeight,1));
  camera.aspect=Math.max(innerWidth,1)/Math.max(innerHeight,1);camera.updateProjectionMatrix();
  if(state.ready)fit();
});
document.addEventListener('visibilitychange',()=>{if(document.hidden){stopPose();cancelAnimationFrame(frame);frame=null;}else scheduleRender();});
window.addEventListener('pagehide',()=>{
  state.disposed=true;cancelAnimationFrame(frame);controls?.dispose();decoder?.dispose();
  const geometries=new Set(),materials=new Set();
  model?.traverse(o=>{if(o.geometry)geometries.add(o.geometry);if(o.material)materials.add(o.material);});
  geometries.forEach(g=>g.dispose());materials.forEach(m=>m.dispose());renderer?.dispose();
  markers.forEach(m=>{m.geometry.dispose();m.material.dispose();});markers.clear();stopPose();
  mapGround?.dispose();
});
// Read-only diagnostics for the isolated native-host smoke test.
window.digitalTwin={inspectMotion:()=>{
  const o=objects.get('agv-composite-a');
  return {...motion.inspect(),now:performance.now(),renderedFrames,
    pose:o?{x:o.position.x,y:o.position.y,z:o.position.z,yaw:o.rotation.y}:null};
},inspect:()=>({ ...state, nodes:[...objects].map(([id,o])=>{
    const p=new THREE.Box3().setFromObject(o).getCenter(new THREE.Vector3()).project(camera);
    return {id,visible:o.visible,position:o.position.toArray(),yaw:o.rotation.y,screenPoint:[(p.x+1)*innerWidth/2,(1-p.y)*innerHeight/2]};
  }),
  camera:camera?.position.toArray(),bounds:bounds?[bounds.min.toArray(),bounds.max.toArray()]:null,
  cameraView:camera?.isOrthographicCamera?'top':'overview',cameraTarget:controls?.target.toArray(),
  cameraDirection:camera?.position.clone().sub(controls.target).normalize().toArray(),
  stationScreen:mapGround?.inspect().stationPositions.map(s=>{const p=new THREE.Vector3(s.position[0],-.025,s.position[1]).project(camera);return {id:s.id,ndc:p.toArray()};}),
  drawCalls:renderer?.info.render.calls,triangles:renderer?.info.render.triangles,modelTriangles,ground:mapGround?.inspect(),
  pixelRatio:renderer?.getPixelRatio(),contextLost:renderer?.getContext().isContextLost() })};
start().catch(fail);
