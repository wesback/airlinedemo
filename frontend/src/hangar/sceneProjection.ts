export const SUPPORTED_SCENE_CONTRACT_VERSION = 1;
export const HANGAR_CAPABILITY_LABELS = ['AI agent','Rules-based','Simulated','Design-only','Unknown'] as const;
export const HANGAR_CASE_IDS = ['cp-case-a','cp-case-b','cp-case-c'] as const;
export const HANGAR_ACTUAL_OBJECT_IDS = [
  'SYN-CP-A-AIR-001','SYN-CP-A-ENG-001','SYN-CP-A-CMP-001','SYN-CP-A-CMP-002',
  'SYN-CP-B-AIR-001','SYN-CP-B-ENG-001','SYN-CP-B-CMP-001','SYN-CP-B-CMP-002',
  'SYN-CP-C-AIR-001','SYN-CP-C-ENG-001','SYN-CP-C-CMP-001','SYN-CP-C-CMP-002',
] as const;

export type SceneStatus = Partial<Record<'assessment'|'review'|'request'|'finance'|'legal'|'acceptance', string|null>>;
export type SceneEnvelope = { contractVersion?: unknown; case?: { caseId?: unknown; caseRevision?: unknown; revision?: unknown; status?: unknown; owner?: unknown; nextAction?: unknown; capabilityLabel?: unknown }; objects?: unknown[]; history?: unknown[]; evidence?: unknown[]; documentRefs?: unknown[]; canResetBlockedVariant?: unknown; allowedActions?: unknown[]; [key:string]:unknown };
export type ProjectionResult = { valid: boolean; scene: any|null; error: string|null; reason?: string };
const isObject=(v:unknown):v is Record<string,unknown>=>typeof v==='object'&&v!==null&&!Array.isArray(v);
const nonempty=(v:unknown):v is string=>typeof v==='string'&&v.trim().length>0;
const positive=(v:unknown):v is number=>typeof v==='number'&&Number.isInteger(v)&&v>0;
export function validateHangarSceneEnvelope(envelope: unknown): ProjectionResult {
  if (!isObject(envelope)) return {valid:false,scene:null,error:'Malformed hangar scene contract'};
  if (envelope.contractVersion!==SUPPORTED_SCENE_CONTRACT_VERSION) return {valid:false,scene:null,error:'Unsupported hangar contract version '+String(envelope.contractVersion)};
  const c=envelope.case;
  const revision=c && isObject(c)?(c.caseRevision ?? c.revision):undefined;
  if (!isObject(c)||typeof revision!== 'number'||!Number.isInteger(revision)||revision<0) return {valid:false,scene:null,error:'Missing or invalid scene revision'};
  if (!nonempty(c.caseId)) return {valid:false,scene:null,error:'Invalid scene case identifier'};
  if (typeof c.status!=='object'||c.status===null||Array.isArray(c.status)||typeof envelope.canResetBlockedVariant!== 'boolean'||!Array.isArray(envelope.allowedActions)||!Array.isArray(envelope.history)||!Array.isArray(envelope.evidence)||!Array.isArray(envelope.documentRefs)) return {valid:false,scene:null,error:'Malformed hangar scene envelope'};
  if (typeof c.capabilityLabel!=='string'||!HANGAR_CAPABILITY_LABELS.includes(c.capabilityLabel as typeof HANGAR_CAPABILITY_LABELS[number])) return {valid:false,scene:null,error:'Unknown capability label'};
  const permission = c.permission ?? c.permissions;
  if (permission === undefined || (typeof permission!== 'boolean' && (!isObject(permission) || typeof permission.canResetBlockedVariant!== 'boolean'))) return {valid:false,scene:null,error:'Missing scene permission snapshot'};
  if (!Array.isArray(envelope.objects)||envelope.objects.length!==4) return {valid:false,scene:null,error:'Invalid scene object set'};
  const allowedKinds=new Set(['aircraft','engine','component']); const ids=new Set<string>();
  for (const o of envelope.objects) {
    if (!isObject(o)||!nonempty(o.objectId)||!nonempty(o.caseId)||o.caseId!==c.caseId||!allowedKinds.has(String(o.kind))) return {valid:false,scene:null,error:'Scene object identifier mapping is invalid'};
    if (!HANGAR_ACTUAL_OBJECT_IDS.includes(o.objectId as typeof HANGAR_ACTUAL_OBJECT_IDS[number])) return {valid:false,scene:null,error:'Unknown scene object identifier'};
    const prefix=String(c.caseId).slice(-1); const packPrefix=prefix==='a'?'A':prefix==='b'?'B':'C';
    if (!o.objectId.includes(`-CP-${packPrefix}-`) && !o.objectId.includes(`-CP-${packPrefix}-`)) return {valid:false,scene:null,error:'Cross-case scene object identifier'};
    if (ids.has(o.objectId)) return {valid:false,scene:null,error:'Duplicate scene object identifier'};
    ids.add(o.objectId);
  }
  for (const event of envelope.history) if (!isObject(event)||!Array.isArray(event.witnessIds)||(event.sequence!==undefined&&!positive(event.sequence))) return {valid:false,scene:null,error:'Missing scene history'};
  const witnesses=envelope.evidence as unknown[];
  const witnessIds=new Set(witnesses.filter(isObject).map(w=>w.witnessId));
  for (const event of envelope.history) for (const id of (event as Record<string,unknown>).witnessIds as unknown[]) if(typeof id!=='string'||!witnessIds.has(id)) return {valid:false,scene:null,error:'Bad witness ID'};
  for (const witness of witnesses) if(isObject(witness)&&(witness.caseId!==undefined&&witness.caseId!==c.caseId)) return {valid:false,scene:null,error:'Cross-case witness ID'};
  return {valid:true,scene:envelope,error:null};
}
export type SceneDescriptorEntry={objectId:string;caseId:string;kind:string;handler?:()=>unknown;activate?:()=>unknown};
export function buildHangarSceneDescriptor(envelope: unknown, manifest: unknown, handler?:((object:any)=>unknown)|null): ProjectionResult & {descriptor:any[]|null} {
  const result=validateHangarSceneEnvelope(envelope);
  if(!result.valid||!Array.isArray((envelope as any).objects)||typeof handler!=='function') return {...result,scene:null,descriptor:null,error:result.error?.startsWith('Visible mapping error')?result.error:'Visible mapping error: '+(result.error??'scene descriptor handler is missing')};
  const objects=(envelope as any).objects; const descriptor:SceneDescriptorEntry[]=[]; const ids=new Set<string>();
  if(!isObject(manifest)||!Array.isArray((manifest as any).objectIds)) return {...result,scene:null,descriptor:null,error:'Visible mapping error: scene manifest is missing'};
  const expected=(manifest as any).objectIds as unknown[];
  if(expected.length!==12||new Set(expected).size!==12||expected.some(id=>!HANGAR_ACTUAL_OBJECT_IDS.includes(id as typeof HANGAR_ACTUAL_OBJECT_IDS[number]))) return {...result,scene:null,descriptor:null,error:'Visible mapping error: scene must map exactly 12 CP identifiers'};
  for(const o of objects){ if(!nonempty(o.objectId)||ids.has(o.objectId)||!expected.includes(o.objectId)) return {...result,scene:null,descriptor:null,error:'Visible mapping error: duplicate, cross-case, or unknown identifier'}; ids.add(o.objectId); descriptor.push({objectId:o.objectId,caseId:o.caseId,kind:o.kind,handler:()=>handler(o),activate:()=>handler(o)}); }
  return {...result,descriptor};
}
export function projectHangarScene(envelope:unknown,manifest?:unknown,handler?:((object:any)=>unknown)|null){return manifest===undefined?validateHangarSceneEnvelope(envelope):buildHangarSceneDescriptor(envelope,manifest,handler);}
export const validateSceneEnvelope=validateHangarSceneEnvelope;
export const buildSceneDescriptor=buildHangarSceneDescriptor;
