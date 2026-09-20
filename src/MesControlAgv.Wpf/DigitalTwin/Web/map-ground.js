import * as THREE from 'three';
import {validateMap,projectMap,validTransform,clipLine,curvePoints,referenceVisibility} from './map-ground-data.mjs';

export async function createMapGround(scene,modelBounds,scheduleRender) {
  const response=await fetch('./map-ground.json');if(!response.ok)throw Error('地图参照未加载');
  const data=validateMap(await response.json());
  const group=new THREE.Group();group.name='map-ground';scene.add(group);
  const overlay=new THREE.Group();overlay.name='navigation-map-overlay';group.add(overlay);overlay.visible=false;
  const b=data.bounds,width=b[2]-b[0],depth=b[3]-b[1];
  const center=modelBounds.getCenter(new THREE.Vector3());
  let transform=null,enabled=true,routesEnabled=true,referenceEnabled=false,ground=null,grid=null,edge=null;
  let alignmentKind='none';
  let currentBounds;const stationLabels=[];
  const panel=document.getElementById('map-reference'),svg=document.getElementById('map-svg'),status=document.getElementById('ground-status');
  const routeGroup=new THREE.Group();overlay.add(routeGroup);
  const release=o=>{o.traverse(n=>{n.geometry?.dispose();if(Array.isArray(n.material))n.material.forEach(m=>m.dispose());else n.material?.dispose();});};
  const lineObject=(segments,color)=>{
    const geometry=new THREE.BufferGeometry();geometry.setAttribute('position',new THREE.Float32BufferAttribute(segments,3));
    return new THREE.LineSegments(geometry,new THREE.LineBasicMaterial({color}));
  };
  function rebuildGround(t) {
    for(const item of [ground,grid,edge])if(item){group.remove(item);release(item);}
    let minX,maxX,minZ,maxZ;
    if(t) {
      const corners=[[b[0],b[1]],[b[2],b[1]],[b[2],b[3]],[b[0],b[3]]].map(p=>projectMap(p,t));
      minX=Math.min(...corners.map(p=>p[0]),modelBounds.min.x-1);maxX=Math.max(...corners.map(p=>p[0]),modelBounds.max.x+1);
      minZ=Math.min(...corners.map(p=>p[1]),modelBounds.min.z-1);maxZ=Math.max(...corners.map(p=>p[1]),modelBounds.max.z+1);
    } else {
      minX=Math.min(center.x-width/2,modelBounds.min.x-1);maxX=Math.max(center.x+width/2,modelBounds.max.x+1);
      minZ=Math.min(center.z-depth/2,modelBounds.min.z-1);maxZ=Math.max(center.z+depth/2,modelBounds.max.z+1);
    }
    // Extra display margin requested after field preview; never scale CAD or map coordinates.
    minX-=2;maxX+=2;minZ-=2;maxZ+=2;
    const y=(t?.floor??modelBounds.min.y)-.015;
    currentBounds=new THREE.Box3(new THREE.Vector3(minX,y,minZ),new THREE.Vector3(maxX,y,maxZ));
    ground=new THREE.Mesh(new THREE.PlaneGeometry(maxX-minX,maxZ-minZ),new THREE.MeshStandardMaterial({color:0xd6dde0,roughness:1,metalness:0,side:THREE.DoubleSide}));
    ground.rotation.x=-Math.PI/2;ground.position.set((minX+maxX)/2,y,(minZ+maxZ)/2);group.add(ground);
    const segments=[];
    for(let x=Math.ceil(minX);x<maxX;x++)segments.push(x,y+.002,minZ,x,y+.002,maxZ);
    for(let z=Math.ceil(minZ);z<maxZ;z++)segments.push(minX,y+.002,z,maxX,y+.002,z);
    grid=lineObject(segments,0xa8b4bc);group.add(grid);
    edge=lineObject([minX,y+.003,minZ,maxX,y+.003,minZ,maxX,y+.003,minZ,maxX,y+.003,maxZ,
      maxX,y+.003,maxZ,minX,y+.003,maxZ,minX,y+.003,maxZ,minX,y+.003,minZ],0x253a48);group.add(edge);
  }
  function buildOverlay(t) {
    for(const child of [...overlay.children]){if(child===routeGroup)continue;overlay.remove(child);release(child);}
    for(const child of [...routeGroup.children]){routeGroup.remove(child);release(child);}
    stationLabels.forEach(l=>l.element.remove());stationLabels.length=0;
    const y=t.floor+.008,segments=[];
    for(const line of data.lines){const clipped=clipLine(line,b);if(!clipped)continue;const a=projectMap(clipped.slice(0,2),t),z=projectMap(clipped.slice(2),t);segments.push(a[0],y,a[1],z[0],y,z[1]);}
    overlay.add(lineObject(segments,0x28333c));
    const scans=data.scanPoints.flatMap(p=>{const q=projectMap(p,t);return [q[0],y,q[1]];});
    const scanGeometry=new THREE.BufferGeometry();scanGeometry.setAttribute('position',new THREE.Float32BufferAttribute(scans,3));
    overlay.add(new THREE.Points(scanGeometry,new THREE.PointsMaterial({color:0x586777,size:.025,sizeAttenuation:true})));
    const routeSegments=[];
    for(const route of data.routes){const points=curvePoints(route.points);for(let i=1;i<points.length;i++){
      const clipped=clipLine([...points[i-1],...points[i]],b);if(!clipped)continue;
      const a=projectMap(clipped.slice(0,2),t),z=projectMap(clipped.slice(2),t);routeSegments.push(a[0],y+.003,a[1],z[0],y+.003,z[1]);
    }}
    routeGroup.add(lineObject(routeSegments,0x2979ba));routeGroup.visible=routesEnabled;
    for(const station of data.stations){const p=projectMap(station.point,t);
      const marker=new THREE.Mesh(new THREE.RingGeometry(.10,.15,24),new THREE.MeshBasicMaterial({color:station.id==='LM1'?0xbc6000:0x1760a0,side:THREE.DoubleSide}));
      marker.rotation.x=-Math.PI/2;marker.position.set(p[0],y+.005,p[1]);overlay.add(marker);
      const label=document.createElement('span');label.className='station-label';label.textContent=station.id+(alignmentKind==='schematic'?' · 示意':'');
      document.getElementById('labels').appendChild(label);stationLabels.push({element:label,point:marker.position.clone()});
    }
  }
  const ns='http://www.w3.org/2000/svg';
  function el(tag,attributes,parent=svg){const e=document.createElementNS(ns,tag);for(const [k,v] of Object.entries(attributes))e.setAttribute(k,String(v));parent.appendChild(e);return e;}
  // Navigation mini-map is its own coordinate frame until a field-confirmed transform exists.
  svg.setAttribute('viewBox',`${b[0]} ${-b[3]} ${width} ${depth}`);
  el('rect',{x:b[0],y:-b[3],width,height:depth,fill:'#e5e9eb',stroke:'#344854','stroke-width':.05});
  for(let x=Math.ceil(b[0]);x<b[2];x++)el('path',{d:`M${x},${-b[3]}V${-b[1]}`,stroke:'#ccd3d7','stroke-width':.018});
  for(let y=Math.ceil(b[1]);y<b[3];y++)el('path',{d:`M${b[0]},${-y}H${b[2]}`,stroke:'#ccd3d7','stroke-width':.018});
  el('path',{d:data.scanPoints.map(([x,y])=>`M${x},${-y}h.025`).join(' '),stroke:'#596775','stroke-width':.045,fill:'none'});
  el('path',{d:data.lines.map(l=>clipLine(l,b)).filter(Boolean).map(([a,c,d,e])=>`M${a},${-c}L${d},${-e}`).join(' '),stroke:'#263947','stroke-width':.065,fill:'none'});
  const paths=el('g',{});
  for(const r of data.routes){const [a,c,d,e]=r.points;el('path',{d:`M${a[0]},${-a[1]}C${c[0]},${-c[1]} ${d[0]},${-d[1]} ${e[0]},${-e[1]}`,stroke:'#287bb6','stroke-width':.055,fill:'none'},paths);}
  for(const s of data.stations){const [x,y]=s.point;el('circle',{cx:x,cy:-y,r:.13,fill:s.id==='LM1'?'#cf6f14':'#287bb6',stroke:'white','stroke-width':.035});
    const label=el('text',{x:x+.2,y:-y+(s.id==='LM2'?.5:s.id==='LM7'?-.4:-.2),fill:'#19384e','font-size':.42,'font-weight':'bold'});label.textContent=s.id;
  }
  const setStatus=()=>{
    status.textContent=alignmentKind==='schematic'?'示意定位 · 工作台轮廓对齐，非测量标定 · 机器人跟随实机坐标':transform?'地面：1 m 网格 · 地图已按确认标定叠加':'地面：1 m 网格 · 边框为示意范围，非房间外墙';
    document.getElementById('map-reference-status').textContent=alignmentKind==='schematic'?'导航地图参照 · 示意对齐':transform?'导航地图参照 · 已按标定对齐':'导航地图参照 · 未与 CAD 对齐';
    status.dataset.kind=alignmentKind;
  };
  const visibility=()=>referenceVisibility({groundEnabled:enabled,referenceEnabled,hasTransform:!!transform,routesEnabled});
  function syncVisibility() {
    const v=visibility();
    panel.hidden=!v.reference;
    for(const id of ['source','ground-status','hint'])document.getElementById(id).hidden=!v.reference;
    overlay.visible=v.overlay;routeGroup.visible=v.routes;paths.style.display=v.mapRoutes?'':'none';
    if(!v.stations)stationLabels.forEach(l=>l.element.hidden=true);
    scheduleRender();
  }
  rebuildGround(null);setStatus();syncVisibility();
  return {
    setCalibration(t,kind='confirmed'){
      const next=validTransform(t)&&['confirmed','schematic'].includes(kind)?t:null;
      const nextKind=next?kind:'none';
      if(JSON.stringify(next)===JSON.stringify(transform)&&nextKind===alignmentKind)return;
      transform=next;alignmentKind=nextKind;rebuildGround(next);
      if(next)buildOverlay(next);else stationLabels.forEach(l=>l.element.hidden=true);
      setStatus();syncVisibility();
    },
    toggleGround(){enabled=!enabled;group.visible=enabled;syncVisibility();},
    toggleReference(){referenceEnabled=!referenceEnabled;syncVisibility();},
    toggleRoutes(){routesEnabled=!routesEnabled;syncVisibility();},
    updateLabels(camera){const visible=visibility().stations;for(const label of stationLabels){const p=label.point.clone().project(camera);label.element.hidden=!visible||p.z < -1||p.z>1||Math.abs(p.x)>1||Math.abs(p.y)>1;
      label.element.style.left=`${(p.x+1)*innerWidth/2}px`;label.element.style.top=`${(1-p.y)*innerHeight/2}px`;}},
    getBounds:()=>currentBounds.clone(),
    getOperatingBounds:()=>{
      if(!transform)return null;
      const operating=new THREE.Box3();
      for(const station of data.stations){const p=projectMap(station.point,transform);operating.expandByPoint(new THREE.Vector3(p[0],transform.floor,p[1]));}
      // Include the displayed curves, not only their endpoints; bends can extend past the stations.
      for(const route of data.routes){const points=curvePoints(route.points);for(let i=1;i<points.length;i++){
        const line=clipLine([...points[i-1],...points[i]],b);if(!line)continue;
        for(const point of [line.slice(0,2),line.slice(2)]){const p=projectMap(point,transform);operating.expandByPoint(new THREE.Vector3(p[0],transform.floor,p[1]));}
      }}
      operating.min.x-=.7;operating.max.x+=.7;operating.min.z-=.7;operating.max.z+=.7;
      return operating;
    },
    inspect:()=>({enabled,referenceEnabled,routesEnabled,aligned:alignmentKind==='confirmed',schematic:alignmentKind==='schematic',alignmentKind,
      overlayVisible:overlay.visible,routesVisible:overlay.visible&&routeGroup.visible,stationLabelsVisible:stationLabels.filter(l=>!l.element.hidden).length,
      stationCount:data.stations.length,mapMd5:data.md5,gridMeters:1,bounds:[currentBounds.min.toArray(),currentBounds.max.toArray()],
      stationPositions:transform?data.stations.map(s=>({id:s.id,position:projectMap(s.point,transform)})):[]}),
    dispose(){release(group);scene.remove(group);stationLabels.forEach(l=>l.element.remove());}
  };
}
