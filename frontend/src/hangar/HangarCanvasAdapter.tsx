import { createElement, useEffect, useMemo, useRef, useState, type ComponentType, type ReactNode } from 'react';
import * as THREE from 'three';
import { HANGAR_ASSET_INVALID } from './assetPolicy';
import { validateHangarAircraftDescriptor } from './sceneDescriptor';
import { HANGAR_SCENE_THEME } from './sceneTheme';
import { buildHangarAircraftLabelText, buildHangarSelectedObjectLabel, HANGAR_LABEL_MAPPING_ERROR } from './selectionPanel';

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
export function projectHangarLabelAnchor(camera:THREE.PerspectiveCamera|null|undefined,anchor:{x:number;y:number;z:number}|THREE.Vector3|null|undefined):{x:number;y:number;z:number;visible:boolean}|null{
  if(!camera||!anchor)return null;
  (camera as any).updateMatrixWorld(true);camera.updateProjectionMatrix();(camera as any).updateWorldMatrix(true,false);
  const point=anchor instanceof THREE.Vector3?(anchor as any).clone():new THREE.Vector3(anchor.x,anchor.y,anchor.z);
  const toAnchor=(point as any).clone().sub(camera.position); const direction=new THREE.Vector3();(camera as any).getWorldDirection(direction);
  if((toAnchor as any).dot(direction)<=0)return {x:0,y:0,z:0,visible:false};
  (point as any).project(camera); const visible=Number.isFinite(point.x)&&Number.isFinite(point.y)&&Number.isFinite(point.z)&&point.x>=-1&&point.x<=1&&point.y>=-1&&point.y<=1&&point.z>=-1&&point.z<=1;
  return {x:point.x,y:point.y,z:point.z,visible};
}
export const projectHangarLabelToNdc=projectHangarLabelAnchor;
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
      if(camera.position.distanceTo(desired)<=0.05){camera.position.copy(desired);camera.lookAt(target?.x??0,target?.y??0,target?.z??0);camera.updateProjectionMatrix();return;}
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
  useEffect(()=>{invalidate?.()},[selection]);
  if (!canvasComponent) return createElement('canvas',{'aria-label':'3D hangar scene','data-hangar-visible-fallback':'true'});
  const frame=createElement(HangarDemandFrame,{camera:ownedCamera,invalidate,target,radius,azimuthDegrees,polarDegrees,reducedMotion});
  const capture=invalidationCapture?createElement(invalidationCapture):null;
  const canvas=createElement(canvasComponent,{...getHangarDemandCanvasProps(ownedCamera)},sceneGroup?createElement('primitive',{object:sceneGroup}):createElement('group',{'data-hangar-visible-fallback':'true'}),capture,frame);
  return createElement(HangarLabelOverlay,{sceneGroup,camera:ownedCamera,sceneData,selection},canvas);
}

function HangarLabelOverlay({sceneGroup,camera,sceneData,selection,children}:{sceneGroup?:THREE.Group|null;camera:THREE.PerspectiveCamera;sceneData?:unknown;selection?:unknown;children?:ReactNode}){
  const host=useRef<HTMLDivElement|null>(null);
  const data=useMemo(()=>{
    const entries:Array<{id:string;text:string;anchor:THREE.Object3D|null;kind:'aircraft'|'selected'}>=[];
    if(!sceneGroup)return entries;
    const root=typeof sceneData==='object'&&sceneData!==null?sceneData as Record<string,unknown>:{};
    for(const model of [0,1,2]){
      let aircraft:any=null; sceneGroup.traverse(o=>{if(!aircraft&&/^Aircraft[A-C]$/.test(o.name)&&o.name.endsWith(['A','B','C'][model]))aircraft=o;});
      if(!aircraft){continue;}
      const letter=['A','B','C'][model]; const id=`SYN-CP-${letter}-AIR-001`;
      const caseId=`cp-case-${letter.toLowerCase()}`;
      let count=0;sceneGroup.traverse(o=>{if(o.name===aircraft.name)count++;});
      const text=buildHangarAircraftLabelText(root.case && (root.case as any).caseId===caseId ? {cases:[root.case]} : root,caseId);
      if(text&&count===1)entries.push({id,text,anchor:aircraft,kind:'aircraft'});
    }
    if(typeof selection==='string'){
      const selected=buildHangarSelectedObjectLabel(sceneData,selection);
      if(selected){let anchor:any=null;sceneGroup.traverse(o=>{if(!anchor&&typeof o.userData.objectId==='string'&&o.userData.objectId===selection)anchor=o;});if(anchor)entries.push({id:selection,text:selected,anchor,kind:'selected'});}
    }
    return entries;
  },[sceneGroup,sceneData,selection]);
  useEffect(()=>{const update=()=>{
    const hostNode=host.current;if(!hostNode)return;
    for(const el of Array.from(hostNode.querySelectorAll('[data-hangar-label]')) as HTMLElement[]){const id=el.getAttribute('data-hangar-label');const entry=data.find(v=>v.id===id);if(!entry||!entry.anchor){el.style.display='none';continue;}
      const bounds=new THREE.Box3().setFromObject(entry.anchor);const anchor=(bounds as any).getCenter(new THREE.Vector3());if(entry.kind==='aircraft')anchor.y=(bounds as any).max.y+1.5;
      const projected=projectHangarLabelAnchor(camera,anchor);if(!projected?.visible){el.style.display='none';continue;}const width=hostNode.clientWidth||1,height=hostNode.clientHeight||1;const x=(projected.x*.5+.5)*width,y=(-projected.y*.5+.5)*height;
      el.style.display='block';el.style.left='0';el.style.top='0';el.style.transform=`translate(${x}px,${y}px)`;
    }};update();},[data,camera,sceneData,selection]);
  const mappingError=data.length===0&&sceneGroup?HANGAR_LABEL_MAPPING_ERROR:null;
  return createElement('div',{ref:host,'data-hangar-label-host':'true',style:{position:'relative'}},children as any,
    mappingError?createElement('p',{role:'alert'},mappingError):null,
    data.map(entry=>createElement('div',{key:entry.id,'data-hangar-label':entry.id,'aria-hidden':'true',style:{position:'absolute',zIndex:1,pointerEvents:'none',whiteSpace:'pre-line',background:HANGAR_SCENE_THEME.labelBackground,color:HANGAR_SCENE_THEME.labelText,border:`1px solid ${HANGAR_SCENE_THEME.labelMuted}`,display:'none'}},entry.text)));
}
export default HangarCanvasAdapter;
