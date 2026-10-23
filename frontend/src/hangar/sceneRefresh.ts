import { validateHangarSceneEnvelope } from './sceneProjection';

export type RefreshFallbackReason = 'refresh_failed' | null;
export type ServerSceneSnapshot = {
  revision: number | null; status: unknown; owner: unknown; nextAction: unknown;
  capabilityLabel: unknown; permissions: unknown; current: boolean; fallbackReason: RefreshFallbackReason;
  [key:string]:unknown;
};
export type RefreshResult = { snapshot: ServerSceneSnapshot|null; scene: unknown|null; valid:boolean; stale:boolean; error:string|null };
const isObj=(v:unknown):v is Record<string,unknown>=>typeof v==='object'&&v!==null&&!Array.isArray(v);
function equalContent(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) return true;
  if (Array.isArray(left) && Array.isArray(right)) return left.length===right.length && left.every((value,index)=>equalContent(value,right[index]));
  if (isObj(left) && isObj(right)) {
    const leftKeys=Object.keys(left).sort(); const rightKeys=Object.keys(right).sort();
    return leftKeys.length===rightKeys.length && leftKeys.every((key,index)=>key===rightKeys[index] && equalContent(left[key],right[key]));
  }
  return false;
}
export function snapshotFromServer(envelope: unknown, injected?: unknown): ServerSceneSnapshot|null {
  const projected=validateHangarSceneEnvelope(envelope);
  if(!projected.valid||!isObj(envelope)||!isObj(envelope.case)) return null;
  const c=envelope.case; const rawRevision=c.caseRevision ?? c.revision;
  if(!Number.isInteger(rawRevision)||(rawRevision as number)<0) return null;
  if(!isObj(c.status)) return null;
  const labels=['AI agent','Rules-based','Simulated','Design-only','Unknown'];
  const label=typeof c.capabilityLabel==='string'?c.capabilityLabel:'';
  if(!labels.includes(label)) return null;
  const resolvedAction=label==='Unknown'?null:(isObj(c.nextAction)?c.nextAction:null);
  const permission=c.permission ?? c.permissions ?? {canResetBlockedVariant:envelope.canResetBlockedVariant,allowedActions:[...(envelope.allowedActions as unknown[])]};
  return {revision:rawRevision as number,status:c.status,owner:c.owner??null,nextAction:resolvedAction,
    capabilityLabel:label,permissions:permission,
    current:true,fallbackReason:null,caseId:c.caseId,completeContent:envelope};
}
export function replaceWithServerSnapshot(previous: ServerSceneSnapshot|null, envelope: unknown, injected?: unknown): RefreshResult {
  const snapshot=snapshotFromServer(envelope,injected);
  if(!snapshot)return {snapshot:previous?{...previous,current:false,fallbackReason:'refresh_failed'}:null,scene:null,valid:false,stale:true,error:'Unsupported or malformed scene refresh'};
  if(previous && previous.caseId===snapshot.caseId && previous.revision!==null && snapshot.revision!==null) {
    if(snapshot.revision < previous.revision) {
      const staleSnapshot: ServerSceneSnapshot={...previous,current:false,fallbackReason:'refresh_failed'};
      return {snapshot:staleSnapshot,scene:null,valid:false,stale:true,error:'Stale scene revision'};
    }
    if(snapshot.revision === previous.revision) {
      if(equalContent(previous.completeContent, envelope)) return {snapshot:previous,scene:null,valid:true,stale:previous.current===false,error:null};
      const staleSnapshot: ServerSceneSnapshot={...previous,current:false,fallbackReason:'refresh_failed'};
      return {snapshot:staleSnapshot,scene:null,valid:false,stale:true,error:'Changed content at retained scene revision'};
    }
  }
  return {snapshot,scene:envelope,valid:true,stale:false,error:null};
}
export function markRefreshStale(snapshot: ServerSceneSnapshot|null): RefreshResult {
  return {snapshot:snapshot?{...snapshot,current:false,fallbackReason:'refresh_failed'}:snapshot,scene:null,valid:false,stale:true,error:'Scene refresh failed'};
}
export function canAdmitHangarCommands(snapshot: ServerSceneSnapshot|null): boolean {
  return !!snapshot?.current;
}
export function refreshHangarScene(reader: () => Promise<unknown>|unknown, previous: ServerSceneSnapshot|null = null): Promise<RefreshResult> {
  return Promise.resolve().then(()=>reader()).then(value=>replaceWithServerSnapshot(previous,value)).catch(()=>markRefreshStale(previous));
}
export const applyServerRefresh=replaceWithServerSnapshot;
export const staleHangarSnapshot=markRefreshStale;
export const admitHangarCommand=canAdmitHangarCommands;
