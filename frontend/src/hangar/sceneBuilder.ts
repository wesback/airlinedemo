import * as THREE from 'three';
import manifestDefault from './sceneManifest.json';
import { validateHangarAircraftDescriptor } from './sceneDescriptor';
import { HANGAR_SCENE_THEME } from './sceneTheme';

export const HANGAR_SCENE_BUILDER_ERROR = 'Visible mapping error';
export const HANGAR_ROUTE_ERROR = 'Visible route error';
export const HANGAR_MAX_ROUTE_POINTS = 64;
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
    if (Object.prototype.hasOwnProperty.call(value, 'route') || Object.prototype.hasOwnProperty.call(value, 'guideLine') || Object.prototype.hasOwnProperty.call(value, 'selectedObjectId') || Object.prototype.hasOwnProperty.call(value, 'selection')) return true;
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
  const result = basic
    ? new THREE.MeshBasicMaterial({ color, transparent: opacity < 1, opacity })
    : new THREE.MeshStandardMaterial({ color, roughness: 0.72, metalness: 0.05, transparent: opacity < 1, opacity });
  for (const slot of ['map','alphaMap','aoMap','bumpMap','displacementMap','emissiveMap','envMap','metalnessMap','normalMap','roughnessMap','clearcoatMap','clearcoatNormalMap','clearcoatRoughnessMap','iridescenceMap','iridescenceThicknessMap','sheenColorMap','sheenRoughnessMap','thicknessMap','transmissionMap','specularIntensityMap','specularColorMap','lightMap'] as const) {
    (result as any)[slot] = null;
  }
  return result;
}

function addHangarLighting(root: THREE.Group, m: any) {
  const sky = new THREE.HemisphereLight(0xffffff, HANGAR_SCENE_THEME.floor, 1.6);
  sky.name = 'HangarSkyLight'; root.add(sky);
  const ambient = new THREE.AmbientLight(0xffffff, 0.35);
  ambient.name = 'HangarAmbientLight'; root.add(ambient);
  const width = Number(m?.layout?.floorSlab?.width) || 90;
  const key = new THREE.DirectionalLight(0xffffff, 2.2);
  key.name = 'HangarKeyLight'; key.position.set(width * 0.35, width * 0.75, width * 0.5); root.add(key); root.add(key.target);
  const fill = new THREE.DirectionalLight(0xdfe7f2, 0.9);
  fill.name = 'HangarFillLight'; fill.position.set(-width * 0.45, width * 0.4, -width * 0.35); root.add(fill); root.add(fill.target);
}

/** Releases the GPU resources of a scene group built by buildHangarAircraftSceneGroups. */
export function disposeHangarSceneGroup(root: THREE.Object3D | null | undefined) {
  if (!root) return;
  root.traverse(object => {
    if (!(object instanceof THREE.Mesh)) return;
    object.geometry?.dispose();
    const materials = Array.isArray(object.material) ? object.material : [object.material];
    for (const item of materials) item?.dispose?.();
  });
}

function box(name: string, size: [number, number, number], position: [number, number, number], mat: THREE.Material, parent: THREE.Object3D) {
  const mesh = new THREE.Mesh(new THREE.BoxGeometry(...size), mat);
  mesh.name = name; mesh.position.set(...position); parent.add(mesh); return mesh;
}

function sceneSelection(view: unknown): string | null {
  if (!view || typeof view !== 'object') return null;
  const value = view as any;
  const candidates = [value.selectedObjectId, value.selectionObjectId, value.selection?.objectId, value.selection?.id, value.selection?.selectedObjectId, value.selection?.selectedId, value.selected?.objectId, value.selected?.id, value.selectedObject?.objectId, value.selection, value.selectedObject];
  for (const candidate of candidates) {
    if (typeof candidate === 'string') return candidate;
  }
  return null;
}

function sceneRoute(view: unknown): Array<{x:number;z:number}> | null {
  if (!view || typeof view !== 'object') return null;
  const value = view as any;
  const raw = value.route?.points ?? value.route?.route ?? value.guideLine?.points ?? value.guideLine?.route ?? value.route ?? value.points;
  if (!Array.isArray(raw) || raw.length === 0) return null;
  if (raw.length > HANGAR_MAX_ROUTE_POINTS) return null;
  if (raw.some((point: any) => point && (point.objectId != null || point.selectionHandler != null))) return null;
  const points: Array<{x:number;z:number} | null> = raw.map((point: any) => typeof point === 'object' && point && typeof point.x === 'number' && typeof point.z === 'number' ? {x:point.x,z:point.z} : null);
  return points.every((point): point is {x:number;z:number} => point !== null && Number.isFinite(point.x) && Number.isFinite(point.z)) ? points : null;
}

function sceneRouteCandidate(view: unknown): boolean {
  if (!view || typeof view !== 'object') return false;
  const value = view as any;
  const raw = value.route?.points ?? value.route?.route ?? value.guideLine?.points ?? value.guideLine?.route ?? value.route ?? value.points;
  return Array.isArray(raw) && raw.length > 0;
}

function pointInsideFloor(x: number, z: number, m: any): boolean {
  const polygon = m.layout.floorPolygon;
  if (!Array.isArray(polygon) || polygon.length < 3) return false;
  let inside = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const a = polygon[i], b = polygon[j];
    if (((a.z > z) !== (b.z > z)) && x < (b.x - a.x) * (z - a.z) / (b.z - a.z || 1e-12) + a.x) inside = !inside;
  }
  return inside;
}

function buildAircraft(layout: any, model: any): THREE.Group {
  const { length, span, height } = layout.dimensions as { length: number; span: number; height: number };
  const name = model.nodes.aircraft as string;
  const aircraft = new THREE.Group(); aircraft.name = name;
  const letter = name.slice(-1);
  const objectId = model.nodes.objectIds[0];
  const regional = model.category === 'regional';
  const body = material(HANGAR_SCENE_THEME.aircraftBody);
  const trim = material(HANGAR_SCENE_THEME.aircraftTrim);
  const glass = material(HANGAR_SCENE_THEME.rack);
  const livery = material(HANGAR_SCENE_THEME.avatarBody);

  // Proportions of a generic airliner; the group is normalised to the manifest dimensions afterwards.
  const r = length * 0.06;
  const gearHeight = height * (regional ? 0.16 : 0.2);
  const fy = gearHeight + r;
  const fuselage = new THREE.Mesh(new THREE.CapsuleGeometry(r, length - 2 * r, 10, 20), body);
  fuselage.name = `${name}Fuselage`; fuselage.rotation.x = Math.PI / 2; fuselage.position.y = fy; aircraft.add(fuselage);
  box(`${name}Cockpit`, [r * 1.1, r * 0.22, r * 0.5], [0, fy + r * 0.62, length / 2 - r * 0.85], glass, aircraft);
  for (const side of [-1, 1]) {
    const label = side < 0 ? 'Left' : 'Right';
    box(`${name}Windows${label}`, [0.06, r * 0.16, length * 0.62], [side * r * 0.97, fy + r * 0.3, length * 0.02], glass, aircraft);
    box(`${name}Stripe${label}`, [0.06, r * 0.1, length * 0.72], [side * r * 0.99, fy - r * 0.12, 0], livery, aircraft);
  }

  const chord = length * (regional ? 0.12 : 0.15);
  const thickness = Math.max(0.18, r * 0.14);
  const semiSpan = span / 2 - r * 0.5;
  const sweep = regional ? 0.08 : 0.42;
  const wingY = regional ? fy + r * 0.78 : fy - r * 0.55;
  const wingZ = -length * 0.03;
  const wingCentreX = r * 0.5 + semiSpan / 2;
  for (const side of [-1, 1]) {
    const wing = box(`${name}Wing${side < 0 ? 'Left' : 'Right'}`, [semiSpan, thickness, chord], [side * wingCentreX, wingY, wingZ], body, aircraft);
    wing.rotation.y = -side * sweep;
  }

  const tailZ = -length / 2 + length * 0.1;
  const finBase = fy + r * 0.5;
  const finHeight = height - finBase;
  const fin = box(`${name}Fin`, [Math.max(0.22, r * 0.14), finHeight, length * 0.14], [0, finBase + finHeight / 2, tailZ], livery, aircraft);
  fin.rotation.x = -0.28;
  const stabiliserY = regional ? height - 0.2 : fy + r * 0.25;
  const stabiliser = box(`${name}Stabiliser`, [span * (regional ? 0.3 : 0.36), 0.16, length * 0.08], [0, stabiliserY, regional ? tailZ - length * 0.05 : tailZ - length * 0.02], body, aircraft);
  stabiliser.name = `${name}Stabiliser`;

  // Engine under the right wing; the left one is the manifest's engine node (buildEngine mirrors this one).
  const engineRadius = r * (regional ? 0.42 : 0.5);
  const engineLength = length * 0.16;
  const engineX = regional ? r + engineRadius + 0.4 : span * 0.18;
  const wingZAtEngine = wingZ + (engineX - wingCentreX) * Math.tan(sweep);
  const engineY = Math.max(engineRadius + 0.3, wingY - thickness / 2 - 0.12 - engineRadius);
  const engine = new THREE.Mesh(new THREE.CylinderGeometry(engineRadius, engineRadius * 0.82, engineLength, 20), trim);
  engine.name = `RightEngine${letter}`; engine.rotation.x = Math.PI / 2; engine.position.set(engineX, engineY, wingZAtEngine + chord * 0.45); aircraft.add(engine);
  const intake = new THREE.Mesh(new THREE.CylinderGeometry(engineRadius * 0.78, engineRadius * 0.78, 0.04, 20), glass);
  intake.name = `RightEngine${letter}Intake`; intake.rotation.x = Math.PI / 2; intake.position.set(engineX, engineY, engine.position.z + engineLength / 2 + 0.01); aircraft.add(intake);

  const wheelRadius = Math.max(0.25, gearHeight * 0.28);
  for (const [label, x, z] of [['Left', -r * 0.85, -length * 0.05], ['Right', r * 0.85, -length * 0.05], ['Nose', 0, length / 2 - length * 0.12]] as Array<[string, number, number]>) {
    const strutTop = fy - r * (label === 'Nose' ? 0.8 : 0.6);
    const strut = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.1, strutTop - wheelRadius, 8), trim);
    strut.name = `${name}LandingGear${label}`; strut.position.set(x, wheelRadius + (strutTop - wheelRadius) / 2, z); aircraft.add(strut);
    const wheel = new THREE.Mesh(new THREE.CylinderGeometry(wheelRadius, wheelRadius, 0.3, 14), glass);
    wheel.name = `${name}Wheel${label}`; wheel.rotation.z = Math.PI / 2; wheel.position.set(x, wheelRadius, z); aircraft.add(wheel);
  }

  aircraft.updateMatrixWorld(true);
  let bounds = new THREE.Box3().setFromObject(aircraft);
  const size = bounds.getSize(new THREE.Vector3());
  aircraft.scale.set(span / Math.max(size.x, 0.001), height / Math.max(size.y, 0.001), length / Math.max(size.z, 0.001));
  aircraft.updateMatrixWorld(true);
  bounds = new THREE.Box3().setFromObject(aircraft);
  aircraft.position.set(layout.centre.x - (bounds.min.x + bounds.max.x) / 2, -bounds.min.y, layout.centre.z - (bounds.min.z + bounds.max.z) / 2);
  aircraft.updateMatrixWorld(true);
  fuselage.userData.objectId = objectId;
  return aircraft;
}

function buildEngine(layout: any, model: any, parent: THREE.Group, aircraft: THREE.Group) {
  const nodes = model.nodes;
  const engine = new THREE.Group(); engine.name = nodes.engine;
  const right = aircraft.getObjectByName(`RightEngine${nodes.aircraft.slice(-1)}`) as THREE.Mesh;
  const rightIntake = aircraft.getObjectByName(`RightEngine${nodes.aircraft.slice(-1)}Intake`) as THREE.Mesh;
  const mirror = (mesh: THREE.Mesh, meshName: string, mat: THREE.Material) => {
    const position = mesh.getWorldPosition(new THREE.Vector3());
    const copy = new THREE.Mesh(mesh.geometry.clone(), mat);
    copy.name = meshName; copy.rotation.x = mesh.rotation.x; copy.rotation.y = mesh.rotation.y; copy.rotation.z = mesh.rotation.z; copy.scale.copy(aircraft.scale);
    copy.position.set(2 * layout.centre.x - position.x, position.y, position.z);
    engine.add(copy); return copy;
  };
  const visible = mirror(right, `${nodes.engine}Visible`, material(HANGAR_SCENE_THEME.aircraftTrim));
  visible.userData.objectId = nodes.objectIds[1];
  mirror(rightIntake, `${nodes.engine}Intake`, material(HANGAR_SCENE_THEME.rack));
  visible.geometry.computeBoundingSphere();
  const visibleRadius = (visible.geometry.boundingSphere?.radius ?? 0) * Math.max(aircraft.scale.x, aircraft.scale.y, aircraft.scale.z);
  const pickRadius = Math.max(1, 1.25 * visibleRadius);
  const pickMaterial = material(HANGAR_SCENE_THEME.groundShadow, true); pickMaterial.visible = false;
  const pick = new THREE.Mesh(new THREE.SphereGeometry(pickRadius, 12, 8), pickMaterial);
  pick.name = `${nodes.engine}PickSphere`; pick.position.copy(visible.position); pick.userData.pickObjectId = nodes.objectIds[1]; engine.add(pick);
  parent.add(engine);
  return engine;
}

export function buildHangarAircraftSceneGroups(manifest: unknown = manifestDefault, sceneView: HangarSceneView = null, selectionHandler?: HangarSelectionHandler | null): THREE.Group | null {
  const descriptor = validateHangarAircraftDescriptor(manifest);
  if (!descriptor.valid || !viewIsValid(sceneView)) return null;
  const m = manifest as any; const root = new THREE.Group(); root.name = 'HangarAircraftScene';
  addHangarLighting(root, m);

  const floor = new THREE.Mesh(new THREE.BoxGeometry(m.layout.floorSlab.width, m.layout.floorSlab.height, m.layout.floorSlab.depth), material(HANGAR_SCENE_THEME.floor));
  floor.name = 'Floor'; floor.position.y = -m.layout.floorSlab.height / 2; root.add(floor);

  for (const layout of m.layout.aircraft) {
    const footprint = layout.footprint;
    if (!footprint || footprint.minX >= footprint.maxX || footprint.minZ >= footprint.maxZ) return null;
    const outside = 1;
    const minX = footprint.minX - outside, maxX = footprint.maxX + outside;
    const minZ = footprint.minZ - outside, maxZ = footprint.maxZ + outside;
    const markingMaterial = material(HANGAR_SCENE_THEME.floorMarking, true);
    const letter = layout.caseId.slice(-1).toUpperCase();
    const width = maxX - minX, depth = maxZ - minZ;
    for (const [name, size, position] of [
      [`FloorMarking${letter}North`, [width, 0.02, 0.12], [(minX + maxX) / 2, 0.025, maxZ]],
      [`FloorMarking${letter}South`, [width, 0.02, 0.12], [(minX + maxX) / 2, 0.025, minZ]],
      [`FloorMarking${letter}East`, [0.12, 0.02, depth], [maxX, 0.025, (minZ + maxZ) / 2]],
      [`FloorMarking${letter}West`, [0.12, 0.02, depth], [minX, 0.025, (minZ + maxZ) / 2]],
    ] as Array<[string, [number,number,number], [number,number,number]]>) {
      box(name, size, position, markingMaterial, root);
    }
  }

  const rackGroup = new THREE.Group(); rackGroup.name = 'Rack'; root.add(rackGroup);
  const rackFootprint = m.layout.rack.footprint;
  if (m.layout.avatarStart && ('objectId' in m.layout.avatarStart || 'selectionHandler' in m.layout.avatarStart)) return null;
  if ('objectId' in m.avatar.hardHat || 'selectionHandler' in m.avatar.hardHat) return null;
  if ('objectId' in m.layout.rack || 'selectionHandler' in m.layout.rack || m.rack.units?.some((unit: any) => unit?.objectId != null || unit?.selectionHandler != null)) return null;
  if ('objectId' in m.layout.floorSlab || 'selectionHandler' in m.layout.floorSlab) return null;
  box('RackFrame', [rackFootprint.maxX - rackFootprint.minX, m.layout.rack.height, rackFootprint.maxZ - rackFootprint.minZ], [
    (rackFootprint.minX + rackFootprint.maxX) / 2, m.layout.rack.height / 2, (rackFootprint.minZ + rackFootprint.maxZ) / 2,
  ], material(HANGAR_SCENE_THEME.rack), rackGroup);
  if (!Array.isArray(m.rack?.units) || m.rack.units.length !== 5) return null;
  for (let i = 0; i < m.rack.units.length; i++) {
    const unit = m.rack.units[i];
    const unitName = unit?.node;
    if (!unitName || unitName !== ['RackUnitTechnical','RackUnitMaintenance','RackUnitFinance','RackUnitRecords','RackUnitContracts'][i]) return null;
    const unitMesh = box(unitName, [0.34, 1.7, 0.78], [rackFootprint.minX + 0.5 + i * 0.0, 1.0, rackFootprint.maxZ - 0.39], material(HANGAR_SCENE_THEME.rackUnit), rackGroup);
    unitMesh.position.x = rackFootprint.minX + (i + 0.5) * ((rackFootprint.maxX - rackFootprint.minX) / 5);
  }

  const rackCentreX = (rackFootprint.minX + rackFootprint.maxX) / 2;
  const rackCentreZ = (rackFootprint.minZ + rackFootprint.maxZ) / 2;
  for (let i = 0; i < 5; i++) {
    const led = new THREE.Mesh(new THREE.SphereGeometry(0.07, 8, 6), material(HANGAR_SCENE_THEME.guideLine, true));
    led.name = `RackLed${i}`; led.position.set(rackFootprint.minX + (i + 0.5) * ((rackFootprint.maxX - rackFootprint.minX) / 5), 1.95, rackFootprint.maxZ + 0.02); rackGroup.add(led);
  }
  const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 2.4, 8), material(HANGAR_SCENE_THEME.aircraftTrim));
  pole.name = 'RackBeaconPole'; pole.position.set(rackCentreX, m.layout.rack.height + 1.2, rackCentreZ); rackGroup.add(pole);
  const beacon = new THREE.Mesh(new THREE.SphereGeometry(0.35, 16, 12), material(HANGAR_SCENE_THEME.guideLine, true));
  beacon.name = 'RackBeacon'; beacon.position.set(rackCentreX, m.layout.rack.height + 2.5, rackCentreZ); rackGroup.add(beacon);
  const zone = m.layout.interactionCircle;
  if (zone?.centre && Number.isFinite(zone.radius)) {
    const pad = new THREE.Mesh(new THREE.CircleGeometry(zone.radius, 40), material(HANGAR_SCENE_THEME.guideLine, true, 0.28));
    pad.name = 'RackInteractionPad'; pad.rotation.x = -Math.PI / 2; pad.position.set(zone.centre.x, 0.03, zone.centre.z); rackGroup.add(pad);
    const rim = new THREE.Mesh(new THREE.TorusGeometry(zone.radius, 0.05, 6, 48), material(HANGAR_SCENE_THEME.guideLine, true));
    rim.name = 'RackInteractionRim'; rim.rotation.x = -Math.PI / 2; rim.position.set(zone.centre.x, 0.04, zone.centre.z); rackGroup.add(rim);
  }

  const walls = new THREE.Group(); walls.name = 'FloorWalls'; root.add(walls);
  const halfWidth = m.layout.floorSlab.width / 2, halfDepth = m.layout.floorSlab.depth / 2, wallHeight = 14;
  const wallMaterial = material(HANGAR_SCENE_THEME.partsStand);
  box('FloorWallBack', [m.layout.floorSlab.width, wallHeight, 0.4], [0, wallHeight / 2, -halfDepth - 0.2], wallMaterial, walls);
  box('FloorWallLeft', [0.4, wallHeight, m.layout.floorSlab.depth], [-halfWidth - 0.2, wallHeight / 2, 0], wallMaterial, walls);
  box('FloorWallRight', [0.4, wallHeight, m.layout.floorSlab.depth], [halfWidth + 0.2, wallHeight / 2, 0], wallMaterial, walls);

  const avatarStart = m.layout.avatarStart;
  const avatar = new THREE.Group(); avatar.name = 'Avatar'; avatar.position.set(avatarStart.x, 0, avatarStart.z); root.add(avatar);
  const body = new THREE.Mesh(new THREE.CapsuleGeometry(0.425, 0.85, 8, 12), material(HANGAR_SCENE_THEME.avatarBody));  body.name = 'AvatarBody'; body.position.y = 0.85; avatar.add(body);
  const head = new THREE.Mesh(new THREE.SphereGeometry(0.34, 16, 12), material(HANGAR_SCENE_THEME.avatarBody));
  head.name = 'AvatarHead'; head.position.y = 1.56; avatar.add(head);
  const hardHat = new THREE.Mesh(new THREE.SphereGeometry(0.38, 16, 10, 0, Math.PI * 2, 0, Math.PI / 2), material(m.avatar.hardHat.color));
  hardHat.name = m.avatar.hardHat.node; hardHat.position.y = 1.86; hardHat.scale.y = 0.55; avatar.add(hardHat);
  const locator = new THREE.Mesh(new THREE.TorusGeometry(0.75, 0.06, 6, 32), material(m.avatar.hardHat.color, true));
  locator.name = 'AvatarLocator'; locator.rotation.x = -Math.PI / 2; locator.position.y = 0.04; avatar.add(locator);

  for (let i = 0; i < 3; i++) {
    const model = m.aircraft.models[i], layout = m.layout.aircraft[i];
    const caseGroup = new THREE.Group(); caseGroup.name = `Case${model.nodes.aircraft.slice(-1)}`; caseGroup.userData.caseId = layout.caseId;
    const aircraft = buildAircraft(layout, model); caseGroup.add(aircraft);

    buildEngine(layout, model, caseGroup, aircraft);
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
      component.add(visible);
      visible.geometry.computeBoundingSphere();
      const visibleRadius = visible.geometry.boundingSphere?.radius ?? 0;
      const pickRadius = Math.max(1, 1.25 * visibleRadius);
      const pickMaterial = material(HANGAR_SCENE_THEME.groundShadow, true); pickMaterial.visible = false;
      const pick = new THREE.Mesh(new THREE.SphereGeometry(pickRadius, 12, 8), pickMaterial); pick.name = `${componentName}PickSphere`; pick.position.copy(visible.position); pick.userData.pickObjectId = model.nodes.objectIds[c + 1]; component.add(pick);
      stand.add(component);
    }
    const shadow = new THREE.Mesh(new THREE.CircleGeometry(1, 32), material(HANGAR_SCENE_THEME.groundShadow, true, 0.18));
    shadow.name = `GroundShadow${model.nodes.aircraft.slice(-1)}`; shadow.rotation.x = -Math.PI / 2; shadow.position.set(layout.centre.x, 0.01, layout.centre.z); shadow.scale.set(layout.dimensions.span / 2, layout.dimensions.length / 2, 1); caseGroup.add(shadow);
    root.add(caseGroup);
  }

  const route = sceneRoute(sceneView);
  if (sceneRouteCandidate(sceneView)) {
    if (!route || route.length < 2) throw new Error(HANGAR_ROUTE_ERROR);
    if (!route.every(point => pointInsideFloor(point.x, point.z, m) && Number.isFinite(point.x) && Number.isFinite(point.z))) throw new Error(HANGAR_ROUTE_ERROR);
  }
  if (route && route.length >= 2) {
    const curve = new THREE.CatmullRomCurve3(route.map(point => new THREE.Vector3(point.x, 0.05, point.z)));
    const tube = new THREE.Mesh(new THREE.TubeGeometry(curve, Math.max(2, route.length * 12), 0.16, 8, false), material(HANGAR_SCENE_THEME.guideLine, true));
    tube.name = 'GuideLine'; tube.userData.routePoints = route.map(point => ({x: point.x, y: 0.05, z: point.z})); root.add(tube);
    const end = route[route.length - 1];
    const marker = new THREE.Mesh(new THREE.TorusGeometry(1.5, 0.08, 6, 40), material(HANGAR_SCENE_THEME.guideLine, true));
    marker.name = 'GuideLineDestination'; marker.rotation.x = -Math.PI / 2; marker.position.set(end.x, 0.06, end.z); root.add(marker);
  }

  const selectedId = sceneSelection(sceneView);
  if (selectedId) {
    let selected: THREE.Object3D | undefined;
    root.traverse(object => { if (typeof object.userData.objectId === 'string' && object.userData.objectId === selectedId) selected = selected ?? object; });
    if (!selected && /^SYN-CP-[ABC]-AIR-001$/.test(selectedId)) {
      root.traverse(object => { if (object.name === `Aircraft${selectedId.slice(8,9)}`) selected = selected ?? object; });
    }
    if (selected) {
      // An aircraft is highlighted as a whole, not only its fuselage mesh.
      let highlight: THREE.Object3D = selected;
      const aircraftLetter = /^SYN-CP-([ABC])-AIR-001$/.exec(selectedId)?.[1];
      if (aircraftLetter) root.traverse(object => { if (object.name === `Aircraft${aircraftLetter}`) highlight = object; });
      const bounds = new THREE.Box3().setFromObject(highlight);
      const size = bounds.getSize(new THREE.Vector3());
      const centre = bounds.getCenter(new THREE.Vector3());
      const radius = Math.max(size.x, size.z, 1) * 0.58;
      const ring = new THREE.Mesh(new THREE.TorusGeometry(radius, Math.max(0.18, radius * 0.025), 12, 64), material(HANGAR_SCENE_THEME.selection));
      ring.name = 'SelectionRing'; ring.rotation.x = Math.PI / 2; ring.position.set(centre.x, 0.05, centre.z); root.add(ring);
      highlight.traverse(object => {
        if (!(object instanceof THREE.Mesh) || !(object.material instanceof THREE.MeshStandardMaterial) || object.material.visible === false) return;
        object.material.emissive.set(HANGAR_SCENE_THEME.selection);
        object.material.emissiveIntensity = 0.28;
      });
    }
  }

  if (selectionHandler) root.traverse((object) => { if (typeof object.userData.objectId === 'string') object.userData.selectionHandler = selectionHandler; });
  return root;
}

export const buildHangarScene = buildHangarAircraftSceneGroups;
export const buildHangarSceneGroups = buildHangarAircraftSceneGroups;
export const buildHangarAircraftScene = buildHangarAircraftSceneGroups;
export const buildRealAircraftSceneGroups = buildHangarAircraftSceneGroups;
