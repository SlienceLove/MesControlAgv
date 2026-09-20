import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validPose, interpolatePose, PoseMotion, samePose } from '../../src/MesControlAgv.Wpf/DigitalTwin/Web/pose-motion.mjs';
test('finite bounded pose with explicit TTL only',()=>{
  const p={x:1,y:0,z:2,yaw:0,validForMs:1500};
  assert.equal(validPose(p),true);
  for(const bad of [{...p,x:NaN},{...p,y:Infinity},{...p,validForMs:0},{...p,validForMs:2001},{...p,x:1001},{...p,yaw:null}])
    assert.equal(validPose(bad),false);
});
const pose=(x,yaw=0,validForMs=2000)=>({x,y:0,z:0,yaw,validForMs});
const close=(a,b)=>assert.ok(Math.abs(a-b)<1e-9,`${a} != ${b}`);
test('snap is immediate and normal motion lasts the receipt interval, not 200 ms',()=>{
  const m=new PoseMotion();
  close(m.push({...pose(4),snap:true},0,pose(-10)).x,4);assert.equal(m.active,false);
  m.push(pose(5),500,pose(4));close(m.inspect().durationMs,550);
  close(m.sample(700).x,4+200/550);
  assert.ok(m.sample(900).x>4.7);assert.equal(m.active,true);
  close(m.sample(1050).x,5);assert.equal(m.active,false);
});
test('retarget evaluates current time even when the last rendered frame is older',()=>{
  const m=new PoseMotion();m.push(pose(1),0,pose(0));
  const oldRender=m.sample(100);
  close(m.push(pose(2),300,oldRender).x,300/550);
  close(m.sample(300).x,300/550);
  const p=m.sample(500);assert.ok(p.x>300/550 && p.x<2);
});
test('duplicate endpoint packets converge instead of restarting forever, with no idle animation',()=>{
  const m=new PoseMotion();m.push(pose(1),0,pose(0));
  m.sample(490);m.push(pose(1),500,pose(0));
  close(m.sample(550).x,1);assert.equal(m.active,false);
  m.push(pose(1,2*Math.PI),1000,pose(1));assert.equal(m.active,false);
  assert.ok(samePose(m.sample(1100),pose(1)));
});
test('expired data freezes at last presentation and duplicates cannot extend TTL',()=>{
  const m=new PoseMotion();m.push(pose(1,0,300),0,pose(0));
  const shown=m.sample(200);m.push(pose(1),200,shown);
  close(m.inspect().expiresAt,300);
  close(m.sample(300).x,shown.x);assert.equal(m.active,false);
  close(m.sample(5000).x,shown.x);
  m.push(pose(2),6000,shown);close(m.inspect().durationMs,550);
  const last=m.sample(6100);m.push(pose(2,0,20),6100,last);
  close(m.inspect().expiresAt,6120);close(m.sample(6130).x,last.x);
});
test('stop and invalid packets discard old motion and cadence before resuming from displayed pose',()=>{
  const m=new PoseMotion();m.push(pose(1),0,pose(0));m.sample(100);m.stop();
  assert.equal(m.sample(500),null);assert.equal(m.active,false);
  close(m.push(pose(3),600,pose(2)).x,2);close(m.inspect().durationMs,550);
  assert.equal(m.push({...pose(4),x:NaN},700,pose(2)),null);
  assert.equal(m.sample(1000),null);
});
test('shortest arc is preserved while retargeting across pi',()=>{
  const m=new PoseMotion();m.push(pose(0,-Math.PI+.1),0,pose(0,Math.PI-.1));
  close(m.sample(275).yaw,Math.PI);
  close(m.push(pose(0,-Math.PI+.2),275,pose(0)).yaw,Math.PI);
  assert.ok(m.sample(450).yaw>Math.PI);
});
test('duration is bounded and a long gap does not pollute cadence',()=>{
  const m=new PoseMotion();m.push({...pose(0),snap:true},0,pose(0));
  for(let i=1;i<=20;i++){m.push(pose(i),i*10,pose(i-1));assert.ok(m.inspect().durationMs>=450);}
  for(let i=1;i<=20;i++){m.push(pose(20+i),200+i*1900,pose(19+i));assert.ok(m.inspect().durationMs<=1000);}
  m.push(pose(100),50000,pose(40));close(m.inspect().durationMs,550);
});

// Same timestamped input for both policies; each simulated render step is 10 ms.
function benchmark(intervals,adaptive) {
  const events=[{at:0,p:pose(0)}];let at=0;
  for(const interval of intervals){at+=interval;events.push({at,p:pose(at*.0003)});}
  const m=new PoseMotion();let current=pose(0),segment=null,next=1,waiting=0,steps=0,maxLag=0;
  m.push({...pose(0),snap:true},0,current);
  function oldSample(t){if(segment){current=interpolatePose(segment.from,segment.to,(t-segment.start)/200);if(t-segment.start>=200)segment=null;}return current;}
  for(let t=10;t<=at;t+=10){
    while(next<events.length&&events[next].at<=t){const e=events[next++];
      if(adaptive)m.push(e.p,e.at,current);else{oldSample(e.at);segment={from:current,to:e.p,start:e.at};}
    }
    current=adaptive?m.sample(t):oldSample(t);
    if(t<events[1].at)continue;
    const latest=events[next-1].p;
    assert.ok(current.x<=latest.x+1e-9,'extrapolated past newest known position');
    if(adaptive?!m.active:!segment)waiting++;
    steps++;maxLag=Math.max(maxLag,latest.x-current.x);
  }
  return {waitingFraction:waiting/steps,maxLagMeters:maxLag};
}
test('500 ms and jittered 450–650 ms input eliminate most periodic idle waiting',t=>{
  for(const intervals of [Array(24).fill(500),Array.from({length:24},(_,i)=>[450,650,500,600,550,500][i%6])]){
    const old=benchmark(intervals,false),updated=benchmark(intervals,true);
    assert.ok(old.waitingFraction>.5);assert.ok(updated.waitingFraction<.1);
    assert.ok(updated.maxLagMeters<.35,'unexpected unbounded display lag');
    t.diagnostic(JSON.stringify({intervals:intervals.slice(0,6),old,updated}));
  }
});
test('shortest heading arc across +/- pi without extrapolation',()=>{
  const a={x:0,y:0,z:0,yaw:Math.PI-.02},b={x:2,y:0,z:4,yaw:-Math.PI+.02};
  const midpoint=interpolatePose(a,b,.5);
  assert.ok(Math.abs(midpoint.yaw-Math.PI)<1e-9);assert.equal(midpoint.x,1);assert.equal(midpoint.z,2);
  assert.equal(interpolatePose(a,b,-1).x,0);assert.equal(interpolatePose(a,b,5).x,2);
});
