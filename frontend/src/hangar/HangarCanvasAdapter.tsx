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
export function getHangarDemandCanvasProps(camera:THREE.PerspectiveCamera, onCreated?: (state:any)=>unknown){ return {frameloop:'demand'as const,dpr:[1,2] as [number,number],gl:{antialias:true},camera,...(onCreated ? {onCreated} : {})}; }

<<<<<<< HEAD
export type HangarCanvasAdapterProps={sceneGroup?:THREE.Group|null;camera?:THREE.PerspectiveCamera;invalidate?:()=>unknown;invalidationCapture?:ComponentType<any>;sceneData?:unknown;avatar?:unknown;selection?:unknown;route?:unknown;target?:{x:number;y:number;z:number};radius?:number;azimuthDegrees?:number;polarDegrees?:number;reducedMotion?:boolean;canvasComponent?:ComponentType<any>;onCreated?: (state:any)=>unknown;onContextLost?:()=>unknown};
=======
export function raycastHangarSelection(sceneGroup:THREE.Group|null|undefined,camera:THREE.PerspectiveCamera|null|undefined,clientX:number,clientY:number,width:number,height:number):{objectId:string;object:THREE.Object3D}|null{
  if(!sceneGroup||!camera||width<=0||height<=0||!Number.isFinite(clientX)||!Number.isFinite(clientY))return null;
  const ndc=new THREE.Vector2((clientX/width)*2-1,-(clientY/height)*2+1);
  camera.updateMatrixWorld(true); camera.updateProjectionMatrix(); sceneGroup.updateMatrixWorld(true);
  const raycaster=new THREE.Raycaster(); raycaster.setFromCamera(ndc,camera);
  const hits=raycaster.intersectObject(sceneGroup,true).sort((a,b)=>a.distance-b.distance);
  if(!hits.length)return null;
  if(/^(Floor|FloorMarking|Rack|RackUnit|Avatar|AvatarBody|AvatarHead|HardHat|GuideLine|SelectionRing|GroundShadow)/.test(hits[0].object.name))return null;
  for(const hit of hits){
    if(/^(Floor|FloorMarking|Rack|RackUnit|Avatar|AvatarBody|AvatarHead|HardHat|GuideLine|SelectionRing|GroundShadow)/.test(hit.object.name))continue;
    let node:THREE.Object3D|null=hit.object;
    while(node){
      const visibleId=typeof node.userData.objectId==='string'&&node.visible&&!(node instanceof THREE.Mesh&&(node.material as THREE.Material).visible===false)?node.userData.objectId:null;
      const pickId=typeof node.userData.pickObjectId==='string'?node.userData.pickObjectId:null;
      const id=visibleId??pickId;
      if(id){
        let selectable=node;
        if(pickId){let match:THREE.Object3D|null=null;sceneGroup.traverse(candidate=>{if(!match&&typeof candidate.userData.objectId==='string'&&candidate.userData.objectId===pickId)match=candidate;});if(match)selectable=match;}
        return {objectId:id,object:selectable};
      }
      node=node.parent;
    }
  }
  return null;
}

export type HangarCanvasAdapterProps={sceneGroup?:THREE.Group|null;camera?:THREE.PerspectiveCamera;invalidate?:()=>unknown;invalidationCapture?:ComponentType<any>;sceneData?:unknown;avatar?:unknown;selection?:unknown;route?:unknown;target?:{x:number;y:number;z:number};radius?:number;azimuthDegrees?:number;polarDegrees?:number;reducedMotion?:boolean;canvasComponent?:ComponentType<any>};
>>>>>>> origin/main
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

export function HangarCanvasAdapter({sceneGroup,camera,invalidate,invalidationCapture,sceneData,avatar,selection,route,target,radius,azimuthDegrees,polarDegrees,reducedMotion,canvasComponent,onCreated,onContextLost}:HangarCanvasAdapterProps){
  const [ownedCamera,setOwnedCamera]=useState(()=>camera ?? createHangarOwnedPerspectiveCamera());
  const pointerRef=useRef<{x:number;y:number;max:number}|null>(null);
  const pointerProps={
    onPointerDown:(event:any)=>{pointerRef.current={x:Number(event.clientX)||0,y:Number(event.clientY)||0,max:0};},
    onPointerMove:(event:any)=>{const start=pointerRef.current;if(start){const dx=Number(event.clientX)-start.x,dy=Number(event.clientY)-start.y;start.max=Math.max(start.max,Math.hypot(dx,dy));}},
    onPointerUp:(event:any)=>{const start=pointerRef.current;pointerRef.current=null;if(!start)return;const dx=Number(event.clientX)-start.x,dy=Number(event.clientY)-start.y;const distance=Math.max(start.max,Math.hypot(dx,dy));if(distance>6)return;const rect=(event.currentTarget?.getBoundingClientRect?.()??{width:1,height:1,left:0,top:0});const x=Number(event.clientX)-Number(rect.left||0),y=Number(event.clientY)-Number(rect.top||0);const hit=raycastHangarSelection(sceneGroup,ownedCamera,x,y,Number(rect.width)||1,Number(rect.height)||1);if(!hit)return;const handler=hit.object.userData.selectionHandler;if(typeof handler==='function')handler(hit.objectId,hit.object);},
    onPointerCancel:()=>{pointerRef.current=null;},
  };
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
  const [contextLost,setContextLost]=useState(false);
  if (contextLost) return null;
  if (!canvasComponent) return createElement('canvas',{'aria-label':'3D hangar scene','data-hangar-visible-fallback':'true'});
  const frame=createElement(HangarDemandFrame,{camera:ownedCamera,invalidate,target,radius,azimuthDegrees,polarDegrees,reducedMotion});
  const capture=invalidationCapture?createElement(invalidationCapture):null;
<<<<<<< HEAD
  const handleCreated=(state:any)=>{
    onCreated?.(state);
    const element=state?.gl?.domElement ?? state?.canvas;
    if(element && typeof element.addEventListener==='function'){
      element.addEventListener('webglcontextlost',(event:any)=>{event.preventDefault?.();setContextLost(true);onContextLost?.();},{once:true});
    }
  };
  const canvas=createElement(canvasComponent,{...getHangarDemandCanvasProps(ownedCamera,handleCreated)},sceneGroup?createElement('primitive',{object:sceneGroup}):createElement('group',{'data-hangar-visible-fallback':'true'}),capture,frame);
=======
  const canvas=createElement(canvasComponent,{...getHangarDemandCanvasProps(ownedCamera),...pointerProps},sceneGroup?createElement('primitive',{object:sceneGroup}):createElement('group',{'data-hangar-visible-fallback':'true'}),capture,frame);
>>>>>>> origin/main
  return createElement(HangarLabelOverlay,{sceneGroup,camera:ownedCamera,sceneData,selection},canvas);
}

export function HangarLabelOverlay({sceneGroup,camera,sceneData,selection,children}:{sceneGroup?:THREE.Group|null;camera:THREE.PerspectiveCamera;sceneData?:unknown;selection?:unknown;children?:ReactNode}){
  const host=useRef<HTMLDivElement|null>(null);
  const data=useMemo(()=>{
    const entries:Array<{id:string;text:string;anchor:THREE.Object3D|null;kind:'aircraft'|'selected'}>=[]; let mappingFailed=false;
    if(!sceneGroup)return {entries,mappingFailed:true};
    const root=typeof sceneData==='object'&&sceneData!==null?sceneData as Record<string,unknown>:{};
    for(const model of [0,1,2]){
      const letter=['A','B','C'][model], caseId=`cp-case-${letter.toLowerCase()}`; let count=0,aircraft:any=null;
      sceneGroup.traverse(o=>{if(/^Aircraft[A-C]$/.test(o.name)&&o.name===`Aircraft${letter}`){count++;if(!aircraft)aircraft=o;}});
      if(count!==1){mappingFailed=true;continue;}
      const cases=Array.isArray(root.cases)?root.cases:(Array.isArray(root.caseList)?root.caseList:null);
      const singleValue=root.case&&typeof root.case==='object'?root.case as Record<string,unknown>:null;
      const singleCase=singleValue&&singleValue.caseId===caseId?[singleValue]:null;
      const caseSource=cases||singleCase;
      const text=buildHangarAircraftLabelText({cases:caseSource},caseId);
      if(count===1&&!text){mappingFailed=true;continue;}
      if(count===1&&text){entries.push({id:`SYN-CP-${letter}-AIR-001`,text,anchor:aircraft,kind:'aircraft'});}
    }
    if(typeof selection==='string'){
      const aircraftSelection=/^SYN-CP-([ABC])-AIR-001$/.exec(selection);
      if(aircraftSelection){
        let count=0;
        sceneGroup.traverse(o=>{if(o.name===`Aircraft${aircraftSelection[1]}`)count++;});
        if(count!==1)mappingFailed=true;
      }else{
        const selected=buildHangarSelectedObjectLabel(sceneData,selection); let anchor:any=null,count=0;
        sceneGroup.traverse(o=>{if(typeof o.userData.objectId==='string'&&o.userData.objectId===selection){count++;if(!anchor)anchor=o;}});
        if(!selected||count!==1||!anchor){mappingFailed=true;}else entries.push({id:selection,text:selected,anchor,kind:'selected'});
      }
    }
    return {entries,mappingFailed};
  },[sceneGroup,sceneData,selection]);
  useEffect(()=>{let active=true;const update=()=>{if(!active)return;const hostNode=host.current;if(!hostNode)return;
    for(const el of Array.from(hostNode.querySelectorAll('[data-hangar-label]')) as HTMLElement[]){const id=el.getAttribute('data-hangar-label');const entry=data.entries.find(v=>v.id===id);if(!entry||!entry.anchor){el.style.display='none';continue;}
      const bounds:any=new THREE.Box3().setFromObject(entry.anchor);const center=bounds.getCenter(new THREE.Vector3());const anchor=entry.kind==='aircraft'?new THREE.Vector3(center.x,bounds.max.y+1.5,center.z):center;
      const projected=projectHangarLabelAnchor(camera,anchor);if(!projected?.visible){el.style.display='none';continue;}const width=hostNode.clientWidth||1,height=hostNode.clientHeight||1;const x=(projected.x*.5+.5)*width,y=(-projected.y*.5+.5)*height;
      el.style.display='block';el.style.left='0';el.style.top='0';el.style.transform=`translate(${x}px,${y}px)`;
    }};update();if(typeof setInterval==='function'){const interval=setInterval(update,16);return ()=>{active=false;clearInterval(interval);}}return ()=>{active=false};},[data,camera]);
  const mappingError=data.mappingFailed||data.entries.length===0&&sceneGroup?HANGAR_LABEL_MAPPING_ERROR:null;
  return createElement('div',{ref:host,'data-hangar-label-host':'true',style:{position:'relative'}},children as any,
    mappingError?createElement('p',{role:'alert'},mappingError):null,
    data.entries.map(entry=>createElement('div',{key:entry.id,'data-hangar-label':entry.id,'aria-hidden':'true',style:{position:'absolute',zIndex:1,pointerEvents:'none',whiteSpace:'pre-line',background:HANGAR_SCENE_THEME.labelBackground,color:HANGAR_SCENE_THEME.labelText,border:`1px solid ${HANGAR_SCENE_THEME.labelMuted}`,display:'none'}},entry.text)));
}
export default HangarCanvasAdapter;
