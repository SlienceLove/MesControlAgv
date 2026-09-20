import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
const [input,output]=process.argv.slice(2);
if(!input||!output)throw Error('Usage: node export-map-ground.mjs original.smap output.json');
const bytes=fs.readFileSync(input), md5=crypto.createHash('md5').update(bytes).digest('hex');
if(md5!=='9bd67a8b01f4da2617ce67e5f8a8d6b1')throw Error('Unexpected source map fingerprint; review station/scene calibration before exporting.');
const map=JSON.parse(bytes.toString('utf8'));
function point(p){if(!p)throw Error('Missing point');const x=p.x??0,y=p.y??0;if(!Number.isFinite(x)||!Number.isFinite(y)||Math.abs(x)>1000||Math.abs(y)>1000)throw Error('Invalid map point');return [x,y];}
const stations=map.advancedPointList.map(p=>({id:p.instanceName,point:point(p.pos)}));
const bounds=[Math.floor(Math.min(...stations.map(s=>s.point[0]))-3),Math.floor(Math.min(...stations.map(s=>s.point[1]))-3),
 Math.ceil(Math.max(...stations.map(s=>s.point[0]))+3),Math.ceil(Math.max(...stations.map(s=>s.point[1]))+3)];
const inside=([x,y])=>x>=bounds[0]&&x<=bounds[2]&&y>=bounds[1]&&y<=bounds[3];
// Downsample to 5 cm cells, not a filled polygon: obstacle returns are not room walls.
const points=new Map();for(const p of map.normalPosList){const q=point(p);if(inside(q))points.set(q.map(n=>Math.round(n/.05)).join(','),q);}
const lines=map.advancedLineList.map(p=>[...point(p.line.startPos),...point(p.line.endPos)]);
const routes=map.advancedCurveList.map(p=>({from:p.startPos.instanceName,to:p.endPos.instanceName,
 points:[point(p.startPos.pos),point(p.controlPos1),point(p.controlPos2),point(p.endPos.pos)]}));
const result={schemaVersion:1,mapName:map.header.mapName,md5,unit:'meter',bounds,
 boundaryKind:'station-workspace-display-extent-not-room-wall',stations,lines,scanPoints:[...points.values()],routes};
fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(result));
console.log(JSON.stringify({md5,stations:stations.length,lines:lines.length,scanPoints:points.size,routes:routes.length,bounds,bytes:fs.statSync(output).size}));
