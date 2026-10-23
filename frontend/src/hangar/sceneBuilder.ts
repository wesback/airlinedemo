import * as THREE from 'three';
import manifestDefault from './sceneManifest.json';
import { validateHangarAircraftDescriptor } from './sceneDescriptor';
import { HANGAR_SCENE_THEME } from './sceneTheme';

export const HANGAR_SCENE_BUILDER_ERROR = 'Visible mapping error';
export type HangarSceneView = unknown;
export type HangarSelectionHandler = (objectId: string, object: THREE.Object3D) => unknown;

function viewIsValid(view: unknown): boolean {
  if (view == null) return true;
  if (Array.isArray(view)) {
    const ids = view.map((v: any) => v?.caseId ?? v?.id).filter((v: unknown): v is string => typeof v === 'string');
    return ids.length === 3 && new Set(ids).size === 3 && ['cp-case-a', 'cp-case-b', 'cp-case-c'].every(id => ids.includes(id));
  }
  if (typeof view === 'object') {
    const value = view as any;
    const list = Array.isArray(value.cases) ? value.cases : Array.isArray(value.caseList) ? value.caseList : null;
    if (list) return viewIsValid(list);
    const ids = Array.isArray(value.objects) ? value.objects.map((o: any) => o?.caseId).filter((v: unknown): v is string => typeof v === 'string') : [];
    if (ids.length) {
      const unique = new Set(ids);
      if (unique.size !== 3 || !['cp-case-a','cp-case-b','cp-case-c'].every(id => unique.has(id))) return false;
      return value.case?.caseId ? ['cp-case-a','cp-case-b','cp-case-c'].includes(value.case.caseId) : true;
    }
  }
  return false;
}

function material(color: string, basic = false, opacity = 1): THREE.MeshStandardMaterial | THREE.MeshBasicMaterial {
  return basic
    ? Object.assign(new THREE.MeshBasicMaterial({ color, transparent: opacity < 1, opacity }), { map: null })
    : Object.assign(new THREE.MeshStandardMaterial({ color, roughness: 0.72, metalness: 0.05, transparent: opacity < 1, opacity }), { map: null });
}

function box(name: string, size: [number, number, number], position: [number, number, number], mat: THREE.Material, parent: THREE.Object3D) {
  const mesh = new THREE.Mesh(new THREE.BoxGeometry(...size), mat);
  mesh.name = name; mesh.position.set(...position); parent.add(mesh); return mesh;
}

function buildAircraft(layout: any, model: any): THREE.Group {
  const { length, span, height } = layout.dimensions as { length: number; span: number; height: number };
  const aircraft = new THREE.Group(); aircraft.name = model.nodes.aircraft;
  const letter = model.nodes.aircraft.slice(-1);
  const objectId = model.nodes.objectIds[0];

  const fuselage = new THREE.Mesh(new THREE.CapsuleGeometry(span * 0.105, length * 0.68, 8, 12), material(HANGAR_SCENE_THEME.aircraftBody));
  fuselage.name = `${model.nodes.aircraft}Fuselage`; fuselage.rotation.x = Math.PI / 2; aircraft.add(fuselage);
  box(`${model.nodes.aircraft}WingLeft`, [span * 0.46, 0.3, length * 0.38], [-span * 0.25, height * 0.32, 0], material(HANGAR_SCENE_THEME.aircraftBody), aircraft);
  box(`${model.nodes.aircraft}WingRight`, [span * 0.46, 0.3, length * 0.38], [span * 0.25, height * 0.32, 0], material(HANGAR_SCENE_THEME.aircraftBody), aircraft);
  box(`${model.nodes.aircraft}Stabiliser`, [span * 0.42, 0.18, length * 0.16], [0, height * 0.58, -length * 0.38], material(HANGAR_SCENE_THEME.aircraftBody), aircraft);
  const fin = new THREE.Mesh(new THREE.BoxGeometry(span * 0.08, height * 0.3, length * 0.16), material(HANGAR_SCENE_THEME.aircraftBody));
  fin.name = `${model.nodes.aircraft}Fin`; fin.position.set(0, height * 0.72, -length * 0.38); aircraft.add(fin);

  const regional = model.category === 'regional';
  if (regional) {
    fin.position.set(0, height * 0.62, -length * 0.39); fin.scale.y = 0.5;
  }
  for (const side of [-1, 1]) {
    const gear = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.09, height * 0.25, 8), material(HANGAR_SCENE_THEME.aircraftTrim));
    gear.name = `${model.nodes.aircraft}LandingGear${side < 0 ? 'Left' : 'Right'}`; gear.position.set(side * span * 0.27, height * 0.14, 0); aircraft.add(gear);
    if (side < 0) continue;
    const engine = new THREE.Mesh(new THREE.CapsuleGeometry(Math.min(span, length) * 0.07, height * 0.55, 8, 12), material(HANGAR_SCENE_THEME.aircraftTrim));
    engine.name = `RightEngine${letter}`; engine.rotation.x = Math.PI / 2; engine.position.set(side * span * 0.27, height * 0.26, length * 0.05); aircraft.add(engine);
  }
  aircraft.updateMatrixWorld(true);
  let bounds = new THREE.Box3().setFromObject(aircraft);
  let size = bounds.getSize(new THREE.Vector3());
  aircraft.scale.set(span / Math.max(size.x, 0.001), height / Math.max(size.y, 0.001), length / Math.max(size.z, 0.001));
  aircraft.updateMatrixWorld(true);
  bounds = new THREE.Box3().setFromObject(aircraft);
  aircraft.position.set(layout.centre.x, -bounds.min.y, layout.centre.z);
  aircraft.updateMatrixWorld(true);
  fuselage.userData.objectId = objectId;
  aircraft.userData.objectId = objectId;
  return aircraft;
}

function buildEngine(layout: any, model: any, parent: THREE.Group) {
  const nodes = model.nodes; const { length, span, height } = layout.dimensions;
  const engine = new THREE.Group(); engine.name = nodes.engine;
  const visible = new THREE.Mesh(new THREE.CapsuleGeometry(Math.min(span, length) * 0.07, height * 0.55, 8, 12), material(HANGAR_SCENE_THEME.aircraftTrim));
  visible.name = `${nodes.engine}Visible`; visible.rotation.x = Math.PI / 2; visible.userData.objectId = nodes.objectIds[1]; engine.add(visible);
  visible.position.set(layout.centre.x - span * 0.27, height * 0.26, layout.centre.z + length * 0.05);
  const pickRadius = Math.max(1, 1.25 * visible.geometry.boundingSphere?.radius || 1);
  const pickMaterial = Object.assign(new THREE.MeshBasicMaterial({ color: HANGAR_SCENE_THEME.groundShadow }), { map: null }); pickMaterial.visible = false;
  const pick = new THREE.Mesh(new THREE.SphereGeometry(pickRadius, 12, 8), pickMaterial);
  pick.name = `${nodes.engine}PickSphere`; pick.position.copy(visible.position); pick.userData.objectId = nodes.objectIds[1]; engine.add(pick);
  engine.userData.objectId = nodes.objectIds[1]; parent.add(engine);
  return engine;
}

export function buildHangarAircraftSceneGroups(manifest: unknown = manifestDefault, sceneView: HangarSceneView = null, selectionHandler?: HangarSelectionHandler | null): THREE.Group | null {
  const descriptor = validateHangarAircraftDescriptor(manifest);
  if (!descriptor.valid || !viewIsValid(sceneView)) return null;
  const m = manifest as any; const root = new THREE.Group(); root.name = 'HangarAircraftScene';

  const floor = new THREE.Mesh(new THREE.BoxGeometry(m.layout.floorSlab.width, m.layout.floorSlab.height, m.layout.floorSlab.depth), material(HANGAR_SCENE_THEME.floor));
  floor.name = 'Floor'; floor.position.y = -m.layout.floorSlab.height / 2; root.add(floor);

  for (let i = 0; i < 3; i++) {
    const model = m.aircraft.models[i], layout = m.layout.aircraft[i];
    const caseGroup = new THREE.Group(); caseGroup.name = `Case${model.nodes.aircraft.slice(-1)}`; caseGroup.userData.caseId = layout.caseId;
    const aircraft = buildAircraft(layout, model); caseGroup.add(aircraft);

    buildEngine(layout, model, caseGroup);
    const standX = layout.centre.x - 0.3 * layout.dimensions.span;
    const standZ = layout.centre.z + 0.3 * layout.dimensions.length;
    const stand = new THREE.Group(); stand.name = `PartsStand${model.nodes.aircraft.slice(-1)}`; stand.position.set(standX, 0, standZ); caseGroup.add(stand);
    box('PartsStandBase', [1.4, 1, 1.4], [0, 0.5, 0], material(HANGAR_SCENE_THEME.partsStand), stand);
    for (let c = 1; c <= 2; c++) {
      const componentName = c === 1 ? model.nodes.component1 : model.nodes.component2;
      const component = new THREE.Group(); component.name = componentName;
      const visible = c === 1
        ? new THREE.Mesh(new THREE.CylinderGeometry(0.5, 0.5, 1, 16), material(HANGAR_SCENE_THEME.component))
        : new THREE.Mesh(new THREE.CylinderGeometry(0.6, 0.6, 0.12, 16), material(HANGAR_SCENE_THEME.component));
      visible.name = `${componentName}Visible`;
      if (c === 1) visible.rotation.z = Math.PI / 2;
      visible.position.set(c === 1 ? -0.45 : 0.45, 1.35, 0);
      visible.userData.objectId = model.nodes.objectIds[c + 1];
      component.userData.objectId = model.nodes.objectIds[c + 1]; component.add(visible);
      const pickRadius = Math.max(1, 1.25 * Math.max(c === 1 ? 0.5 : 0.6, 0.5));
      const pickMaterial = Object.assign(new THREE.MeshBasicMaterial({ color: HANGAR_SCENE_THEME.groundShadow }), { map: null }); pickMaterial.visible = false;
      const pick = new THREE.Mesh(new THREE.SphereGeometry(pickRadius, 12, 8), pickMaterial); pick.name = `${componentName}PickSphere`; pick.position.copy(visible.position); pick.userData.objectId = model.nodes.objectIds[c + 1]; component.add(pick);
      stand.add(component);
    }
    const shadow = new THREE.Mesh(new THREE.CircleGeometry(1, 32), material(HANGAR_SCENE_THEME.groundShadow, true, 0.18));
    shadow.name = `GroundShadow${model.nodes.aircraft.slice(-1)}`; shadow.rotation.x = -Math.PI / 2; shadow.position.set(layout.centre.x, 0.01, layout.centre.z); shadow.scale.set(layout.dimensions.span / 2, layout.dimensions.length / 2, 1); caseGroup.add(shadow);
    root.add(caseGroup);
  }

  if (selectionHandler) root.traverse((object) => { if (typeof object.userData.objectId === 'string') object.userData.selectionHandler = selectionHandler; });
  return root;
}

export const buildHangarScene = buildHangarAircraftSceneGroups;
export const buildHangarSceneGroups = buildHangarAircraftSceneGroups;
export const buildHangarAircraftScene = buildHangarAircraftSceneGroups;
export const buildRealAircraftSceneGroups = buildHangarAircraftSceneGroups;
