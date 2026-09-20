export const MAP_MD5='9bd67a8b01f4da2617ce67e5f8a8d6b1';
export function referenceVisibility({groundEnabled,referenceEnabled,hasTransform,routesEnabled}) {
  const overlay=!!(groundEnabled&&referenceEnabled&&hasTransform);
  return {reference:!!referenceEnabled,overlay,stations:overlay,routes:overlay&&!!routesEnabled,
    mapRoutes:!!(referenceEnabled&&routesEnabled)};
}
const finitePoint=p=>Array.isArray(p)&&p.length===2&&p.every(n=>Number.isFinite(n)&&Math.abs(n)<=1000);
export function validateMap(data) {
  if(data?.schemaVersion!==1||data.mapName!=='guangzhou606'||data.md5!==MAP_MD5||data.unit!=='meter')throw Error('地图参照身份不匹配');
  const b=data.bounds;
  if(!Array.isArray(b)||b.length!==4||!b.every(Number.isFinite)||b[2]<=b[0]||b[3]<=b[1]||b[2]-b[0]>100||b[3]-b[1]>100)throw Error('地图范围无效');
  if(!Array.isArray(data.stations)||data.stations.length!==6||new Set(data.stations.map(p=>p.id)).size!==6||
    data.stations.some(p=>!/^LM[124567]$/.test(p.id)||!finitePoint(p.point)))throw Error('地图站点无效');
  if(!Array.isArray(data.lines)||data.lines.length>10000||data.lines.some(p=>!Array.isArray(p)||p.length!==4||!finitePoint(p.slice(0,2))||!finitePoint(p.slice(2))))throw Error('地图轮廓无效');
  if(!Array.isArray(data.scanPoints)||data.scanPoints.length>50000||!data.scanPoints.every(finitePoint))throw Error('扫描点无效');
  if(!Array.isArray(data.routes)||data.routes.length>1000||data.routes.some(r=>!Array.isArray(r.points)||r.points.length!==4||!r.points.every(finitePoint)))throw Error('路线无效');
  return data;
}
export function projectMap([x,y],{theta,tx,tz}) {
  return [Math.cos(theta)*x+Math.sin(theta)*y+tx,Math.sin(theta)*x-Math.cos(theta)*y+tz];
}
export function validTransform(t) {
  return t?.md5?.toLowerCase()===MAP_MD5&&['theta','tx','tz','floor'].every(k=>typeof t[k]==='number'&&Number.isFinite(t[k]))&&
    Math.abs(t.tx)<1000&&Math.abs(t.tz)<1000&&Math.abs(t.floor)<=10;
}
export function clipLine([x1,y1,x2,y2],[left,bottom,right,top]) {
  const dx=x2-x1,dy=y2-y1;let lo=0,hi=1;
  for(const [p,q] of [[-dx,x1-left],[dx,right-x1],[-dy,y1-bottom],[dy,top-y1]]) {
    if(p===0){if(q<0)return null;continue;}
    const r=q/p;if(p<0)lo=Math.max(lo,r);else hi=Math.min(hi,r);if(lo>hi)return null;
  }
  return [x1+lo*dx,y1+lo*dy,x1+hi*dx,y1+hi*dy];
}
export function curvePoints(points,steps=32) {
  return Array.from({length:steps+1},(_,i)=>{const t=i/steps,u=1-t;return [0,1].map(k=>u*u*u*points[0][k]+3*u*u*t*points[1][k]+3*u*t*t*points[2][k]+t*t*t*points[3][k]);});
}
