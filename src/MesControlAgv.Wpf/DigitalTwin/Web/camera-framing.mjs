import * as THREE from './vendor/three/build/three.module.min.js';

// The CAD environment spans 7 m along Z and 5.5 m along X.
// Look from +X so the long bench axis (-Z) reads horizontally, without changing world coordinates.
export const overviewDirection = new THREE.Vector3(1, 1, 0).normalize();
export const topUp = new THREE.Vector3(-1, 0, 0);

export function frameCamera(camera, controls, object, extraBounds = [], preserveDirection = false) {
  object.updateWorldMatrix(true, true);
  const box = new THREE.Box3().setFromObject(object, true);
  for (const extra of extraBounds) box.union(extra);
  if (box.isEmpty()) return;
  const center = box.getCenter(new THREE.Vector3());
  const direction = preserveDirection
    ? camera.position.clone().sub(controls.target).normalize()
    : camera.isOrthographicCamera ? new THREE.Vector3(0, 1, 0) : overviewDirection.clone();
  const right = new THREE.Vector3().crossVectors(camera.up, direction).normalize();
  const up = new THREE.Vector3().crossVectors(direction, right).normalize();
  const ty = Math.tan(THREE.MathUtils.degToRad((camera.fov ?? 30) / 2));
  const kx = ty * camera.aspect * .9, ky = ty * .83;
  let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity,maxDepth=-Infinity;
  let px=-Infinity,nx=-Infinity,py=-Infinity,ny=-Infinity;
  function include(x,y,z) {
    x-=center.x; y-=center.y; z-=center.z;
    const a=x*right.x+y*right.y+z*right.z, b=x*up.x+y*up.y+z*up.z, c=x*direction.x+y*direction.y+z*direction.z;
    minX=Math.min(minX,a);maxX=Math.max(maxX,a);minY=Math.min(minY,b);maxY=Math.max(maxY,b);maxDepth=Math.max(maxDepth,c);
    px=Math.max(px,c+a/kx);nx=Math.max(nx,c-a/kx);py=Math.max(py,c+b/ky);ny=Math.max(ny,c-b/ky);
  }
  object.traverse(mesh => {
    if (!mesh.isMesh) return;
    const a=mesh.geometry.attributes.position, m=mesh.matrixWorld.elements;
    for(let i=0;i<a.count;i++) {
      const x=a.getX(i),y=a.getY(i),z=a.getZ(i);
      include(m[0]*x+m[4]*y+m[8]*z+m[12],m[1]*x+m[5]*y+m[9]*z+m[13],m[2]*x+m[6]*y+m[10]*z+m[14]);
    }
  });
  for(const extra of extraBounds)
    for(const x of [extra.min.x,extra.max.x])for(const y of [extra.min.y,extra.max.y])for(const z of [extra.min.z,extra.max.z])include(x,y,z);
  if(!Number.isFinite(minX))return;
  const mx=(minX+maxX)/2,my=(minY+maxY)/2;
  const target=center.addScaledVector(right,mx).addScaledVector(up,my);
  let distance;
  camera.zoom=1;
  if(camera.isOrthographicCamera) {
    const halfHeight=Math.max((maxY-minY)/2/.83,(maxX-minX)/2/(camera.aspect*.9),.1)*1.015;
    camera.left=-halfHeight*camera.aspect;camera.right=halfHeight*camera.aspect;
    camera.top=halfHeight;camera.bottom=-halfHeight;
    distance=maxDepth+Math.max(box.getSize(new THREE.Vector3()).length(),1);
  } else {
    distance=Math.max(px-mx/kx,nx+mx/kx,py-my/ky,ny+my/ky,.15)*1.015;
  }
  camera.position.copy(target).addScaledVector(direction,distance);
  camera.near=.005;camera.far=Math.max(1000,distance+box.getSize(new THREE.Vector3()).length()+1);
  camera.updateProjectionMatrix();
  controls.target.copy(target);controls.update();
}
