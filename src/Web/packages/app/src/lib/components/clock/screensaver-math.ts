export interface Vec2 {
  x: number;
  y: number;
}

export interface Bounds {
  blockW: number;
  blockH: number;
  viewportW: number;
  viewportH: number;
}

export interface AdvanceResult {
  pos: Vec2;
  vel: Vec2;
  hitLeft: boolean;
  hitRight: boolean;
  hitTop: boolean;
  hitBottom: boolean;
}

export function reflect(v: Vec2, axis: "x" | "y"): Vec2 {
  return axis === "x" ? { x: -v.x, y: v.y } : { x: v.x, y: -v.y };
}

export function advance(
  pos: Vec2,
  vel: Vec2,
  bounds: Bounds,
  dtSec: number
): AdvanceResult {
  const maxX = Math.max(0, bounds.viewportW - bounds.blockW);
  const maxY = Math.max(0, bounds.viewportH - bounds.blockH);

  let nx = pos.x + vel.x * dtSec;
  let ny = pos.y + vel.y * dtSec;
  let vx = vel.x;
  let vy = vel.y;

  let hitLeft = false;
  let hitRight = false;
  let hitTop = false;
  let hitBottom = false;

  if (nx <= 0) {
    nx = 0;
    vx = Math.abs(vx);
    hitLeft = true;
  } else if (nx >= maxX) {
    nx = maxX;
    vx = -Math.abs(vx);
    hitRight = true;
  }

  if (ny <= 0) {
    ny = 0;
    vy = Math.abs(vy);
    hitTop = true;
  } else if (ny >= maxY) {
    ny = maxY;
    vy = -Math.abs(vy);
    hitBottom = true;
  }

  return {
    pos: { x: nx, y: ny },
    vel: { x: vx, y: vy },
    hitLeft,
    hitRight,
    hitTop,
    hitBottom,
  };
}

/**
 * Compute a velocity vector of magnitude `speed` that, from `from`, points at `target`.
 * Used to rig the trajectory after a wall bounce so the block lands on a corner.
 */
export function computeAngleToCorner(from: Vec2, target: Vec2, speed: number): Vec2 {
  const dx = target.x - from.x;
  const dy = target.y - from.y;
  const mag = Math.hypot(dx, dy);
  if (mag === 0) return { x: speed, y: 0 };
  return { x: (dx / mag) * speed, y: (dy / mag) * speed };
}

/**
 * Generate a random unit angle that's at least `minDegFromAxis` degrees away from any cardinal axis,
 * so the bouncer doesn't slide imperceptibly along an edge.
 */
export function randomNonAxialAngle(
  random: () => number,
  minDegFromAxis = 5
): number {
  const minRad = (minDegFromAxis * Math.PI) / 180;
  let angle = random() * Math.PI * 2;
  for (const axis of [0, Math.PI / 2, Math.PI, (3 * Math.PI) / 2, 2 * Math.PI]) {
    if (Math.abs(angle - axis) < minRad) {
      angle = axis + minRad * (random() > 0.5 ? 1 : -1);
    }
  }
  return angle;
}

export function angleToVel(angle: number, speed: number): Vec2 {
  return { x: Math.cos(angle) * speed, y: Math.sin(angle) * speed };
}
