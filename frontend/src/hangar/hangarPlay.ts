import manifestDefault from './sceneManifest.json';
import { getHangarCameraRelativeStep, isHangarWalkStepAllowed, HangarPoint, HangarWalkConfig } from './walkController';
import { getHangarRouteLiveDirection, planHangarRoute, HANGAR_ROUTE_ARRIVAL_THRESHOLD } from './routePlanner';

export const HANGAR_PLAY_MAX_ROUTE_POINTS = 64;

export type HangarPlayMarker = { caseId: string; aircraftId: string; marker: HangarPoint };
export type HangarPlayLayout = {
  config: HangarWalkConfig;
  start: HangarPoint;
  rackZone: { centre: HangarPoint; radius: number };
  markers: HangarPlayMarker[];
};
export type HangarPlayRoute = {
  status: 'route' | 'arrival' | 'unreachable';
  points: HangarPoint[];
  liveText: string | null;
};

// Builds the walk and route configuration from the manifest layout (the same source as the scene).
export function createHangarPlayLayout(manifest: any = manifestDefault): HangarPlayLayout {
  const layout = manifest.layout;
  const halfWidth = layout.floorSlab.width / 2, halfDepth = layout.floorSlab.depth / 2;
  const floorPolygon = [
    { x: -halfWidth, z: -halfDepth }, { x: halfWidth, z: -halfDepth },
    { x: halfWidth, z: halfDepth }, { x: -halfWidth, z: halfDepth },
  ];
  const aircraftFootprints = layout.aircraft.map((a: any) => ({ ...a.footprint, x: a.centre.x, z: a.centre.z }));
  const rack = layout.rack.footprint;
  return {
    config: { walkablePolygon: floorPolygon, aircraftFootprints, rackFootprints: [{ ...rack, x: (rack.minX + rack.maxX) / 2, z: (rack.minZ + rack.maxZ) / 2 }], avatarRadius: 0.3 },
    start: { x: layout.avatarStart.x, z: layout.avatarStart.z },
    rackZone: { centre: { x: layout.interactionCircle.centre.x, z: layout.interactionCircle.centre.z }, radius: layout.interactionCircle.radius },
    markers: layout.aircraft.map((a: any, i: number) => ({ caseId: a.caseId, aircraftId: manifest.aircraft.models[i].nodes.objectIds[0], marker: { x: a.marker.x, z: a.marker.z } })),
  };
}

// One arrow key press or key repeat moves 0.5 relative to the camera azimuth; refused steps keep the position.
export function stepHangarAvatar(position: HangarPoint, key: string, azimuthDegrees: number, layout: HangarPlayLayout): { position: HangarPoint; accepted: boolean } | null {
  const delta = getHangarCameraRelativeStep(key, azimuthDegrees);
  if (!delta) return null;
  const target = { x: position.x + delta.x, z: position.z + delta.z };
  if (!isHangarWalkStepAllowed(position, target, layout.config)) return { position, accepted: false };
  return { position: target, accepted: true };
}

export function isInHangarRackZone(position: HangarPoint, layout: HangarPlayLayout): boolean {
  return Math.hypot(position.x - layout.rackZone.centre.x, position.z - layout.rackZone.centre.z) <= layout.rackZone.radius;
}

// Exact clearance test for a long straight segment: inside the floor (less the avatar radius) and outside every
// footprint grown by the avatar radius (slab clipping against each box).
export function isHangarSegmentClear(a: HangarPoint, b: HangarPoint, layout: HangarPlayLayout): boolean {
  const r = layout.config.avatarRadius ?? 0.3;
  const floor = layout.config.walkablePolygon ?? [];
  const xs = floor.map(p => p.x), zs = floor.map(p => p.z);
  const inFloor = (p: HangarPoint) => p.x >= Math.min(...xs) + r && p.x <= Math.max(...xs) - r && p.z >= Math.min(...zs) + r && p.z <= Math.max(...zs) - r;
  if (!inFloor(a) || !inFloor(b)) return false;
  const boxes = [...(layout.config.aircraftFootprints ?? []), ...(layout.config.rackFootprints ?? [])];
  for (const f of boxes) {
    const minX = f.minX! - r, maxX = f.maxX! + r, minZ = f.minZ! - r, maxZ = f.maxZ! + r;
    let t0 = 0, t1 = 1;
    const dx = b.x - a.x, dz = b.z - a.z;
    const clip = (p: number, q: number) => {
      if (Math.abs(p) < 1e-12) return q >= 0;
      const t = q / p;
      if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; } else { if (t < t0) return false; if (t < t1) t1 = t; }
      return true;
    };
    if (clip(-dx, a.x - minX) && clip(dx, maxX - a.x) && clip(-dz, a.z - minZ) && clip(dz, maxZ - a.z) && t0 <= t1) return false;
  }
  return true;
}

// Drops grid points while the straight segment stays walkable, so routes fit the scene's point limit.
export function simplifyHangarRoute(points: HangarPoint[], layout: HangarPlayLayout): HangarPoint[] {
  if (points.length <= 2) return points.slice();
  const result = [points[0]];
  let anchor = 0;
  while (anchor < points.length - 1) {
    let next = anchor + 1;
    for (let j = points.length - 1; j > anchor + 1; j--) {
      if (isHangarSegmentClear(points[anchor], points[j], layout)) { next = j; break; }
    }
    result.push(points[next]);
    anchor = next;
  }
  if (result.length > HANGAR_PLAY_MAX_ROUTE_POINTS) {
    const stride = Math.ceil((result.length - 1) / (HANGAR_PLAY_MAX_ROUTE_POINTS - 1));
    return [...result.filter((_, i) => i % stride === 0 && i < result.length - 1), result[result.length - 1]];
  }
  return result;
}

export function planHangarPlayRoute(position: HangarPoint, marker: HangarPoint, azimuthDegrees: number, layout: HangarPlayLayout): HangarPlayRoute {
  if (Math.hypot(position.x - marker.x, position.z - marker.z) <= HANGAR_ROUTE_ARRIVAL_THRESHOLD) return { status: 'arrival', points: [], liveText: null };
  const planned = planHangarRoute(position, marker, layout.config);
  if (planned.status !== 'route') return { status: planned.status === 'arrival' ? 'arrival' : 'unreachable', points: [], liveText: null };
  return { status: 'route', points: simplifyHangarRoute(planned.route, layout), liveText: getHangarRouteLiveDirection(position, marker, azimuthDegrees).liveText };
}

// Keeps the current guide line while the avatar can still see its next waypoint; null means a full replan is needed.
export function advanceHangarPlayRoute(position: HangarPoint, points: HangarPoint[], layout: HangarPlayLayout, slack = 1): HangarPoint[] | null {
  if (points.length < 2) return null;
  let rest = points.slice(1);
  while (rest.length > 1 && isHangarSegmentClear(position, rest[1], layout)) rest = rest.slice(1);
  if (!isHangarSegmentClear(position, rest[0], layout)) return null;
  const start = Math.hypot(points[0].x - position.x, points[0].z - position.z) <= slack && rest.length === points.length - 1 ? points[0] : position;
  return [start, ...rest];
}
