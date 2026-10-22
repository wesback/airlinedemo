import { createElement } from 'react';
import hangarProvenance from '../assets/hangar/hangar-provenance.json';
import { HANGAR_ASSET_INVALID } from './assetPolicy';
import { loadValidatedHangarAsset } from './hangarAssetLoader';
import { validateHangarAircraftDescriptor } from './sceneDescriptor';
export type HangarAircraftAsset='aircraft-a'|'aircraft-b'|'aircraft-c';
export function loadHangarAircraftAsset<T>(asset:HangarAircraftAsset,bytes:unknown,options:{loader:(bytes:Uint8Array)=>T;onFallback:(reason:typeof HANGAR_ASSET_INVALID)=>void;aggregateBytes:number;resource?:unknown}){const descriptor=validateHangarAircraftDescriptor();if(!descriptor.valid){options.onFallback(HANGAR_ASSET_INVALID);return{loaded:false as const,reason:HANGAR_ASSET_INVALID};}const suffix=asset.endsWith('-a')?'a':asset.endsWith('-b')?'b':'c',path=`assets/hangar/aircraft-${suffix}.glb`;const provenance=(hangarProvenance.assets as any[]).find(r=>r.path===path);if(!provenance||provenance.synthetic!==true||provenance.realAircraftType!==false||provenance.brandMarks!==false||provenance.type!== 'model'||typeof provenance.syntheticMarkerDeclaration!== 'string'||!provenance.syntheticMarkerDeclaration.includes('Synthetic')){options.onFallback(HANGAR_ASSET_INVALID);return{loaded:false as const,reason:HANGAR_ASSET_INVALID};}return loadValidatedHangarAsset(bytes,{...options,resource:options.resource??{path,type:'model'},provenance:{...provenance,assetBytes:bytes}});}
export const loadAircraftAsset = loadHangarAircraftAsset;
export const loadAircraftHangarAsset = loadHangarAircraftAsset;
export function HangarCanvasAdapter(){return createElement('div',{'data-hangar-canvas':'admitted'});}export default HangarCanvasAdapter;
