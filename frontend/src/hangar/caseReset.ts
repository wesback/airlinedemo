import { createElement, useRef, useState, type ReactNode } from 'react';

export const CASE_RESET_CONTRACT_VERSION = 1;
export const CASE_RESET_VARIANT = 'blocked' as const;
export const CASE_RESET_REJECTION_STATUSES = new Set([400, 403, 404, 409, 422]);
export const CASE_RESET_UNCERTAIN_STATUSES = new Set([0, 408, 429, 500, 502, 503, 504]);
export type CaseResetResult = { accepted: boolean; uncertain: boolean; rejected: boolean; status: number|null; payload: CaseResetPayload|null };
export type CaseResetPayload = { contractVersion: 1; commandId: string; expectedRevision: number; variant: 'blocked' };
export type CaseResetTransport = (url: string, init: { method: 'POST'; headers: Record<string,string>; body: string }) => Promise<unknown>;
export type CaseResetRefetch = () => unknown | Promise<unknown>;

const objectOf=(v:unknown):v is Record<string,unknown>=>typeof v==='object'&&v!==null&&!Array.isArray(v);
let resetSequence=0;
const uncertainReset = new Map<string,{commandId:string;payload:CaseResetPayload}>();
export function resetHangarCaseResetState():void { uncertainReset.clear(); resetSequence=0; }
export function createHangarCaseResetCommandId(revision:number):string { resetSequence+=1; return `cp-reset-${revision}-${resetSequence}`; }
export function getHangarCaseResetPermission(envelope:unknown): {allowed:boolean;revision:number|null} {
  if(!objectOf(envelope)||envelope.current===false)return {allowed:false,revision:null};
  if(!objectOf(envelope.case)||envelope.contractVersion!==1)return {allowed:false,revision:null};
  const c=envelope.case;
  if(envelope.canResetBlockedVariant!==true||c.caseId!== 'cp-case-c'||typeof c.caseRevision!=='number'&&typeof c.revision!=='number')return {allowed:false,revision:null};
  const revision=typeof c.caseRevision==='number'?c.caseRevision:c.revision as number;
  const permission=c.permission ?? c.permissions;
  const permissionObject=objectOf(permission)?permission:null;
  const permissionRevision=c.permissionRevision ?? envelope.permissionRevision ?? permissionObject?.expectedRevision ?? permissionObject?.revision;
  if(permissionRevision!==undefined&&(permissionRevision!==revision))return {allowed:false,revision:null};
  if(permission===undefined)return {allowed:false,revision:null};
  const permitted=permission===true||(permissionObject?.canResetBlockedVariant===true);
  if(!permitted)return {allowed:false,revision:null};
  const allowedActions=Array.isArray(c.allowedActions)?c.allowedActions:Array.isArray(envelope.allowedActions)?envelope.allowedActions:[];
  if(!allowedActions.includes('reset')&&!allowedActions.includes('resetBlockedVariant'))return {allowed:false,revision:null};
  return {allowed:true,revision:Number.isInteger(revision)&&revision>=0?revision:null};
}
export const canResetHangarCase= getHangarCaseResetPermission;
export function buildHangarCaseResetPayload(envelope:unknown, commandId:unknown): CaseResetPayload|null {
  const permission=getHangarCaseResetPermission(envelope);
  if(!permission.allowed||permission.revision===null||typeof commandId!== 'string'||!/^[A-Za-z0-9_-]{1,64}$/.test(commandId))return null;
  return {contractVersion:CASE_RESET_CONTRACT_VERSION,commandId,expectedRevision:permission.revision,variant:CASE_RESET_VARIANT};
}
export const buildCaseResetPayload=buildHangarCaseResetPayload;
export function validateHangarCaseResetPayload(payload:unknown, envelope:unknown):payload is CaseResetPayload {
  if(!objectOf(payload)||Object.keys(payload).length!==4)return false;
  const permission=getHangarCaseResetPermission(envelope); if(!permission.allowed||permission.revision===null)return false;
  const current=buildHangarCaseResetPayload(envelope,payload.commandId);
  return current!==null&&payload.contractVersion===1&&payload.variant==='blocked'&&payload.expectedRevision===permission.revision&&payload.commandId===current.commandId;
}

export type HangarCaseResetProps={envelope?:unknown;scene?:unknown;csrfToken?:unknown;transport?:CaseResetTransport;fetcher?:CaseResetTransport;refetch?:CaseResetRefetch;reader?:CaseResetRefetch;commandId?:string;commandIdFactory?:((revision:number)=>string)|null;confirmReset?:()=>boolean;children?:ReactNode};
const responseStatus=(response:unknown):number|null=>objectOf(response)&&typeof response.status==='number'?response.status:null;
const responseOk=(response:unknown,status:number|null):boolean=>objectOf(response)&&typeof response.ok==='boolean'?response.ok:status!==null&&status>=200&&status<300;
export async function submitHangarCaseReset(options:{envelope:unknown;csrfToken?:unknown;commandId?:string;transport?:CaseResetTransport;refetch?:CaseResetRefetch}):Promise<CaseResetResult> {
  const permission=getHangarCaseResetPermission(options.envelope); if(!permission.allowed||permission.revision===null)return {accepted:false,uncertain:false,rejected:true,status:400,payload:null as never};
  const commandId=typeof options.commandId==='string'?options.commandId:createHangarCaseResetCommandId(permission.revision);
  const retained=[...uncertainReset.values()].find((entry)=>entry.commandId===commandId)?.payload;
  const payload=retained?{...retained}:buildHangarCaseResetPayload(options.envelope,commandId); if(!payload||typeof options.csrfToken!=='string'||options.csrfToken.length===0)return {accepted:false,uncertain:false,rejected:true,status:400,payload};
  const transport=options.transport;if(typeof transport!=='function')return {accepted:false,uncertain:false,rejected:true,status:400,payload};
  let response:unknown; let status:number|null=null;
  try { response=await transport(`/api/v1/cases/cp-case-c/reset-blocked-variant`,{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':options.csrfToken},body:JSON.stringify(payload)}); status=responseStatus(response); }
  catch { rememberUncertainHangarCaseReset(payload); return {accepted:false,uncertain:true,rejected:false,status:null,payload}; }
  if(status!==null&&CASE_RESET_REJECTION_STATUSES.has(status))return {accepted:false,uncertain:false,rejected:true,status,payload};
  if(status!==null&&(status>=500||status===0)){ rememberUncertainHangarCaseReset(payload); return {accepted:false,uncertain:true,rejected:false,status,payload}; }
  if(responseOk(response,status)){if(typeof options.refetch==='function')await Promise.resolve(options.refetch());return {accepted:true,uncertain:false,rejected:false,status,payload};}
  return status===null?{accepted:false,uncertain:true,rejected:false,status,payload}:{accepted:false,uncertain:false,rejected:true,status,payload};
}
export const sendHangarCaseReset=submitHangarCaseReset;
export function rememberUncertainHangarCaseReset(payload:CaseResetPayload):CaseResetPayload {
  const key=`${payload.commandId}:${payload.expectedRevision}`; uncertainReset.set(key,{commandId:payload.commandId,payload:{...payload}}); return {...payload};
}
export function retryUncertainHangarCaseReset(original:unknown, candidate?:unknown):CaseResetPayload|null {
  if(!objectOf(original)||typeof original.commandId!=='string'||typeof original.expectedRevision!=='number')return null;
  const key=`${original.commandId}:${original.expectedRevision}`; const retained=uncertainReset.get(key); if(!retained)return null;
  if(candidate!==undefined&&(!objectOf(candidate)||candidate.commandId!==original.commandId||candidate.expectedRevision!==original.expectedRevision))return null;
  return {...retained.payload};
}
export function HangarCaseReset({envelope,scene,csrfToken,transport,fetcher,refetch,reader,commandId,commandIdFactory,confirmReset,children}:HangarCaseResetProps){
  const source=scene??envelope; const permission=getHangarCaseResetPermission(source); const [message,setMessage]=useState(''); const [busy,setBusy]=useState(false); const retained=useRef<{commandId:string;payload:CaseResetPayload}|null>(null);
  if(!permission.allowed)return createElement('span',{hidden:true},children??null);
  async function reset(){if(busy)return; if(confirmReset?confirmReset():typeof window==='undefined'||!window.confirm?false:window.confirm('Reset the Case C blocked variant?')){
    setBusy(true); const id=retained.current?.commandId??commandId??commandIdFactory?.(permission.revision!)??createHangarCaseResetCommandId(permission.revision!); const payload=retained.current?.payload??buildHangarCaseResetPayload(source,id); if(!payload){setBusy(false);return;} retained.current={commandId:id,payload};
    const result=await submitHangarCaseReset({envelope:source,csrfToken:typeof csrfToken==='string'?csrfToken:undefined,commandId:id,transport:transport??fetcher,refetch:refetch??reader}); setBusy(false);
    setMessage(result.accepted?'Case C blocked variant reset accepted.':result.uncertain?'Reset result uncertain. Retry without changing the command.':`Reset rejected (${result.status}).`);
  }}
  return createElement('div',{'aria-label':'Case C blocked variant reset'},createElement('button',{type:'button',name:'Reset blocked variant','aria-label':'Reset blocked variant',disabled:busy,onClick:()=>void reset()},'Reset blocked variant'),message&&createElement('p',{role:'status'},message));
}
export default HangarCaseReset;
