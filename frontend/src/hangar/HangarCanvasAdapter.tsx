import { createElement } from 'react';
import { HANGAR_ASSET_INVALID } from './assetPolicy';
import { validateHangarAircraftDescriptor } from './sceneDescriptor';
export type HangarAircraftAsset='aircraft-a'|'aircraft-b'|'aircraft-c';
export function loadHangarAircraftAsset<T>(_asset:HangarAircraftAsset,_bytes:unknown,options:{loader:(bytes:Uint8Array)=>T;onFallback:(reason:typeof HANGAR_ASSET_INVALID)=>void;aggregateBytes:number;resource?:unknown;provenance?:unknown}){
  if(!validateHangarAircraftDescriptor().valid){options.onFallback(HANGAR_ASSET_INVALID);return {loaded:false as const,reason:HANGAR_ASSET_INVALID};}
  options.onFallback(HANGAR_ASSET_INVALID);
  return {loaded:false as const,reason:HANGAR_ASSET_INVALID};
}
export const loadAircraftAsset = loadHangarAircraftAsset;
export const loadAircraftHangarAsset = loadHangarAircraftAsset;
export function HangarCanvasAdapter(){return createElement('div',{'data-hangar-canvas':'admitted'});}
export default HangarCanvasAdapter;
