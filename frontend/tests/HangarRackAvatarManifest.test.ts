import { describe, expect, it, vi } from 'vitest';
import manifest from '../src/hangar/sceneManifest.json';
import { HANGAR_SCENE_THEME } from '../src/hangar/sceneTheme';
import { createHangarRackMenuController } from '../src/hangar/rackMenu';
import {
  HANGAR_MANIFEST_ERROR,
  HANGAR_AVATAR_HARD_HAT,
  HANGAR_HARD_HAT_COLOR,
  validateHangarRackAvatarManifest,
  buildHangarRackDescriptor,
  openValidatedHangarRack,
} from '../src/hangar/dataRoomAdapter';

const clone = () => JSON.parse(JSON.stringify(manifest));

describe('Hangar rack and avatar manifest', () => {
  it('hangar label contrast and synthetic declaration contract', () => {
    const result = validateHangarRackAvatarManifest(manifest);
    expect(result.valid).toBe(true);
    const lowText = clone(); lowText.avatar.synthetic = false;
    expect(validateHangarRackAvatarManifest(lowText)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
    const realPerson = clone(); realPerson.avatar.realPerson = true;
    expect(validateHangarRackAvatarManifest(realPerson)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
    const brandMarks = clone(); brandMarks.avatar.brandMarks = true;
    expect(validateHangarRackAvatarManifest(brandMarks)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
    const missingSynthetic = clone(); delete missingSynthetic.avatar.synthetic;
    expect(validateHangarRackAvatarManifest(missingSynthetic)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
    for (let modelIndex = 0; modelIndex < 3; modelIndex += 1) {
      for (const field of ['synthetic', 'realAircraftType', 'realPerson', 'brandMarks']) {
        const missing = clone(); delete missing.aircraft.models[modelIndex][field];
        expect(validateHangarRackAvatarManifest(missing)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
        const incorrect = clone(); incorrect.aircraft.models[modelIndex][field] = field === 'synthetic' ? false : true;
        expect(validateHangarRackAvatarManifest(incorrect)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
      }
    }
    const originalLabelText = HANGAR_SCENE_THEME.labelText;
    const originalLabelMuted = HANGAR_SCENE_THEME.labelMuted;
    (HANGAR_SCENE_THEME as { labelText: string }).labelText = '#FFFFFF';
    expect(validateHangarRackAvatarManifest(manifest)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
    (HANGAR_SCENE_THEME as { labelText: string }).labelText = originalLabelText;
    (HANGAR_SCENE_THEME as { labelMuted: string }).labelMuted = '#FFFFFF';
    expect(validateHangarRackAvatarManifest(manifest)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
    (HANGAR_SCENE_THEME as { labelMuted: string }).labelMuted = originalLabelMuted;
  });

  it('Hangar rack manifest exact five-unit mapping', () => {
    const result = validateHangarRackAvatarManifest(manifest);
    expect(result.valid).toBe(true);
    if (!result.valid) throw new Error(result.reason);
    expect(result.units.map(unit => unit.source)).toEqual(['technical', 'maintenance', 'finance', 'records', 'contracts']);
    expect(result.units).toHaveLength(5);
    expect(buildHangarRackDescriptor(manifest)).toEqual({ valid: true, units: result.units, openRack: true });
    const loader = vi.fn();
    const opened = vi.fn();
    expect(openValidatedHangarRack(opened, manifest, { count: 0, loader })).toEqual({ opened: true, value: undefined });
    expect(opened).toHaveBeenCalledTimes(1);
    expect(loader).toHaveBeenCalledTimes(1);

    const duplicate = clone(); duplicate.rack.units[1].source = 'technical';
    const missing = clone(); delete missing.rack.units[2].source;
    const extra = clone(); extra.rack.units.push({ node: 'RackUnitExtra', source: 'extra' });
    const unknown = clone(); unknown.rack.units[4].node = 'UnknownNode';
    for (const bad of [duplicate, missing, extra, unknown]) {
      const badOpened = vi.fn(); const badLoader = vi.fn(); const loaderCalls = { count: 0, loader: badLoader };
      expect(validateHangarRackAvatarManifest(bad)).toEqual({ valid: false, reason: HANGAR_MANIFEST_ERROR });
      expect(buildHangarRackDescriptor(bad).valid).toBe(false);
      expect(openValidatedHangarRack(badOpened, bad, loaderCalls)).toEqual({ opened: false, reason: HANGAR_MANIFEST_ERROR });
      expect(badOpened).not.toHaveBeenCalled();
      expect(badLoader).not.toHaveBeenCalled();

      const rackController = createHangarRackMenuController([], { manifest: bad, loaderCalls });
      expect(rackController.open()).toBe(false);
      expect(rackController.getState()).toEqual(expect.objectContaining({ open: false, error: HANGAR_MANIFEST_ERROR }));
      expect(badOpened).not.toHaveBeenCalled();
      expect(badLoader).not.toHaveBeenCalled();
    }
    expect(loader).toHaveBeenCalledTimes(1);

    expect(result.units.map(unit => unit.node)).toEqual(['RackUnitTechnical', 'RackUnitMaintenance', 'RackUnitFinance', 'RackUnitRecords', 'RackUnitContracts']);
  });

  it('Hangar avatar synthetic identity and hard-hat contract', () => {
    expect(validateHangarRackAvatarManifest(manifest).valid).toBe(true);
    expect(manifest.avatar.synthetic).toBe(true);
    expect(manifest.avatar.realPerson).toBe(false);
    expect(manifest.avatar.brandMarks).toBe(false);
    expect(manifest.avatar.hardHat.node).toBe(HANGAR_AVATAR_HARD_HAT);
    expect(manifest.avatar.hardHat.color).toBe(HANGAR_HARD_HAT_COLOR);
    for (const key of ['serverStatus', 'role', 'permission']) expect(JSON.stringify(manifest.avatar)).not.toContain(key);

    expect(manifest.avatar.hardHat.node).toBe('HardHat');
    for (const bad of ['realPerson', 'brandMarks', 'hardHat']) {
      const changed = clone();
      if (bad === 'hardHat') changed.avatar.hardHat = {};
      else changed.avatar[bad] = bad === 'realPerson' ? true : true;
      expect(validateHangarRackAvatarManifest(changed).valid).toBe(false);
    }
  });
});
