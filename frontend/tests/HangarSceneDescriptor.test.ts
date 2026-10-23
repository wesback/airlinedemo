import { describe, expect, it, vi } from 'vitest';
import manifest from '../src/hangar/sceneManifest.json';
import { activateHangarSceneObject, validateHangarAircraftDescriptor } from '../src/hangar/sceneDescriptor';
import { HANGAR_SCENE_THEME, hangarLabelContrastRatio, validateHangarSceneColor } from '../src/hangar/sceneTheme';
const clone=()=>JSON.parse(JSON.stringify(manifest));
describe('Hangar scene descriptor',()=>{
 it('Hangar descriptor exact 12-object wiring',()=>{
  const result=validateHangarAircraftDescriptor(manifest);
  expect(result.valid).toBe(true);
  expect(result.descriptor).toEqual([
   {objectId:'SYN-CP-A-AIR-001',caseId:'cp-case-a',kind:'aircraft',node:'AircraftA'},
   {objectId:'SYN-CP-A-ENG-001',caseId:'cp-case-a',kind:'engine',node:'EngineA'},
   {objectId:'SYN-CP-A-CMP-001',caseId:'cp-case-a',kind:'component',node:'ComponentA1'},
   {objectId:'SYN-CP-A-CMP-002',caseId:'cp-case-a',kind:'component',node:'ComponentA2'},
   {objectId:'SYN-CP-B-AIR-001',caseId:'cp-case-b',kind:'aircraft',node:'AircraftB'},
   {objectId:'SYN-CP-B-ENG-001',caseId:'cp-case-b',kind:'engine',node:'EngineB'},
   {objectId:'SYN-CP-B-CMP-001',caseId:'cp-case-b',kind:'component',node:'ComponentB1'},
   {objectId:'SYN-CP-B-CMP-002',caseId:'cp-case-b',kind:'component',node:'ComponentB2'},
   {objectId:'SYN-CP-C-AIR-001',caseId:'cp-case-c',kind:'aircraft',node:'AircraftC'},
   {objectId:'SYN-CP-C-ENG-001',caseId:'cp-case-c',kind:'engine',node:'EngineC'},
   {objectId:'SYN-CP-C-CMP-001',caseId:'cp-case-c',kind:'component',node:'ComponentC1'},
   {objectId:'SYN-CP-C-CMP-002',caseId:'cp-case-c',kind:'component',node:'ComponentC2'},
  ]);
  const handler=vi.fn();
  expect(activateHangarSceneObject(manifest,'SYN-CP-C-CMP-002',handler)).toEqual({activated:true,value:undefined});
  expect(handler).toHaveBeenCalledExactlyOnceWith('SYN-CP-C-CMP-002',{objectId:'SYN-CP-C-CMP-002',caseId:'cp-case-c',kind:'component',node:'ComponentC2'});
  for(const bad of [Object.assign(clone(),{packVersion:'wrong'}),(()=>{const x=clone();x.objectIds[1]=x.objectIds[2];return x})(),(()=>{const x=clone();x.objectIds.push('unknown-node');return x})(),(()=>{const x=clone();x.aircraft.models[0].nodes.engine='UnknownEngine';return x})(),(()=>{const x=clone();x.aircraft.models[1].nodes.objectIds[0]='SYN-CP-A-AIR-001';return x})()]){
   const selection=vi.fn();expect(validateHangarAircraftDescriptor(bad).valid).toBe(false);expect(activateHangarSceneObject(bad,'SYN-CP-A-AIR-001',selection)).toEqual(expect.objectContaining({activated:false}));expect(selection).not.toHaveBeenCalled();
  }
 });
});


describe('hangar scene manifest layout contract cases', () => {
  it('accepts exact 0.4.0 layout and rejects malformed contracts', () => {
    expect(validateHangarAircraftDescriptor(manifest)).toEqual(expect.objectContaining({ valid: true }));
    for (const bad of [
      (() => { const x = clone(); delete x.layout.aircraft[0].footprint; return x; })(),
      (() => { const x = clone(); x.rack.units[1].node = x.rack.units[0].node; return x; })(),
      (() => { const x = clone(); x.rack.units[1].node = 'ArbitraryRackNode'; return x; })(),
      (() => { const x = clone(); x.rack.units[1].source = 'telemetry'; return x; })(),
      (() => { const x = clone(); x.packVersion = '0.3.0'; return x; })(),
      (() => { const x = clone(); const a = x.layout.aircraft[0].dimensions; x.layout.aircraft[0].dimensions = x.layout.aircraft[2].dimensions; x.layout.aircraft[2].dimensions = a; return x; })(),
    ]) {
      expect(validateHangarAircraftDescriptor(bad)).toEqual({ valid: false, error: 'Visible mapping error' });
    }
  });

  it('hangar scene palette remains status independent', () => {
    expect(Object.keys(HANGAR_SCENE_THEME)).toHaveLength(16);
    expect(HANGAR_SCENE_THEME).toEqual({
      background:'#E8EDF2', floor:'#C3CBD4', floorMarking:'#F2F4F6', aircraftBody:'#F5F7F9', aircraftTrim:'#9AA5B1', partsStand:'#5A6573', component:'#B7C0CA', rack:'#2F3A47', rackUnit:'#4C5866', avatarBody:'#3D6E9E', groundShadow:'#000000', guideLine:'#0E7C66', selection:'#1F6FEB', labelBackground:'#FFFFFF', labelText:'#1B2430', labelMuted:'#4A5563'
    });
    expect(manifest.avatar.hardHat.color).toBe('#FFD400');
    const statusIndependent = clone();
    statusIndependent.status = 'blocked'; statusIndependent.owner = 'reviewer'; statusIndependent.nextAction = 'request'; statusIndependent.capabilityLabel = 'Permitted';
    expect(validateHangarAircraftDescriptor(statusIndependent).valid).toBe(true);
    expect(hangarLabelContrastRatio(HANGAR_SCENE_THEME.labelText)).toBeGreaterThanOrEqual(4.5);
    expect(hangarLabelContrastRatio(HANGAR_SCENE_THEME.labelMuted)).toBeGreaterThanOrEqual(4.5);

    expect(validateHangarSceneColor('#FF0000')).toEqual({ valid: false, error: 'Visible mapping error' });
    expect(validateHangarSceneColor('#00FF00')).toEqual({ valid: false, error: 'Visible mapping error' });
    expect(validateHangarSceneColor('#FFD400')).toEqual({ valid: false, error: 'Visible mapping error' });
    const outsideToken = clone(); outsideToken.layout.aircraft[0].materialColor = '#FF0000';
    expect(validateHangarAircraftDescriptor(outsideToken)).toEqual({ valid: false, error: 'Visible mapping error' });
    const outsideMeshToken = clone(); outsideMeshToken.layout.aircraft[0].meshColor = '#00FF00';
    expect(validateHangarAircraftDescriptor(outsideMeshToken)).toEqual({ valid: false, error: 'Visible mapping error' });
  });
});
