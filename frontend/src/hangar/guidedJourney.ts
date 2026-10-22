import { createElement, useState } from 'react';

const isObject=(v:unknown):v is Record<string,unknown>=>typeof v==='object'&&v!==null&&!Array.isArray(v);
const text=(v:unknown):string|null=>typeof v==='string'&&v.trim().length>0?v.trim():null;
const positive=(v:unknown):v is number=>typeof v==='number'&&Number.isInteger(v)&&v>0;

export const CASE_A_JOURNEY_LABELS=['Finding','Review','Request','Response','New basis','Reassessment'] as const;
export type CaseAJourneyLabel=typeof CASE_A_JOURNEY_LABELS[number];
export type CaseBJourneyLabel='Finding'|'Internal review';
export type CaseCJourneyLabel='Finding'|'Review'|'Response'|'New basis'|'Reassessment'|'blocked'|'retryable';
export type CaseJourneyVariant='baseline'|'blocked';
export type CaseAJourneyWitness={witnessId:string;documentId:string;documentVersion:number;page:number;basisId:string;basisVersion:number;displayLabel:string|null};
export type CaseAJourneyStep={id:string;sequence:number;runId:string|null;basisId:string|null;basisVersion:number|null;actorLabel:string|null;label:string;summary:string|null;sourceEventId:string|null;sourceEventLink:string|null;witnessId:string|null;witnessIds:string[];evidence:CaseAJourneyWitness[];completed:boolean;available:boolean;future:boolean;invalidReason:string|null};
export type CaseAJourney={caseId:string|null;runId:string|null;basisId:string|null;basisVersion:number|null;steps:CaseAJourneyStep[];olderHistoryAvailable:boolean;olderHistoryAccess:string|null;variant?:CaseJourneyVariant};

const typeToLabel=(type:string|null):CaseAJourneyLabel|null=>{
  if(!type)return null;
  const normalized=type.toLowerCase().replace(/[_\s]+/g,'-');
  if(normalized.includes('finding')||normalized==='finding-recorded')return 'Finding';
  if(normalized.includes('review')||normalized==='review')return 'Review';
  if(normalized.includes('request'))return 'Request';
  if(normalized.includes('response')||normalized.includes('partner'))return 'Response';
  if(normalized.includes('new-basis')||normalized.includes('basis-created')||normalized.includes('basis'))return 'New basis';
  if(normalized.includes('reassess')||normalized.includes('assessment'))return 'Reassessment';
  return null;
};

export function buildCaseAJourney(envelope:unknown, caseId='cp-case-a'):CaseAJourney {
  const source=isObject(envelope)?envelope:{}; const caseObject=isObject(source.case)?source.case:{};
  const expected=text(caseObject.caseId)??text(source.caseId)??caseId; const history=Array.isArray(source.history)?source.history:[];
  const evidence=Array.isArray(source.evidence)?source.evidence:Array.isArray(source.witnesses)?source.witnesses:[];
  const events=history.filter(isObject).sort((a,b)=>{
    const av=positive(a.sequence)?a.sequence:Number.MAX_SAFE_INTEGER; const bv=positive(b.sequence)?b.sequence:Number.MAX_SAFE_INTEGER;
    return av-bv;
  });
  const byLabel=new Map<CaseAJourneyLabel,Record<string,unknown>>();
  for(const event of events){const label=typeToLabel(text(event.eventType)??text(event.label)??text(event.summary));if(label&&!byLabel.has(label))byLabel.set(label,event);}
  const steps=CASE_A_JOURNEY_LABELS.map((label,index)=>{
    const event=byLabel.get(label); const sequence=event?event.sequence:undefined;
    if(!event)return {id:`future-${index+1}`,sequence:index+1,runId:null,basisId:null,basisVersion:null,actorLabel:null,label,summary:null,sourceEventId:null,sourceEventLink:null,witnessId:null,witnessIds:[],evidence:[],completed:false,available:false,future:true,invalidReason:'Future unrecorded step'} satisfies CaseAJourneyStep;
    const eventCase=text(event.caseId)??expected; const runId=text(event.runId); const basisId=text(event.basisId); const basisVersion=positive(event.basisVersion)?event.basisVersion:null;
    const witnessIds=Array.isArray(event.witnessIds)?event.witnessIds.filter((id):id is string=>typeof id==='string'):[];
    const eventWitnesses=witnessIds.map((id)=>evidence.find((w)=>isObject(w)&&w.witnessId===id&&(w.caseId===undefined||w.caseId===expected)&&w.documentId!==undefined&&positive(w.documentVersion)&&positive(w.page)&&text(w.basisId)&&positive(w.basisVersion))).filter((w):w is Record<string,unknown>=>isObject(w));
    const exactWitnesses:CaseAJourneyWitness[]=eventWitnesses.map((w)=>({witnessId:String(w.witnessId),documentId:String(w.documentId??''),documentVersion:positive(w.documentVersion)?w.documentVersion:0,page:positive(w.page)?w.page:0,basisId:String(w.basisId??''),basisVersion:positive(w.basisVersion)?w.basisVersion:0,displayLabel:text(w.displayLabel)}));
    const invalidReason=eventCase!==expected?'Cross-case journey event':!runId?'Missing run':!basisId||basisVersion===null?'Missing basis':!positive(sequence)?'Missing sequence':witnessIds.length===0?'Missing witness ID':witnessIds.some((id)=>!exactWitnesses.some((w)=>w.witnessId===id))?'Missing or invalid witness ID':null;
    const eventId=text(event.eventId); const sourceEventLink=invalidReason==='Cross-case journey event'?null:(text(event.sourceEventLink)??(eventId?`#${eventId}`:null));
    const rawSummary=invalidReason==='Cross-case journey event'?null:(text(event.summary)??text(event.label)); const safeSummary=rawSummary&&/[<>]/.test(rawSummary)?null:rawSummary;
    const unique={id:eventId??`event-${sequence}`,sequence:sequence as number,runId:invalidReason==='Cross-case journey event'?null:runId,basisId:invalidReason==='Cross-case journey event'?null:basisId,basisVersion:invalidReason==='Cross-case journey event'?null:basisVersion,actorLabel:invalidReason==='Cross-case journey event'?null:(text(event.actorRole)??text(event.actorLabel)),label,summary:safeSummary,sourceEventId:invalidReason==='Cross-case journey event'?null:eventId,sourceEventLink,witnessId:invalidReason==='Cross-case journey event'?null:(witnessIds[0]??null),witnessIds:invalidReason==='Cross-case journey event'?[]:witnessIds,evidence:invalidReason==='Cross-case journey event'?[]:exactWitnesses,completed:invalidReason===null,available:invalidReason===null,future:false,invalidReason}; return unique;
  });
  return {caseId:expected,runId:text(source.currentRunId)??text(caseObject.currentRunId)??events.map((e)=>text(e.runId)).find(Boolean)??null,basisId:events.map((e)=>text(e.basisId)).find(Boolean)??null,basisVersion:events.map((e)=>positive(e.basisVersion)?e.basisVersion:null).find((v)=>v!==null)??null,steps,olderHistoryAvailable:source.olderHistoryAvailable===true,olderHistoryAccess:null};
}
export const buildHangarGuidedJourney=buildCaseAJourney; export const constructCaseAJourney=buildCaseAJourney;

const variantLabel=(value:unknown):CaseJourneyVariant|null=>typeof value==='string'&&(value.toLowerCase()==='blocked'||value.toLowerCase()==='retryable')?'blocked':value==='baseline'?'baseline':null;

export function buildCaseBJourney(envelope:unknown):CaseAJourney {
  const source=isObject(envelope)?envelope:{}; const base=buildCaseAJourney(source,'cp-case-b');
  const allowed=Array.isArray(source.history)?source.history.filter(isObject):[];
  const steps=base.steps.filter(step=>step.completed&&(step.label==='Finding'||step.label==='Review')).map(step=>{
    const event=allowed.find(e=>text(e.eventId)==step.sourceEventId);
    const raw=event?(text(event.eventType)??text(event.label)??text(event.summary)??''):'';
    if(/request/i.test(raw)||!/finding|review/i.test(raw))return null;
    return step.label==='Review'?{...step,label:'Internal review'}:step;
  }).filter((step):step is CaseAJourneyStep=>step!==null);
  return {...base,caseId:'cp-case-b',variant:undefined,steps};
}

export function buildCaseCJourney(envelope:unknown):CaseAJourney {
  const source=isObject(envelope)?envelope:{}; const base=buildCaseAJourney(source,'cp-case-c');
  const caseObject=isObject(source.case)?source.case:{};
  const declared=variantLabel(source.variant)??variantLabel(caseObject.variant)??variantLabel(source.blockedVariant)??(source.canResetBlockedVariant===true?'blocked':null);
  const history=Array.isArray(source.history)?source.history.filter(isObject):[];
  const eventBlocked=history.some(event=>{
    const eventType=text(event.eventType)?.toLowerCase(); const action=text(event.action)?.toLowerCase(); const variant=text(event.variant)?.toLowerCase(); const status=text(event.status)?.toLowerCase(); const reasonCode=text(event.reasonCode)?.toLowerCase();
    return eventType==='presenter_reset_blocked_variant'||action==='presenter_reset_blocked_variant'||variant==='blocked'||variant==='retryable'||status==='blocked'||status==='retryable'||eventType==='blocked'||eventType==='retryable'||reasonCode==='presenter_reset_blocked_variant'||reasonCode==='retryable';
  });
  const variant:CaseJourneyVariant=(declared??(eventBlocked?'blocked':'baseline'));
  const allowed=base.steps.filter(step=>['Finding','Review','Response','New basis','Reassessment'].includes(step.label)&&step.completed)
    .filter(step=>{const event=history.find(e=>text(e.eventId)==step.sourceEventId);const raw=event?(text(event.eventType)??text(event.label)??text(event.summary)??''):step.summary??'';return !/(request|accept|accepted|acceptance)/i.test(raw);});
  if(variant==='blocked'){
    const tail=allowed.at(-1)?.sequence??0;
    return {...base,caseId:'cp-case-c',variant,steps:[...allowed,
      {id:'blocked',sequence:tail+1,runId:base.runId,basisId:base.basisId,basisVersion:base.basisVersion,actorLabel:null,label:'blocked',summary:'blocked',sourceEventId:null,sourceEventLink:null,witnessId:null,witnessIds:[],evidence:[],completed:true,available:true,future:false,invalidReason:null},
      {id:'retryable',sequence:tail+2,runId:base.runId,basisId:base.basisId,basisVersion:base.basisVersion,actorLabel:null,label:'retryable',summary:'retryable',sourceEventId:null,sourceEventLink:null,witnessId:null,witnessIds:[],evidence:[],completed:true,available:true,future:false,invalidReason:null}]};
  }
  return {...base,caseId:'cp-case-c',variant,steps:allowed.filter(step=>step.label!=='Request')};
}
export const buildCaseBGuidedJourney=buildCaseBJourney; export const buildCaseCGuidedJourney=buildCaseCJourney;

export type OlderHistoryAdapter=(request:{caseId:string;runId:string;basisId:string})=>unknown|Promise<unknown>;
export async function openCaseAOlderHistory(journey:CaseAJourney, adapter:OlderHistoryAdapter|{read?:OlderHistoryAdapter}|null|undefined, selector?:{runId?:unknown;basisId?:unknown}):Promise<{ok:boolean;history:CaseAJourney|null;error:string|null}> {
  if(!journey.olderHistoryAvailable)return {ok:false,history:null,error:'Older history access is unavailable'};
  const runId=text(selector?.runId)??journey.runId; const basisId=text(selector?.basisId)??journey.basisId;
  if(!journey.caseId||!runId||!basisId||typeof adapter==='undefined'||adapter===null)return {ok:false,history:null,error:'Unable to access delivered ARR older history'};
  try{const reader=typeof adapter==='function'?adapter:adapter.read;if(typeof reader!=='function')return {ok:false,history:null,error:'Unable to access delivered ARR older history'}; const response=await reader({caseId:journey.caseId,runId,basisId});
    if(!isObject(response)||response.caseId!==journey.caseId||response.runId!==runId||response.basisId!==basisId||!Array.isArray(response.events))return {ok:false,history:null,error:'Out-of-scope or invalid older history response'};
    const history={...response,history:response.events,olderHistoryAvailable:journey.olderHistoryAvailable}; const result=journey.caseId==='cp-case-b'?buildCaseBJourney(history):journey.caseId==='cp-case-c'?buildCaseCJourney(history):buildCaseAJourney(history,journey.caseId); result.olderHistoryAccess='Older ARR history loaded'; return {ok:true,history:result,error:null};
  }catch{return {ok:false,history:null,error:'Unable to access delivered ARR older history'};}
}
export const openOlderHistory=openCaseAOlderHistory;

export type GuidedJourneyProps={scene?:unknown;envelope?:unknown;adapter?:OlderHistoryAdapter|{read?:OlderHistoryAdapter};onActivate?:()=>unknown};
export function GuidedJourney({scene,envelope,adapter,onActivate}:GuidedJourneyProps){
  const source=scene??envelope; const caseObject=isObject(source)?(isObject(source.case)?source.case:source):{};
  const caseId=text(caseObject.caseId)??text(isObject(source)?source.caseId:null)??'cp-case-a';
  const build=(value:unknown)=>caseId==='cp-case-b'?buildCaseBJourney(value):caseId==='cp-case-c'?buildCaseCJourney(value):buildCaseAJourney(value,caseId);
  const initial=build(source); const [journey,setJourney]=useState(initial); const [message,setMessage]=useState<string|null>(initial.olderHistoryAvailable?'ARR older history available':'Older history unavailable'); const [loading,setLoading]=useState(false);
  async function open(){setLoading(true);const result=await openCaseAOlderHistory(journey,adapter);setLoading(false);if(result.ok&&result.history)setJourney(result.history);setMessage(result.error??result.history?.olderHistoryAccess??null);}
  const h=createElement;
  const journeyName=journey.caseId==='cp-case-b'?'Case B':journey.caseId==='cp-case-c'?'Case C':'Case A';
  return h('section',{ 'aria-label':journeyName+' guided journey'},
    h('h2',null,`${journeyName} guided journey`),
    message?h('p',{role:message.includes('Unable')||message.includes('scope')?'alert':'status'},message):null,
    journey.olderHistoryAvailable?h('button',{type:'button',onClick:()=>void open(),disabled:loading,'aria-label':'Open delivered ARR older history'},'Open delivered ARR older history'):null,
    h('ol',{ 'aria-label':journeyName+' journey steps'},journey.steps.map((step)=>h('li',{key:step.id},
      h('button',{type:'button',disabled:!step.available,'aria-disabled':!step.available,onClick:()=>onActivate?.(),'aria-label':step.label},step.label),
      step.summary?h('span',null,` — ${step.summary}`):null,
      step.runId?h('span',null,` Run: ${step.runId}`):null,
      step.basisId?h('span',null,` Basis: ${step.basisId} v${step.basisVersion}`):null,
      step.actorLabel?h('span',null,` Actor: ${step.actorLabel}`):null,
      step.invalidReason?h('span',{role:'alert'},`Invalid: ${step.invalidReason}`):null,
      ...step.evidence.map((w)=>h('button',{key:w.witnessId,type:'button','aria-label':`Evidence witness ${w.witnessId}`,onClick:()=>onActivate?.()},`${w.displayLabel??`Witness ${w.witnessId}`} page ${w.page}`))
    )))
  );
}
export {GuidedJourney as HangarGuidedJourney}; export default GuidedJourney;
