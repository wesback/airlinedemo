import { describe, expect, it } from 'vitest';
import { advanceHangarPlayRoute, createHangarPlayLayout, isHangarSegmentClear, isInHangarRackZone, planHangarPlayRoute, stepHangarAvatar, HANGAR_PLAY_MAX_ROUTE_POINTS } from '../src/hangar/hangarPlay';

const layout = createHangarPlayLayout();

describe('hangar play layout', () => {
  it('starts the avatar at the manifest start and lists the three aircraft markers', () => {
    expect(layout.start).toEqual({ x: -4, z: 20 });
    expect(layout.rackZone).toEqual({ centre: { x: -4, z: -19.5 }, radius: 1.5 });
    expect(layout.markers.map(m => m.marker)).toEqual([{ x: -28, z: 14 }, { x: -4, z: 9 }, { x: 25, z: 18 }]);
  });

  it('moves 0.5 per arrow key relative to the camera azimuth', () => {
    expect(stepHangarAvatar({ x: -4, z: 20 }, 'ArrowUp', 0, layout)).toEqual({ position: { x: -4, z: 19.5 }, accepted: true });
    expect(stepHangarAvatar({ x: -4, z: 20 }, 'ArrowRight', 0, layout)).toEqual({ position: { x: -3.5, z: 20 }, accepted: true });
    expect(stepHangarAvatar({ x: -4, z: 20 }, 'KeyW', 0, layout)).toBeNull();
  });

  it('refuses a step through the floor edge and keeps the position', () => {
    expect(stepHangarAvatar({ x: -4, z: 24.5 }, 'ArrowDown', 0, layout)).toEqual({ position: { x: -4, z: 24.5 }, accepted: false });
  });

  it('detects the rack interaction circle', () => {
    expect(isInHangarRackZone({ x: -4, z: -18.5 }, layout)).toBe(true);
    expect(isInHangarRackZone({ x: -4, z: -17.5 }, layout)).toBe(false);
  });

  it('plans a walkable route to every aircraft from the rack that fits the scene point limit', () => {
    for (const { marker } of layout.markers) {
      const route = planHangarPlayRoute({ x: -4, z: -19.5 }, marker, 0, layout);
      expect(route.status).toBe('route');
      expect(route.points.length).toBeGreaterThanOrEqual(2);
      expect(route.points.length).toBeLessThanOrEqual(HANGAR_PLAY_MAX_ROUTE_POINTS);
      expect(route.points[route.points.length - 1]).toEqual(marker);
      for (let i = 1; i < route.points.length; i++) expect(isHangarSegmentClear(route.points[i - 1], route.points[i], layout)).toBe(true);
      expect(route.liveText).toMatch(/^(ahead|behind|left|right), \d+\.\d{2} metres$/);
    }
  });

  it('reports arrival within 1.5 of the marker and no route line', () => {
    expect(planHangarPlayRoute({ x: -4, z: 10.4 }, { x: -4, z: 9 }, 0, layout)).toEqual({ status: 'arrival', points: [], liveText: null });
  });

  it('rejects a straight segment that crosses an aircraft footprint even when both ends are clear', () => {
    expect(isHangarSegmentClear({ x: -14, z: 7.5 }, { x: -4, z: -13 }, layout)).toBe(false);
    expect(isHangarSegmentClear({ x: -14, z: 7.5 }, { x: -14, z: -13 }, layout)).toBe(true);
    expect(isHangarSegmentClear({ x: -4, z: 20 }, { x: -4, z: 24.9 }, layout)).toBe(false);
  });

  it('keeps the guide line while the next waypoint is visible and asks for a replan when it is not', () => {
    const corridor = [{ x: -14, z: 8 }, { x: -14, z: -13 }, { x: -4, z: -13 }];
    expect(advanceHangarPlayRoute({ x: -14, z: 7.5 }, corridor, layout)).toEqual(corridor);
    expect(advanceHangarPlayRoute({ x: -4, z: 19.5 }, [{ x: -4, z: 20 }, { x: -4, z: 15 }, { x: 0, z: 15 }], layout)).toEqual([{ x: -4, z: 19.5 }, { x: 0, z: 15 }]);
    expect(advanceHangarPlayRoute({ x: -4, z: 12 }, [{ x: -4, z: 12 }, { x: -4, z: 0 }], layout)).toBeNull();
  });
});
