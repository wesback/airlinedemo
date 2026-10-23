import { describe, expect, it, vi } from 'vitest';
import * as THREE from 'three';
import manifest from '../src/hangar/sceneManifest.json';
import { buildHangarAircraftSceneGroups } from '../src/hangar/sceneBuilder';
import { HANGAR_SCENE_THEME } from '../src/hangar/sceneTheme';

const view = { contractVersion: 1, cases: [
  { caseId: 'cp-case-a', status: { assessment: 'ready' }, owner: 'A', nextAction: { text: 'Review' }, capabilityLabel: 'Rules-based' },
  { caseId: 'cp-case-b', status: { assessment: 'ready' }, owner: 'B', nextAction: { text: 'Request' }, capabilityLabel: 'AI agent' },
  { caseId: 'cp-case-c', status: { assessment: 'ready' }, owner: 'C', nextAction: null, capabilityLabel: 'Simulated' },
] };
const clone = () => JSON.parse(JSON.stringify(manifest));
const traverse = (root: THREE.Object3D, name: string) => { let found: THREE.Object3D | undefined; root.traverse(o => { if (o.name === name) found = o; }); return found; };

describe('Hangar scene builder', () => {
  it('builds exact three aircraft groups without WebGL', () => {
    const root = buildHangarAircraftSceneGroups(manifest, view)!;
    expect(root).toBeInstanceOf(THREE.Group);
    const caseGroups: THREE.Object3D[] = [];
    root.traverse((child: THREE.Object3D) => { if (/^Case[A-C]$/.test(child.name)) caseGroups.push(child); });
    expect(caseGroups.map(group => group.userData.caseId)).toEqual(['cp-case-a', 'cp-case-b', 'cp-case-c']);
    expect(caseGroups).toHaveLength(3);
    for (const [letter, id, length, span, height] of [['A','cp-case-a',24,22,7],['B','cp-case-b',18,17,6],['C','cp-case-c',32,30,10]] as const) {
      const aircraft = traverse(root, `Aircraft${letter}`)!;
      expect(aircraft).toBeInstanceOf(THREE.Group);
      expect(traverse(root, `Engine${letter}`)).toBeInstanceOf(THREE.Group);
      expect(traverse(root, `Component${letter}1`)).toBeInstanceOf(THREE.Group);
      expect(traverse(root, `Component${letter}2`)).toBeInstanceOf(THREE.Group);
      expect(traverse(root, `PartsStand${letter}`)).toBeInstanceOf(THREE.Group);
      const bounds = new THREE.Box3().setFromObject(aircraft);
      const size = bounds.getSize(new THREE.Vector3());
      expect(Math.abs(size.z - length) / length).toBeLessThanOrEqual(0.05);
      expect(Math.abs(size.x - span) / span).toBeLessThanOrEqual(0.05);
      expect(Math.abs(size.y - height) / height).toBeLessThanOrEqual(0.05);
      expect(Math.abs(bounds.min.y)).toBeLessThanOrEqual(0.05);
    }
    const missing = clone(); delete missing.aircraft.models[0].nodes.engine;
    const swapped = clone(); [swapped.layout.aircraft[0].dimensions, swapped.layout.aircraft[2].dimensions] = [swapped.layout.aircraft[2].dimensions, swapped.layout.aircraft[0].dimensions];
    expect(buildHangarAircraftSceneGroups(missing, view)).toBeNull();
    expect(buildHangarAircraftSceneGroups(swapped, view)).toBeNull();
  });

  it('assigns exact selectable object IDs only', () => {
    const handler = vi.fn();
    const root = buildHangarAircraftSceneGroups(manifest, view, handler)!;
    const meshIds: string[] = []; root.traverse(o => { if (o instanceof THREE.Mesh && typeof o.userData.objectId === 'string') meshIds.push(o.userData.objectId); });
    expect([...new Set(meshIds)].sort()).toEqual([...manifest.objectIds].sort());
    expect(new Set(meshIds).size).toBe(12);
    expect(meshIds).toHaveLength(12);
    for (const [letter, suffixes] of [['A',['AIR-001','ENG-001','CMP-001','CMP-002']],['B',['AIR-001','ENG-001','CMP-001','CMP-002']],['C',['AIR-001','ENG-001','CMP-001','CMP-002']]] as const) {
      const intended = [`SYN-CP-${letter}-${suffixes[0]}`, `SYN-CP-${letter}-${suffixes[1]}`, `SYN-CP-${letter}-${suffixes[2]}`, `SYN-CP-${letter}-${suffixes[3]}`];
      for (const id of intended) {
        let matches = 0; root.traverse(o => { if (typeof o.userData.objectId === 'string' && o.userData.objectId === id) { matches++; expect(o.userData.selectionHandler).toBe(handler); } });
        expect(matches).toBeGreaterThanOrEqual(1);
      }
    }
    for (const name of ['Floor','PartsStandBase','AircraftAWingLeft','AircraftBWingLeft','AircraftCWingLeft','GroundShadowA','GroundShadowB','GroundShadowC','EngineAPickSphere','EngineBPickSphere','EngineCPickSphere','ComponentA1PickSphere','ComponentA2PickSphere','ComponentB1PickSphere','ComponentB2PickSphere','ComponentC1PickSphere','ComponentC2PickSphere']) {
      const object = traverse(root, name)!; expect(object).toBeInstanceOf(THREE.Object3D); expect(object.userData.objectId).toBeUndefined();
    }
    const duplicate = clone(); duplicate.objectIds[1] = duplicate.objectIds[2];
    expect(buildHangarAircraftSceneGroups(duplicate, view, handler)).toBeNull(); expect(handler).not.toHaveBeenCalled();
    const unknown = clone(); unknown.objectIds[0] = 'UNKNOWN-ID'; unknown.aircraft.models[0].nodes.objectIds[0] = 'UNKNOWN-ID';
    expect(buildHangarAircraftSceneGroups(unknown, view, handler)).toBeNull(); expect(handler).not.toHaveBeenCalled();
  });

  it('builds status-independent untextured aircraft materials', () => {
    const first = buildHangarAircraftSceneGroups(manifest, view)!;
    const changed = structuredClone(view); changed.cases[0].status.assessment = 'blocked'; changed.cases[0].owner = 'Other'; changed.cases[0].nextAction = { text: 'Request' }; changed.cases[0].capabilityLabel = 'Simulated';
    const second = buildHangarAircraftSceneGroups(manifest, changed)!;
    const snapshot = (root: THREE.Object3D) => { const result: unknown[]=[]; root.traverse(o => { if (o instanceof THREE.Mesh) result.push([o.name,o.position.toArray(),o.scale.toArray(),o.userData.objectId??null,(o.material as THREE.MeshStandardMaterial).color?.getHexString(),o.visible,(o.material as any).map]); }); return result; };
    expect(snapshot(first)).toEqual(snapshot(second));
    first.traverse(o => { if (o instanceof THREE.Mesh) {
      const material = o.material as THREE.MeshStandardMaterial | THREE.MeshBasicMaterial;
      expect(['MeshStandardMaterial','MeshBasicMaterial']).toContain(material.type);
      for (const slot of ['map','alphaMap','aoMap','bumpMap','displacementMap','emissiveMap','envMap','metalnessMap','normalMap','roughnessMap','clearcoatMap','clearcoatNormalMap','clearcoatRoughnessMap','iridescenceMap','iridescenceThicknessMap','sheenColorMap','sheenRoughnessMap','thicknessMap','transmissionMap','specularIntensityMap','specularColorMap','lightMap']) expect((material as any)[slot]).toBeNull();
      const color = (material as any).color;
      if (color) {
        expect(Object.values(HANGAR_SCENE_THEME).map(v => v.toUpperCase())).toContain(`#${color.getHexString().toUpperCase()}`);
      }
    } });
  });
});
