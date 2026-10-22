import { useMemo, useState } from 'react';
import { HANGAR_ACTUAL_OBJECT_IDS } from './sceneProjection';

const isObject=(v:unknown):v is Record<string,unknown>=>typeof v==='object'&&v!==null&&!Array.isArray(v);
const text=(v:unknown):string|null=>typeof v==='string'&&v.trim().length>0?v:null;
const integer=(v:unknown):number|null=>typeof v==='number'&&Number.isInteger(v)&&v>=0?v:null;

export type HangarEvidenceTuple={documentId:string;version:number;basisId:string;basisVersion:number;displayLabel:string;witnesses:unknown[];status:string|null;blocked:boolean};
export type HangarSelection={objectId:string;caseId:string;kind:string;status:unknown;owner:string;nextAction:string;capabilityLabel:string;evidence:HangarEvidenceTuple[];error:string|null};
export type SelectionReader=(identifier:string)=>unknown|Promise<unknown>;

export function resolveHangarSelection(envelope:unknown, identifier:unknown, expectedManifest?:unknown):HangarSelection|null {
  if(typeof identifier!=='string'||!HANGAR_ACTUAL_OBJECT_IDS.includes(identifier as typeof HANGAR_ACTUAL_OBJECT_IDS[number])||!isObject(envelope)) return null;
  if(!isObject(envelope.case)||!Array.isArray(envelope.objects)||!Array.isArray(envelope.documentRefs)||!Array.isArray(envelope.evidence)) return null;
  if(Array.isArray(expectedManifest) ? !expectedManifest.includes(identifier) : isObject(expectedManifest)&&Array.isArray(expectedManifest.objectIds) ? !expectedManifest.objectIds.includes(identifier) : false) return null;
  const c=envelope.case; const projected=envelope.objects.find((o)=>isObject(o)&&o.objectId===identifier);
  if(!projected||!isObject(projected)||projected.caseId!==c.caseId) return null;
  const prefix=String(c.caseId).slice(-1).toUpperCase(); if(!identifier.includes(`-CP-${prefix==='A'? 'A':prefix==='B'?'B':'C'}-`)) return null;
  const object=isObject(projected)?projected:{};
  const status=isObject(c.status)?c.status:null;
  const action=isObject(c.nextAction)?text(c.nextAction.text):text(c.nextAction);
  const evidenceIds=Array.isArray(object.evidenceIds)?object.evidenceIds.filter((v):v is string=>typeof v==='string'):[];
  const evidence:HangarEvidenceTuple[]=[]; let error:string|null=null;
  for(const evidenceId of evidenceIds){
    const ref=(envelope.documentRefs as unknown[]).find((r)=>isObject(r)&&r.evidenceRefId===evidenceId);
    if(!ref||!isObject(ref)){error='Unresolved evidence mapping';continue;}
    if(ref.caseId!==undefined&&ref.caseId!==c.caseId){error='Cross-case document reference';continue;}
    const documentId=text(ref.documentId); const version=integer(ref.documentVersion);
    const basisId=text(ref.basisId); const basisVersion=integer(ref.basisVersion); const displayLabel=text(ref.displayLabel);
    if(!documentId||version===null||!basisId||basisVersion===null||!displayLabel){error='Unresolved evidence mapping';continue;}
    const processing=text(ref.processingState)??''; const usable=ref.usableAsEvidence===true;
    const normalizedProcessing=processing.toLowerCase();
    const knownBlocked=['blocked','processing','retry','pending','failed','partial','unprocessed'].includes(normalizedProcessing);
    const usableState=usable && (normalizedProcessing===''||normalizedProcessing==='accepted'||normalizedProcessing==='usable');
    if(!usableState || knownBlocked || normalizedProcessing.includes('retry') || normalizedProcessing.includes('reject')){
      const blockedStatus=!usable?(processing|| (ref.retryable===true?'retry':'processing')):(processing||'processing');
      evidence.push({documentId,version,basisId,basisVersion,displayLabel,witnesses:[],status:blockedStatus,blocked:true});continue;
    }
    const witnesses=(envelope.evidence as unknown[]).filter((w)=>isObject(w)&&w.evidenceRefId===evidenceId&&w.caseId!==undefined&&w.caseId!==c.caseId?false:isObject(w)&&w.evidenceRefId===evidenceId&&w.documentId===documentId&&w.documentVersion===version&&w.basisId===basisId&&w.basisVersion===basisVersion).map((w)=>isObject(w)?({witnessId:text(w.witnessId),evidenceRefId:evidenceId,documentId,documentVersion:version,page:integer(w.page),basisId,basisVersion,displayLabel:text(w.displayLabel)}):w);
    evidence.push({documentId,version,basisId,basisVersion,displayLabel,witnesses,status:processing||null,blocked:false});
  }
  const projectedStatus=object.status!==undefined?object.status:(isObject(c.status)?c.status:undefined);
  return {objectId:identifier,caseId:String(c.caseId),kind:text(object.kind)??'object',status:projectedStatus,owner:text(object.owner)??text(c.owner)??'Unknown',nextAction:isObject(object.nextAction)?text(object.nextAction.text)??'Unknown':text(object.nextAction)??(isObject(c.nextAction)?text(c.nextAction.text)??'Unknown':text(c.nextAction)??'Unknown'),capabilityLabel:text(object.capabilityLabel)??text(c.capabilityLabel)??'Unknown',evidence,error};
}
export const activateProjectedObject=resolveHangarSelection;
export const buildHangarSelection=resolveHangarSelection;

const encodeDocumentIdentifier=(identifier:string)=>identifier.split('/').map((part)=>encodeURIComponent(part)).join('/');
const buildContentRequest=(evidence:HangarEvidenceTuple,page?:number)=>{
  const query=new URLSearchParams();
  query.set('version',String(evidence.version));
  if(page!==undefined)query.set('page',String(page));
  query.set('basis',evidence.basisId);
  query.set('basisVersion',String(evidence.basisVersion));
  return `${encodeDocumentIdentifier(evidence.documentId)}?${query.toString()}`;
};

export type HangarSelectionEvidenceProps={envelope?:unknown;scene?:unknown;manifest?:unknown;objectId?:unknown;identifier?:unknown;browserIdentifier?:unknown;reader?:SelectionReader;onRead?:SelectionReader;evidenceReader?:SelectionReader;onContentOpen?:SelectionReader};
export function HangarSelectionEvidence({envelope,scene,manifest,objectId,identifier,browserIdentifier,reader,onRead,evidenceReader,onContentOpen}:HangarSelectionEvidenceProps){
  const source=scene??envelope; const browserSupplied=typeof browserIdentifier==='string'; const supplied=browserSupplied?null:(identifier??objectId); const initialSelected=typeof supplied==='string'?supplied:null; const initialSelection=resolveHangarSelection(source,initialSelected,manifest); const [selectedId,setSelectedId]=useState<string|null>(initialSelected); const [identifierError,setIdentifierError]=useState(initialSelected&&!initialSelection?'Unknown or out-of-scope identifier':browserSupplied?'Browser-supplied identifiers are not accepted':''); const [evidenceError,setEvidenceError]=useState<string|null>(initialSelection?.error??null);
  const projected=useMemo(()=>isObject(source)&&Array.isArray(source.objects)?source.objects.map((o)=>isObject(o)?o:null).filter((o):o is Record<string,unknown>=>!!o):[],[source]);
  const selection=useMemo(()=>resolveHangarSelection(source,selectedId,manifest),[source,selectedId,manifest]);
  function activate(raw:unknown){if(typeof raw!=='string'||typeof selectedId==='string'&&raw!==selectedId&&selectedId!==null){} ; const result=resolveHangarSelection(source,raw,manifest); if(!result){setSelectedId(null);setIdentifierError('Unknown or out-of-scope identifier');setEvidenceError(null);return;} setSelectedId(result.objectId);setIdentifierError('');setEvidenceError(result.error); const read=reader??onRead??evidenceReader;if(typeof read==='function')void read(result.objectId);}
  const read=reader??onRead??evidenceReader;
  return <section aria-label="Hangar object selection"><h2>Hangar object selection</h2>{identifierError&&<p role="alert">{identifierError}</p>}
    {projected.map((o)=>typeof o.objectId==='string'?<button key={o.objectId} type="button" onClick={()=>activate(o.objectId)}>Select {o.objectId}</button>:null)}
    {!selection&&selectedId!==null&&<p role="alert">Visible identifier error</p>}
    {selection&&<section aria-label={`Selection ${selection.objectId}`}><h3>{selection.objectId}</h3>
      <p>Status: {typeof selection.status==='object'&&selection.status!==null?Object.entries(selection.status).map(([key,value])=>`${key}: ${value ?? 'none'}`).join('; '):String(selection.status)}</p>
      <p>Owner: {selection.owner}</p><p>Next action: {selection.nextAction}</p><p>Capability: {selection.capabilityLabel}</p>
      {evidenceError&&<p role="alert">{evidenceError}</p>}
      {selection.evidence.map((e,i)=>{const blocked=e.blocked || e.status!==null&&(['blocked','processing','retry','pending','failed','partial','unprocessed'].some(s=>e.status!.toLowerCase().includes(s)));return <article key={`${e.documentId}-${e.version}-${e.basisId}-${e.basisVersion}-${i}`}>
        <p>{e.documentId} v{e.version} / {e.basisId} v{e.basisVersion}</p>
        {e.status!==null&&e.status.length>0&&<p>{e.displayLabel}</p>}
        {blocked?<p role="status">Evidence processing/retry: {e.status}</p>:<div aria-label={`ARR content controls for ${e.documentId}`}><button type="button" onClick={()=>{const content=onContentOpen??read;if(typeof content==='function')void content(buildContentRequest(e))}}>Open content</button>{e.witnesses.map((w,j)=>{const witness=isObject(w)?w:null;const page=witness?.page;return typeof page==='number'?<button key={j} type="button" onClick={()=>{const content=onContentOpen??read;if(typeof content==='function')void content(buildContentRequest(e,page))}}>{`Page witness ${page}`}</button>:null})}</div>}
      </article>})}
    </section>}
  </section>;
}
export default HangarSelectionEvidence;
