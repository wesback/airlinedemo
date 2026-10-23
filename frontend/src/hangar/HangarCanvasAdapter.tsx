import { createElement, useEffect, useState, type ComponentType } from 'react';
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

export type HangarCanvasAdapterProps={sceneGroup?:THREE.Group|null;camera?:THREE.PerspectiveCamera;invalidate?:()=>unknown;invalidationCapture?:ComponentType<any>;sceneData?:unknown;avatar?:unknown;selection?:unknown;route?:unknown;target?:{x:number;y:number;z:number};radius?:number;azimuthDegrees?:number;polarDegrees?:number;reducedMotion?:boolean;canvasComponent?:ComponentType<any>};
export function createHangarDemandInvalidator(invalidate:()=>unknown){
  const values=new Map<string,unknown>(); let easingActive=false; let easingTargetDistance=Infinity;
  const invalidateOnChange=(name:string,value:unknown)=>{if(!Object.is(values.get(name),value)){values.set(name,value);invalidate();return true;}return false;};
  return {invalidate,count:0,values,invalidateOnChange,
    tick(){return this.count;},
    change(nameOrValue?:unknown,value?:unknown){const name=typeof nameOrValue==='string'?nameOrValue:'change';const next=arguments.length>1?value:nameOrValue;if(invalidateOnChange(name,next)){this.count++;}},
    easing(current?:unknown,target?:unknown){if(current!==undefined&&target!==undefined){let distance=Infinity;if(typeof current==='number'&&typeof target==='number')distance=Math.abs(target-current);else if(current&&target&&typeof (current as any).distanceTo==='function')distance=(current as any).distanceTo(target);else if(Array.isArray(current)&&Array.isArray(target))distance=Math.sqrt(current.reduce((sum,v,i)=>sum+(v-(target[i]??0))**2,0));else if(current&&target&&typeof (current as any).x==='number'&&typeof (target as any).x==='number')distance=Math.hypot((current as any).x-(target as any).x,(current as any).y-(target as any).y,(current as any).z-(target as any).z);easingTargetDistance=distance;easingActive=distance>0.05;}else easingActive=true;if(easingActive){invalidate();this.count++;return true;}return false;},
    reset(){values.clear();easingActive=false;easingTargetDistance=Infinity;},
  };
}
export const HangarDemandInvalidator=createHangarDemandInvalidator;

function HangarDemandFrame({invalidate,camera,target,radius,azimuthDegrees,polarDegrees,reducedMotion}:HangarCanvasAdapterProps){
  const requestInvalidate = invalidate;
  useEffect(()=>{
    if(reducedMotion||!camera||typeof radius!== 'number'||!Number.isFinite(radius)||radius<=0)return;
    const desired=getHangarCameraPosition(radius,azimuthDegrees,polarDegrees,target);
    if(!desired)return;
    let active=true;
    const tick=()=>{
      if(!active)return;
      const distance=camera.position.distanceTo(desired);
      if(distance<=0.05)return;
      const nextX=camera.position.x+(desired.x-camera.position.x)*0.2;
      const nextY=camera.position.y+(desired.y-camera.position.y)*0.2;
      const nextZ=camera.position.z+(desired.z-camera.position.z)*0.2;
      camera.position.x=nextX;camera.position.y=nextY;camera.position.z=nextZ;
      camera.lookAt(target?.x??0,target?.y??0,target?.z??0);
      camera.updateProjectionMatrix();
      requestInvalidate?.();
      if(typeof requestAnimationFrame==='function')requestAnimationFrame(tick);
    };
    if(typeof requestAnimationFrame==='function')requestAnimationFrame(tick);
    return ()=>{active=false;};
  },[requestInvalidate,camera,target,radius,azimuthDegrees,polarDegrees,reducedMotion]);
  return null;
}

export function HangarCanvasAdapter({sceneGroup,camera,invalidate,invalidationCapture,sceneData,avatar,selection,route,target,radius,azimuthDegrees,polarDegrees,reducedMotion,canvasComponent}:HangarCanvasAdapterProps){
  const [ownedCamera,setOwnedCamera]=useState(()=>camera ?? createHangarOwnedPerspectiveCamera());
  useEffect(()=>{if(camera)setOwnedCamera(camera)},[camera]);
  useEffect(()=>{if(radius!==undefined&&Number.isFinite(radius)&&radius>0&&reducedMotion)applyHangarOwnedCamera(ownedCamera,radius,azimuthDegrees,polarDegrees,target)},[ownedCamera,radius,azimuthDegrees,polarDegrees,target,reducedMotion]);
  useEffect(()=>{
    if(!sceneGroup||!avatar||typeof avatar !== 'object')return;
    const position=(avatar as any); if(typeof position.x !== 'number'||typeof position.z !== 'number')return;
    let avatarNode:any=null;
    sceneGroup.traverse((node:any)=>{if(!avatarNode&&node.name==='Avatar')avatarNode=node;});
    if(avatarNode){avatarNode.position.x=position.x; avatarNode.position.z=position.z;}
  },[sceneGroup,avatar,reducedMotion]);
  useEffect(()=>{invalidate?.()},[invalidate,sceneData,avatar,selection,route,ownedCamera,sceneGroup,radius,target?.x,target?.y,target?.z,azimuthDegrees,polarDegrees]);
  if (!canvasComponent) return createElement('canvas',{'aria-label':'3D hangar scene','data-hangar-visible-fallback':'true'});
  const frame=createElement(HangarDemandFrame,{camera:ownedCamera,invalidate,target,radius,azimuthDegrees,polarDegrees,reducedMotion});
  const capture=invalidationCapture?createElement(invalidationCapture):null;
  return createElement(canvasComponent,{...getHangarDemandCanvasProps(ownedCamera)},sceneGroup?createElement('primitive',{object:sceneGroup}):createElement('group',{'data-hangar-visible-fallback':'true'}),capture,frame);
}
export default HangarCanvasAdapter;
