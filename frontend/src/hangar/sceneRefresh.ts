export type RefreshFallbackReason = 'refresh_failed' | null;
export type ServerSceneSnapshot = {
  revision: number | null; status: unknown; owner: unknown; nextAction: unknown;
  capabilityLabel: unknown; permissions: unknown; current: boolean; fallbackReason: RefreshFallbackReason;
  [key:string]:unknown;
};
export type RefreshResult = { snapshot: ServerSceneSnapshot|null; scene: unknown|null; valid:boolean; stale:boolean; error:string|null };
const isObj=(v:unknown):v is Record<string,unknown>=>typeof v==='object'&&v!==null&&!Array.isArray(v);
export function snapshotFromServer(envelope: unknown, injected?: unknown): ServerSceneSnapshot|null {
  if(!isObj(envelope)||envelope.contractVersion!==1||!isObj(envelope.case)) return null;
  const c=envelope.case; const rawRevision=c.caseRevision ?? c.revision;
  if(!Number.isInteger(rawRevision)||(rawRevision as number)<0) return null;
  if(!isObj(c.status)||typeof envelope.canResetBlockedVariant!== 'boolean'||!Array.isArray(envelope.allowedActions)) return null;
  const labels=['AI agent','Rules-based','Simulated','Design-only','Unknown'];
  const label=typeof c.capabilityLabel==='string'?c.capabilityLabel:'';
  if(!labels.includes(label)) return null;
  const resolvedAction=label==='Unknown'?null:(isObj(c.nextAction)?c.nextAction:null);
  return {revision:rawRevision as number,status:c.status,owner:c.owner??null,nextAction:resolvedAction,
    capabilityLabel:label,permissions:{canResetBlockedVariant:envelope.canResetBlockedVariant,allowedActions:[...envelope.allowedActions]},
    current:true,fallbackReason:null,caseId:c.caseId};
}
export function replaceWithServerSnapshot(previous: ServerSceneSnapshot|null, envelope: unknown, injected?: unknown): RefreshResult {
  const snapshot=snapshotFromServer(envelope,injected);
  if(!snapshot)return {snapshot:previous?{...previous,current:false,fallbackReason:'refresh_failed'}:null,scene:null,valid:false,stale:true,error:'Unsupported or malformed scene refresh'};
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
