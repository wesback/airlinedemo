export const SUPPORTED_SOURCE_SUMMARY_CONTRACT_VERSION = 1;
export const SOURCE_UNAVAILABLE_TEXT = 'source summary unavailable';
export const HANGAR_SOURCE_IDS = ['technical','maintenance','finance','records','contracts'] as const;
export type HangarSourceId = typeof HANGAR_SOURCE_IDS[number];
export type SourceMode = 'available'|'unavailable'|'stale'|'conflicting';
export type SourceSummaryView = {
  sourceId: HangarSourceId; mode: SourceMode; coverage: boolean;
  modeText: string; coverageText: string; text: string;
};
export type HangarSourceSummaryEntry = {
  caseId: string; name: string; category: string;
  unavailable: boolean; sources: SourceSummaryView[]|null;
};
export type SourceSummaryReader = (caseId: string) => Promise<unknown>|unknown;
export type CaseListEntry = Record<string, unknown> & { caseId?: unknown; id?: unknown; name?: unknown; category?: unknown; aircraftCategory?: unknown; aircraftName?: unknown };

const isObject=(v:unknown):v is Record<string,unknown>=>typeof v==='object'&&v!==null&&!Array.isArray(v);
const isName=(v:unknown):v is string=>typeof v==='string'&&v.trim().length>0;
const sourcesSet=new Set<string>(HANGAR_SOURCE_IDS);

function normalizeScope(scope: unknown): Set<string> {
  if (Array.isArray(scope)) return new Set(scope.filter(isName));
  if (isObject(scope) && Array.isArray(scope.caseScopes)) return new Set(scope.caseScopes.filter(isName));
  return new Set<string>();
}

function normalizeCase(value: unknown): {caseId:string;name:string;category:string}|null {
  if (!isObject(value)) return null;
  const caseId=isName(value.caseId)?value.caseId:isName(value.id)?value.id:null;
  const name=isName(value.name)?value.name:isName(value.aircraftName)?value.aircraftName:null;
  const category=isName(value.category)?value.category:isName(value.aircraftCategory)?value.aircraftCategory:null;
  if (!caseId||!name||!category||!['cp-case-a','cp-case-b','cp-case-c'].includes(caseId)) return null;
  return {caseId,name,category};
}

export function validateSourceSummaryEnvelope(envelope: unknown, requestedCaseId?: string): {valid:boolean;sourceSummary:null|SourceSummaryView[];error:string|null} {
  if (!isObject(envelope)) return {valid:false,sourceSummary:null,error:'Malformed source summary contract'};
  if (envelope.contractVersion!==SUPPORTED_SOURCE_SUMMARY_CONTRACT_VERSION) return {valid:false,sourceSummary:null,error:'Unsupported source-summary contract version'};
  if (!isName(envelope.caseId)||(requestedCaseId!==undefined&&envelope.caseId!==requestedCaseId)) return {valid:false,sourceSummary:null,error:'Source summary case identifier mismatch'};
  const allowedTop=new Set(['contractVersion','fixtureVersion','caseId','caseRevision','sources']);
  if (Object.keys(envelope).some(key=>!allowedTop.has(key))) return {valid:false,sourceSummary:null,error:'Unexpected source summary payload field'};
  if (!Array.isArray(envelope.sources)||envelope.sources.length!==5) return {valid:false,sourceSummary:null,error:'Source summary must contain exactly five sources'};
  const seen=new Set<string>(); const bySource=new Map<HangarSourceId,SourceSummaryView>();
  const allowedSource=new Set(['sourceId','mode','coverage','capabilityLabel','recordCount','pinnedInBasis','recordsInUse']);
  for (const raw of envelope.sources) {
    if (!isObject(raw)||!isName(raw.sourceId)||!sourcesSet.has(raw.sourceId)||seen.has(raw.sourceId)) return {valid:false,sourceSummary:null,error:'Invalid source summary source set'};
    seen.add(raw.sourceId);
    if (Object.keys(raw).some(key=>!allowedSource.has(key))) return {valid:false,sourceSummary:null,error:'Unexpected source payload field'};
    if (!['available','unavailable','stale','conflicting'].includes(String(raw.mode))||typeof raw.coverage!=='boolean') return {valid:false,sourceSummary:null,error:'Malformed source summary fields'};
    if (raw.mode==='unavailable'&&raw.coverage) return {valid:false,sourceSummary:null,error:'Unavailable source has inferred coverage'};
    const sourceId=raw.sourceId as HangarSourceId; const mode=raw.mode as SourceMode; const coverage=raw.coverage;
    const modeText=`mode: ${mode}`; const coverageText=`coverage: ${coverage}`;
    bySource.set(sourceId,{sourceId,mode,coverage,modeText,coverageText,text:`${sourceId}: ${modeText}; ${coverageText}`});
  }
  if (HANGAR_SOURCE_IDS.some(id=>!seen.has(id))) return {valid:false,sourceSummary:null,error:'Missing source summary source'};
  const result=HANGAR_SOURCE_IDS.map(id=>bySource.get(id)!);
  return {valid:true,sourceSummary:result,error:null};
}

async function fetchDefault(caseId:string):Promise<unknown> {
  const response=await fetch(`/api/v1/cases/${encodeURIComponent(caseId)}/sources`);
  if (!response.ok) throw new Error(`Source summary request failed: ${response.status}`);
  return response.json();
}

function unavailable(caseId:string,name:string,category:string):HangarSourceSummaryEntry {
  return {caseId,name,category,unavailable:true,sources:null};
}

export async function projectHangarSourceSummary(caseList:unknown, scope:unknown, reader?:SourceSummaryReader):Promise<HangarSourceSummaryEntry[]> {
  const scoped=normalizeScope(scope); const entries:HangarSourceSummaryEntry[]=[]; const read=reader??fetchDefault;
  const seen=new Set<string>();
  const list=Array.isArray(caseList)?caseList:(isObject(caseList)&&Array.isArray(caseList.cases)?caseList.cases:[]);
  for (const raw of list) {
    const c=normalizeCase(raw);
    if (!c||seen.has(c.caseId)||!scoped.has(c.caseId)) continue;
    seen.add(c.caseId);
    try {
      const envelope=await read(c.caseId);
      const validated=validateSourceSummaryEnvelope(envelope,c.caseId);
      entries.push(validated.valid&&validated.sourceSummary
        ? {caseId:c.caseId,name:c.name,category:c.category,unavailable:false,sources:validated.sourceSummary}
        : unavailable(c.caseId,c.name,c.category));
    } catch {
      entries.push(unavailable(c.caseId,c.name,c.category));
    }
  }
  return entries;
}

export async function refreshHangarSourceSummary(caseList:unknown, scope:unknown, reader:SourceSummaryReader, previous:HangarSourceSummaryEntry[]=[]):Promise<HangarSourceSummaryEntry[]> {
  const fresh=await projectHangarSourceSummary(caseList,scope,reader);
  const byId=new Map(previous.map(entry=>[entry.caseId,entry]));
  for (const entry of fresh) byId.set(entry.caseId,entry);
  return Array.isArray(caseList)?caseList.map(raw=>normalizeCase(raw)).filter((c):c is {caseId:string;name:string;category:string}=>!!c&&byId.has(c.caseId)).map(c=>byId.get(c.caseId)!):[];
}

export const buildHangarSourceSummary=projectHangarSourceSummary;
export const buildHangarSourceSummaryView=projectHangarSourceSummary;
export const buildDataRoomSourceSummary=projectHangarSourceSummary;
export const projectHangarSourceSummaryList=projectHangarSourceSummary;
export const buildSourceSummaryView=projectHangarSourceSummary;
export const projectSourceSummary=projectHangarSourceSummary;
export const validateSourceSummary=validateSourceSummaryEnvelope;
export const SOURCE_SUMMARY_UNAVAILABLE=SOURCE_UNAVAILABLE_TEXT;
