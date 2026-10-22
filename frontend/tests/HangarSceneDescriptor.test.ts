import { describe, expect, it, vi } from 'vitest';
import manifest from '../src/hangar/sceneManifest.json';
import { activateHangarSceneObject, validateHangarAircraftDescriptor } from '../src/hangar/sceneDescriptor';
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
