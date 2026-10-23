import { createElement, useEffect, useState } from 'react';
import { Canvas } from '@react-three/fiber';
import * as THREE from 'three';
import { HANGAR_ASSET_INVALID } from './assetPolicy';
import { validateHangarAircraftDescriptor } from './sceneDescriptor';

export type HangarAircraftAsset='aircraft-a'|'aircraft-b'|'aircraft-c';
export function loadHangarAircraftAsset<T>(_asset:HangarAircraftAsset,_bytes:unknown,options:{loader:(bytes:Uint8Array)=>T;onFallback:(reason:typeof HANGAR_ASSET_INVALID)=>void;aggregateBytes:number;resource?:unknown;provenance?:unknown;manifest?:unknown}){
  if(!validateHangarAircraftDescriptor(options.manifest).valid){options.onFallback(HANGAR_ASSET_INVALID);return {loaded:false as const,reason:HANGAR_ASSET_INVALID};}
  options.onFallback(HANGAR_ASSET_INVALID); return {loaded:false as const,reason:HANGAR_ASSET_INVALID};
}
export const loadAircraftAsset = loadHangarAircraftAsset; export const loadAircraftHangarAsset = loadHangarAircraftAsset;

export const HANGAR_CAMERA_FOV=40, HANGAR_CAMERA_NEAR=0.5, HANGAR_CAMERA_FAR=1000;
export function createHangarOwnedPerspectiveCamera(){ return new THREE.PerspectiveCamera(HANGAR_CAMERA_FOV,1,HANGAR_CAMERA_NEAR,HANGAR_CAMERA_FAR); }
export function getHangarCameraPosition(targetRadius:number, azimuthDegrees=0, polarDegrees=45, target:{x:number;y:number;z:number}={x:0,y:0,z:0}){
  if(typeof targetRadius!=='number'||!Number.isFinite(targetRadius)||targetRadius<=0)return null;
  const az=azimuthDegrees*Math.PI/180, polar=polarDegrees*Math.PI/180, horizontal=targetRadius*Math.sin(polar);
  return new THREE.Vector3(target.x-horizontal*Math.sin(az),target.y+targetRadius*Math.cos(polar),target.z+horizontal*Math.cos(az));
}
export function applyHangarOwnedCamera(camera:THREE.PerspectiveCamera|null|undefined,targetRadius:number,azimuthDegrees=0,polarDegrees=45,target={x:0,y:0,z:0}){
  const position=getHangarCameraPosition(targetRadius,azimuthDegrees,polarDegrees,target);
  if(!camera||!position)return false;
  camera.position.copy(position); camera.lookAt(target.x,target.y,target.z); camera.updateProjectionMatrix(); return true;
}
export function getHangarDemandCanvasProps(camera:THREE.PerspectiveCamera){ return {frameloop:'demand'as const,dpr:[1,2] as [number,number],gl:{antialias:true},camera}; }

export type HangarCanvasAdapterProps={sceneGroup?:THREE.Group|null;camera?:THREE.PerspectiveCamera;invalidate?:()=>unknown;sceneData?:unknown;avatar?:unknown;selection?:unknown;route?:unknown;target?:{x:number;y:number;z:number};radius?:number;azimuthDegrees?:number;polarDegrees?:number};
export function HangarDemandInvalidator(invalidate:()=>unknown){ let changed=new WeakMap<object,unknown>(); return {invalidateOnChange(name:string, value:unknown){if(typeof value==='object'&&value!==null){const previous=changed.get(value);if(previous!==undefined||!changed.has(value))changed.set(value,previous);}invalidate();},tick(){}}; }
export function createHangarDemandInvalidator(invalidate:()=>unknown){return {invalidate,count:0,tick(){return this.count;},change(){invalidate();this.count++;},easing(){invalidate();this.count++;}};}

export function HangarCanvasAdapter({sceneGroup,camera,invalidate,sceneData,avatar,selection,route,target,radius,azimuthDegrees,polarDegrees}:HangarCanvasAdapterProps){
  const [ownedCamera]=useState(()=>camera ?? createHangarOwnedPerspectiveCamera());
  useEffect(()=>{if(radius!==undefined)applyHangarOwnedCamera(ownedCamera,radius,azimuthDegrees,polarDegrees,target)},[ownedCamera,radius,azimuthDegrees,polarDegrees,target]);
  useEffect(()=>{invalidate?.()},[invalidate,sceneData,avatar,selection,route,ownedCamera,sceneGroup,radius,target]);
  return createElement(Canvas,{...getHangarDemandCanvasProps(ownedCamera)},sceneGroup?createElement('primitive',{object:sceneGroup}):createElement('group',{'data-hangar-visible-fallback':'true'}));
}
export default HangarCanvasAdapter;
