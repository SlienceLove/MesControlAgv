// Presentation-only, deterministic interpolation. No extrapolation or device commands.
export function validPose(p) {
  return p && ['x','y','z','yaw','validForMs'].every(k => typeof p[k] === 'number' && Number.isFinite(p[k])) &&
    Math.max(Math.abs(p.x), Math.abs(p.y), Math.abs(p.z)) <= 1000 && p.validForMs > 0 && p.validForMs <= 2000;
}
export function interpolatePose(from, to, fraction) {
  const t = Math.max(0, Math.min(1, fraction));
  const delta = Math.atan2(Math.sin(to.yaw-from.yaw), Math.cos(to.yaw-from.yaw));
  return { x:from.x+(to.x-from.x)*t, y:from.y+(to.y-from.y)*t,
    z:from.z+(to.z-from.z)*t, yaw:from.yaw+delta*t };
}

const copyPose = p => ({x:p.x,y:p.y,z:p.z,yaw:p.yaw});
export function samePose(a,b) {
  return a && b && Math.max(Math.abs(a.x-b.x),Math.abs(a.y-b.y),Math.abs(a.z-b.z),
    Math.abs(Math.atan2(Math.sin(a.yaw-b.yaw),Math.cos(a.yaw-b.yaw)))) < 1e-9;
}

// Times are monotonic browser receipt times, never the controller's wall clock.
// A segment only moves toward an already received position; there is no extrapolation.
export class PoseMotion {
  #position=null;
  #segment=null;
  #lastArrival=null;
  #interval=500;
  get active() { return this.#segment!==null; }
  inspect() { return {active:this.active,intervalMs:this.#interval,durationMs:this.#segment?.duration??null,
    receivedAt:this.#lastArrival,expiresAt:this.#segment?.expires??null}; }
  stop() {
    this.#position=null;this.#segment=null;this.#lastArrival=null;this.#interval=500;
  }
  sample(now) {
    const s=this.#segment;
    if(s) {
      if(!Number.isFinite(now)||now>=s.expires) {
        // Freeze at the last presented pose, not an expired segment's future endpoint.
        this.#segment=null;this.#lastArrival=null;this.#interval=500;
      } else {
        const fraction=(now-s.start)/s.duration;
        this.#position=interpolatePose(s.from,s.to,fraction);
        if(fraction>=1)this.#segment=null;
      }
    }
    return this.#position && copyPose(this.#position);
  }
  push(p,now,displayed) {
    if(!validPose(p)||!Number.isFinite(now)) { this.stop();return null; }
    if(p.snap===true) {
      this.stop();this.#position=copyPose(p);this.#lastArrival=now;
      return copyPose(this.#position);
    }
    // Evaluate the old segment at this exact receipt time, not the previous render frame.
    const from=this.sample(now)??copyPose(displayed);
    const gap=this.#lastArrival===null?null:now-this.#lastArrival;
    if(gap!==null && gap>0 && gap<=2000)this.#interval=.75*this.#interval+.25*gap;
    else if(gap!==null && gap>2000)this.#interval=500;
    this.#lastArrival=now;this.#position=from;
    // Repeated stationary targets must not perpetually restart the settling animation.
    // Nor may a repeat extend the expiry of an existing segment.
    if(this.#segment && samePose(this.#segment.to,p)) {
      this.#segment.expires=Math.min(this.#segment.expires,now+p.validForMs);
      return copyPose(from);
    }
    if(samePose(from,p)) { this.#position=copyPose(p);this.#segment=null;return copyPose(p); }
    this.#segment={from,to:copyPose(p),start:now,expires:now+p.validForMs,
      duration:Math.max(450,Math.min(1000,this.#interval*1.1))};
    return copyPose(from);
  }
}
