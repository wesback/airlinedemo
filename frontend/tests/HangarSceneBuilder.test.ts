import { describe, expect, it, vi } from 'vitest';
import * as THREE from 'three';
import manifest from '../src/hangar/sceneManifest.json';
import { buildHangarAircraftSceneGroups, HANGAR_ROUTE_ERROR } from '../src/hangar/sceneBuilder';
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

  it('Hangar engine and component pick spheres stay unrendered', () => {
    const handler=vi.fn(); const root=buildHangarAircraftSceneGroups(manifest,view,handler)!;
    for(const letter of ['A','B','C']){
      const engine=traverse(root,`Engine${letter}Visible`)! as THREE.Mesh;
      engine.geometry.computeBoundingSphere();
      const enginePick=traverse(root,`Engine${letter}PickSphere`)! as THREE.Mesh;
      expect(enginePick).toBeInstanceOf(THREE.Mesh); expect((enginePick.material as THREE.MeshBasicMaterial).visible).toBe(false); expect(enginePick.geometry).toBeInstanceOf(THREE.SphereGeometry);
      expect((enginePick.geometry as THREE.SphereGeometry).parameters.radius).toBeGreaterThanOrEqual(1); expect((enginePick.geometry as THREE.SphereGeometry).parameters.radius).toBeGreaterThanOrEqual(1.25*(engine.geometry.boundingSphere?.radius??0));
      for(const component of [1,2]){
        const visible=traverse(root,`Component${letter}${component}Visible`)! as THREE.Mesh;
        visible.geometry.computeBoundingSphere(); const pick=traverse(root,`Component${letter}${component}PickSphere`)! as THREE.Mesh;
        expect(pick).toBeInstanceOf(THREE.Mesh); expect((pick.material as THREE.MeshBasicMaterial).visible).toBe(false); expect((pick.geometry as THREE.SphereGeometry).parameters.radius).toBeGreaterThanOrEqual(1); expect((pick.geometry as THREE.SphereGeometry).parameters.radius).toBeGreaterThanOrEqual(1.25*(visible.geometry.boundingSphere?.radius??0));
      }
    }
    root.traverse(object=>{if(object.userData.objectId!==undefined)expect(['AircraftA','EngineAVisible','ComponentA1Visible','ComponentA2Visible','AircraftB','EngineBVisible','ComponentB1Visible','ComponentB2Visible','AircraftC','EngineCVisible','ComponentC1Visible','ComponentC2Visible','AircraftAFuselage','AircraftBFuselage','AircraftCFuselage'].includes(object.name)).toBe(true);});
    const bad=clone(); bad.objectIds[1]=bad.objectIds[2]; expect(buildHangarAircraftSceneGroups(bad,view,handler)).toBeNull(); expect(handler).not.toHaveBeenCalled();
  });

  it('assigns exact selectable object IDs only', () => {
    const handler = vi.fn();
    const root = buildHangarAircraftSceneGroups(manifest, view, handler)!;
    const meshIds: string[] = []; root.traverse(o => { if (o instanceof THREE.Mesh && typeof o.userData.objectId === 'string') meshIds.push(o.userData.objectId); });
    expect([...new Set(meshIds)].sort()).toEqual([...manifest.objectIds].sort());
    expect(new Set(meshIds).size).toBe(12);
    expect(meshIds).toHaveLength(12);
    const exactMeshIds = new Map<string, string>();
    root.traverse(o => {
      if (o instanceof THREE.Mesh && typeof o.userData.objectId === 'string') exactMeshIds.set(o.name, o.userData.objectId);
    });
    const expectedMeshIds = new Map<string, string>();
    for (const letter of ['A', 'B', 'C'] as const) {
      expectedMeshIds.set(`Aircraft${letter}Fuselage`, `SYN-CP-${letter}-AIR-001`);
      expectedMeshIds.set(`Engine${letter}Visible`, `SYN-CP-${letter}-ENG-001`);
      expectedMeshIds.set(`Component${letter}1Visible`, `SYN-CP-${letter}-CMP-001`);
      expectedMeshIds.set(`Component${letter}2Visible`, `SYN-CP-${letter}-CMP-002`);
    }
    expect(Object.fromEntries(exactMeshIds)).toEqual(Object.fromEntries(expectedMeshIds));
    for (const [name, id] of expectedMeshIds) {
      const object = traverse(root, name)!;
      expect(object).toBeInstanceOf(THREE.Mesh);
      expect(object.userData.objectId).toBe(id);
      expect(object.userData.selectionHandler).toBe(handler);
    }
    for (const name of ['Floor','PartsStandBase','AircraftAWingLeft','AircraftAWingRight','AircraftBWingLeft','AircraftBWingRight','AircraftCWingLeft','AircraftCWingRight','GroundShadowA','GroundShadowB','GroundShadowC','EngineAPickSphere','EngineBPickSphere','EngineCPickSphere','ComponentA1PickSphere','ComponentA2PickSphere','ComponentB1PickSphere','ComponentB2PickSphere','ComponentC1PickSphere','ComponentC2PickSphere']) {
      const object = traverse(root, name)!; expect(object).toBeInstanceOf(THREE.Object3D); expect(object.userData.objectId).toBeUndefined();
    }
    const duplicate = clone(); duplicate.objectIds[1] = duplicate.objectIds[2];
    expect(buildHangarAircraftSceneGroups(duplicate, view, handler)).toBeNull(); expect(handler).not.toHaveBeenCalled();
    const unknown = clone(); unknown.objectIds[0] = 'UNKNOWN-ID'; unknown.aircraft.models[0].nodes.objectIds[0] = 'UNKNOWN-ID';
    expect(buildHangarAircraftSceneGroups(unknown, view, handler)).toBeNull(); expect(handler).not.toHaveBeenCalled();
  });

  it('builds floor rack avatar and hard hat', () => {
    const root = buildHangarAircraftSceneGroups(manifest, view)!;
    const floor = traverse(root, 'Floor')! as THREE.Mesh;
    expect(floor.geometry).toBeInstanceOf(THREE.BoxGeometry);
    expect((floor.geometry as THREE.BoxGeometry).parameters.width).toBe(90);
    expect((floor.geometry as THREE.BoxGeometry).parameters.height).toBe(0.2);
    expect((floor.geometry as THREE.BoxGeometry).parameters.depth).toBe(50);
    const rackUnits: THREE.Object3D[] = []; root.traverse(o => { if (manifest.rack.units.some(u => u.node === o.name)) rackUnits.push(o); });
    expect(rackUnits.map(o => o.name)).toEqual(manifest.rack.units.map(u => u.node));
    expect(rackUnits).toHaveLength(5);
    const avatar = traverse(root, 'Avatar')!;
    expect(avatar.position.toArray()).toEqual([-4, 0, 20]);
    const body = traverse(root, 'AvatarBody')! as THREE.Mesh;
    expect(body.geometry).toBeInstanceOf(THREE.CapsuleGeometry);
    const capsuleBounds = new THREE.Box3().setFromObject(body);
    expect(capsuleBounds.getSize(new THREE.Vector3()).y).toBeCloseTo(1.7, 2);
    const head = traverse(root, 'AvatarHead')! as THREE.Mesh;
    expect(head.geometry).toBeInstanceOf(THREE.SphereGeometry);
    const hat = traverse(root, 'HardHat')! as THREE.Mesh;
    expect(hat).toBeInstanceOf(THREE.Mesh);
    expect((hat.material as THREE.MeshStandardMaterial).color.getHexString().toUpperCase()).toBe('FFD400');
    const missing = clone(); missing.rack.units.pop();
    const extra = clone(); extra.rack.units.push({ node: 'RackUnitExtra', source: 'extra' });
    expect(buildHangarAircraftSceneGroups(missing, view)).toBeNull();
    expect(buildHangarAircraftSceneGroups(extra, view)).toBeNull();
  });

  it('builds shadows route and selection ring', () => {
    const routeView = { ...view, route: [{ x: -4, z: 20 }, { x: -4, z: 15 }, { x: -4, z: 9 }], selectedObjectId: manifest.objectIds[0] };
    const root = buildHangarAircraftSceneGroups(manifest, routeView)!;
    for (const letter of ['A', 'B', 'C']) {
      const shadow = traverse(root, `GroundShadow${letter}`)! as THREE.Mesh;
      expect(shadow.position.y).toBe(0.01);
      const layout = manifest.layout.aircraft.find(row => row.caseId.endsWith(letter.toLowerCase())) ?? manifest.layout.aircraft.find(row => row.caseId === `cp-case-${letter.toLowerCase()}`)!;
      expect(shadow.scale.x).toBe(layout.dimensions.span / 2);
      expect(shadow.scale.y).toBe(layout.dimensions.length / 2);
    }
    const guide = traverse(root, 'GuideLine')! as THREE.Mesh;
    expect(guide.geometry).toBeInstanceOf(THREE.TubeGeometry);
    expect((guide.geometry.parameters.path as THREE.Curve<THREE.Vector3>).getPoint(0).y).toBe(0.05);
    expect(traverse(root, 'SelectionRing')).toBeInstanceOf(THREE.Mesh);
    expect((traverse(root, 'SelectionRing') as THREE.Mesh).geometry).toBeInstanceOf(THREE.TorusGeometry);
    const badRoute = { ...view, route: [{ x: -4, z: 20 }, { x: 100, z: 100 }] };
    expect(() => buildHangarAircraftSceneGroups(manifest, badRoute)).toThrow(HANGAR_ROUTE_ERROR);
    const oversizedRoute = { ...view, route: Array.from({ length: 65 }, (_, i) => ({ x: -40 + i, z: 20 })) };
    expect(() => buildHangarAircraftSceneGroups(manifest, oversizedRoute)).toThrow(HANGAR_ROUTE_ERROR);
    const authoritativeRoute = { ...view, route: [{ x: -4, z: 20 }, { x: -4, z: 15, objectId: manifest.objectIds[0] }] };
    expect(() => buildHangarAircraftSceneGroups(manifest, authoritativeRoute)).toThrow(HANGAR_ROUTE_ERROR);
  });

  it('keeps non-selectable scene nodes without authority', () => {
    const root = buildHangarAircraftSceneGroups(manifest, view)!;
    root.traverse(object => {
      if (['Avatar','AvatarBody','AvatarHead','HardHat','Floor','Rack','GuideLine','SelectionRing'].includes(object.name) || object.name.startsWith('RackUnit') || object.name.startsWith('GroundShadow') || object.name.startsWith('FloorMarking')) {
        expect(object.userData.objectId).toBeUndefined();
      }
    });
    const hat = traverse(root, 'HardHat')!;
    expect(hat.userData).not.toHaveProperty('status');
    expect(hat.userData).not.toHaveProperty('role');
    expect(hat.userData).not.toHaveProperty('permission');
    const bad = clone(); bad.rack.units[0].objectId = bad.objectIds[0];
    expect(buildHangarAircraftSceneGroups(bad, view)).toBeNull();
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
        const approvedColors = [...Object.values(HANGAR_SCENE_THEME).map(v => v.toUpperCase()), '#FFD400'];
        expect(approvedColors).toContain(`#${color.getHexString().toUpperCase()}`);
      }
    } });
  });
});
