import type { ReactNode } from 'react';
import { HANGAR_CAPABILITY_LABELS, type SceneDescriptorEntry } from './sceneProjection';

export const HANGAR_SEMANTIC_CONTROL_ERROR = 'Control-deficiency error';
export const HANGAR_CONTROL_DEFICIENCY_ERROR = HANGAR_SEMANTIC_CONTROL_ERROR;
export const HANGAR_FOCUS_STYLE = 'outline: 2px solid currentColor; outline-offset: 3px;';
export const HANGAR_SEMANTIC_CAPABILITIES = ['AI agent','Rules-based','Simulated','Design-only'] as const;
export const HANGAR_SEMANTIC_ACTIONS = ['selection','status','evidence','journey','review','reset'] as const;
export const HANGAR_SEMANTIC_ACTION_LABELS: Record<string,string> = {
  selection:'Select case',status:'View status',evidence:'View evidence',journey:'Follow journey',review:'Open review',reset:'Reset blocked variant'
};

export type HangarSemanticControl = {
  id: string; role: 'button'|'link'|'region'|'status'; name: string; kind: 'object'|'journey'|'action'|'status'|'region';
  activate?: (() => unknown)|null; handler?: (() => unknown)|null; descriptor?: unknown;
};
export type HangarSemanticControlsProps = {
  envelope?: unknown; scene?: unknown; descriptor?: SceneDescriptorEntry[]|null; manifest?: unknown;
  onActivate?: (control: HangarSemanticControl | string, source?: unknown) => unknown;
  onCommand?: (capability: string, action: string) => unknown;
  children?: ReactNode;
};

const objectOf = (v:unknown): v is Record<string,unknown> => typeof v==='object'&&v!==null&&!Array.isArray(v);
const text = (v:unknown): string|null => typeof v==='string'&&v.trim().length>0?v.trim():null;

export function getHangarSemanticPermission(source: unknown): { permitted: boolean; allowedActions: string[]; capability: string|null } {
  if (!objectOf(source)) return { permitted:false, allowedActions:[], capability:null };
  const c=objectOf(source.case)?source.case:{};
  const capability=text(c.capabilityLabel);
  const permission=c.permission ?? c.permissions;
  const permissionObject=objectOf(permission)?permission:null;
  const actionProjection=Array.isArray(c.allowedActions)?c.allowedActions:Array.isArray(source.allowedActions)?source.allowedActions:Array.isArray(permissionObject?.allowedActions)?permissionObject.allowedActions:[];
  const projectedActions=(actionProjection as unknown[]).map((a):string|null=>typeof a==='string'?a:objectOf(a)?text(a.actionId)??text(a.id)??text(a.action):null).filter((a):a is string=>typeof a==='string');
  let allowedActions:string[]=[];
  let permitted=false;
  if (permission===true) {
    permitted=true;
    allowedActions=projectedActions;
  } else if (permissionObject?.canResetBlockedVariant===true) {
    permitted=true;
    allowedActions=projectedActions.includes('reset')?['reset']:[];
  }
  const knownCapability=capability && capability!=='Unknown' && (HANGAR_CAPABILITY_LABELS as readonly string[]).includes(capability);
  return { permitted, allowedActions:permitted?allowedActions:[], capability:knownCapability?capability:null };
}

export function activateHangarSemanticControl(control: HangarSemanticControl|null|undefined): unknown {
  if (!control?.name || !control.role || control.role==='region'||control.role==='status') return `${HANGAR_SEMANTIC_CONTROL_ERROR}: control missing a role or accessible name`;
  if (!control.activate&&!control.handler) return `${HANGAR_SEMANTIC_CONTROL_ERROR}: control has no activation handler`;
  const handler=control.activate??control.handler!;
  try { return handler(); } catch { return `${HANGAR_SEMANTIC_CONTROL_ERROR}: activation failed`; }
}

export function handleHangarSemanticKeydown(control: HangarSemanticControl, event: {key?:unknown;preventDefault?:()=>void;target?:EventTarget|null}, activate?:()=>unknown): void {
  if (event.key==='Tab') return;
  if (event.key==='Enter'||event.key===' ') { event.preventDefault?.(); const result=activateHangarSemanticControl({...control,activate:activate??control.activate}); if (typeof result==='string'&&result.startsWith(HANGAR_SEMANTIC_CONTROL_ERROR)) return; }
}

export function shouldCaptureHangarSemanticKey(event: {key?:unknown;target?:EventTarget|null}): boolean { return event.key==='Enter'||event.key===' '; }

export function buildHangarSemanticControls(source: unknown, descriptor?: SceneDescriptorEntry[]|null, onActivate?: HangarSemanticControlsProps['onActivate'], onCommand?: HangarSemanticControlsProps['onCommand']): {controls:HangarSemanticControl[];error:string|null} {
  if (!objectOf(source)||!Array.isArray(source.objects)) return {controls:[],error:`${HANGAR_SEMANTIC_CONTROL_ERROR}: malformed scene controls`};
  const c=objectOf(source.case)?source.case:{};
  const objectDescriptors=new Map((descriptor??[]).map((d)=>[d.objectId,d]));
  const controls:HangarSemanticControl[]=[];
  for (const object of source.objects) {
    if (!objectOf(object)||typeof object.objectId!=='string'||typeof object.kind!=='string') return {controls:[],error:`${HANGAR_SEMANTIC_CONTROL_ERROR}: scene object missing role or accessible name`};
    const name=text(object.accessibleName)??text(object.label)??text(object.displayName);
    if(!name) return {controls:[],error:`${HANGAR_SEMANTIC_CONTROL_ERROR}: scene object missing an accessible name`};
    const descriptorEntry=objectDescriptors.get(object.objectId);
    const handler=descriptorEntry?.activate??descriptorEntry?.handler;
    const objectId=object.objectId as string;
    controls.push({id:objectId,role:'button',name,kind:'object',activate:typeof handler==='function'?handler:typeof onActivate==='function'?()=>{onActivate(objectId,object);return null;}:undefined,descriptor:descriptorEntry});
  }
  const history=Array.isArray(source.history)?source.history:[];
  for (const [index, step] of history.entries()) {
    if (!objectOf(step)) return {controls:[],error:`${HANGAR_SEMANTIC_CONTROL_ERROR}: journey step missing an accessible name`};
    const label=text(step.label)??text(step.text)??text(step.name);
    if(!label) return {controls:[],error:`${HANGAR_SEMANTIC_CONTROL_ERROR}: journey step missing an accessible name`};
    const journeyControl: HangarSemanticControl={id:`journey-${index}-${String(step.sequence??index+1)}`,role:'button',name:label,kind:'journey',activate:typeof onActivate==='function'?()=>{void onActivate(journeyControl,step);return null;}:undefined};
    controls.push(journeyControl);
  }
  const permission=getHangarSemanticPermission(source);
  for (const action of permission.allowedActions) {
    if (!(HANGAR_SEMANTIC_ACTIONS as readonly string[]).includes(action)||!permission.permitted||!permission.capability) continue;
    const label=HANGAR_SEMANTIC_ACTION_LABELS[action]; if(!label) continue;
    controls.push({id:`action-${action}`,role:'button',name:label,kind:'action',activate:()=>{
      if(typeof onActivate==='function') onActivate(`action-${action}`,action);
      if(typeof onCommand==='function') onCommand(action,action);
    }});
  }
  if (typeof c.status==='object'||text(c.status)) controls.push({id:'status',role:'status',name:'Hangar status',kind:'status'});
  const missingHandler = controls.some((control) => !control.name || (!control.activate && !control.handler && (control.kind === 'object' || control.kind === 'journey')));
  return missingHandler
    ? {controls:[],error:HANGAR_SEMANTIC_CONTROL_ERROR + ': pointer-only or unlabelled control'}
    : {controls,error:null};
}

export function HangarSemanticControls({envelope,scene,descriptor,manifest,onActivate,onCommand,children}:HangarSemanticControlsProps){
  const source=scene??envelope; const built=buildHangarSemanticControls(source,descriptor,onActivate,onCommand); const statusControls=built.controls.filter((c)=>c.kind==='status');
  const interactive=built.controls.filter((c)=>c.kind!=='status');
  // The focus outline lives in hangarMode.css: the production CSP blocks inline <style> elements.
  if (built.error) return <><section aria-label="Hangar semantic controls"><p role="alert">{built.error}</p></section></>;
  return <><section aria-label="Hangar semantic controls">
    {built.error&&<p role="alert">{built.error}</p>}
    {statusControls.map((status)=><p key={status.id} role="status">{status.name}: {objectOf(source)&&objectOf(source.case)&&objectOf(source.case.status)?Object.entries(source.case.status).map(([key,value])=>`${key}: ${value??'none'}`).join('; '):'text status'}</p>)}
    <div aria-label="Hangar scene objects">{interactive.filter((c)=>c.kind==='object').map((control)=><button className="hangar-semantic-control" type="button" key={control.id} onClick={()=>activateHangarSemanticControl(control)} aria-label={control.name}>{control.name}</button>)}</div>
    <div aria-label="Journey steps">{interactive.filter((c)=>c.kind==='journey').map((control)=><button className="hangar-semantic-control" type="button" key={control.id} onClick={()=>activateHangarSemanticControl(control)} aria-label={control.name}>{control.name}</button>)}</div>
    <div aria-label="Permitted actions">{interactive.filter((c)=>c.kind==='action').map((control)=><button className="hangar-semantic-control" type="button" key={control.id} onClick={()=>activateHangarSemanticControl(control)} aria-label={control.name}>{control.name}</button>)}</div>
    {children}
  </section></>;
}
export default HangarSemanticControls;
export { buildHangarSemanticControls as buildSemanticControls, HangarSemanticControls as SemanticHangarControls, handleHangarSemanticKeydown as handleSemanticKeydown, activateHangarSemanticControl as activateSemanticControl };
