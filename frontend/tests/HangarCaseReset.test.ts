import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  HangarCaseReset, buildHangarCaseResetPayload, createHangarCaseResetCommandId,
  getHangarCaseResetPermission, rememberUncertainHangarCaseReset,
  resetHangarCaseResetState, retryUncertainHangarCaseReset, submitHangarCaseReset,
} from '../src/hangar/caseReset';
import { cleanup, render, screen } from '@testing-library/react';
import { createElement } from 'react';
import userEvent from '@testing-library/user-event';

afterEach(()=>{cleanup();resetHangarCaseResetState();vi.restoreAllMocks();});
const envelope=(revision=7, overrides:Record<string,unknown>={})=>({
  contractVersion:1,packVersion:'1.0.2',current:true,
  case:{caseId:'cp-case-c',caseRevision:revision,status:{assessment:'ready',review:'blocked'},owner:'Technical reviewer',nextAction:{actionId:'reset',text:'Reset blocked variant'},capabilityLabel:'Rules-based',permission:{canResetBlockedVariant:true},allowedActions:['resetBlockedVariant']},
  canResetBlockedVariant:true,allowedActions:['resetBlockedVariant'],history:[],evidence:[],documentRefs:[],
  objects:[
    {objectId:'SYN-CP-C-AIR-001',kind:'aircraft',caseId:'cp-case-c'},
    {objectId:'SYN-CP-C-ENG-001',kind:'engine',caseId:'cp-case-c'},
    {objectId:'SYN-CP-C-CMP-001',kind:'component',caseId:'cp-case-c'},
    {objectId:'SYN-CP-C-CMP-002',kind:'component',caseId:'cp-case-c'},
  ],...overrides,
});

describe('HangarCaseReset',()=>{
  it('HangarCaseReset hides reset without current permission',()=>{
    const falsePermission=envelope(7,{case:{...envelope().case,permission:false}});
    const missingPermission=envelope(7,{case:{...envelope().case,permission:undefined}});
    const stale=envelope(7,{current:false});
    const mismatched=envelope(7,{permissionRevision:6});
    for(const source of [falsePermission,missingPermission,stale,mismatched]){
      const transport=vi.fn(); const result=render(createElement(HangarCaseReset,{envelope:source,csrfToken:'csrf-123',transport,confirmReset:()=>true}));
      expect(screen.queryByRole('button',{name:'Reset blocked variant'})).toBeNull();
      expect(transport).not.toHaveBeenCalled(); result.unmount();
    }
    expect(getHangarCaseResetPermission(envelope())).toEqual({allowed:true,revision:7});
  });

  it('HangarCaseReset sends exact CP command and refetches',async()=>{
    const transport=vi.fn(async()=>({ok:true,status:200})); const refetch=vi.fn();
    const commandId='cp-reset-7'; const source=envelope();
    vi.spyOn(window,'confirm').mockReturnValue(true);
    render(createElement(HangarCaseReset,{envelope:source,csrfToken:'csrf-123',transport,refetch,commandId,confirmReset:()=>true}));
    await userEvent.setup().click(screen.getByRole('button',{name:'Reset blocked variant'}));
    expect(transport).toHaveBeenCalledTimes(1);
    const [url,init]=transport.mock.calls[0] as unknown as [string,{method:string;headers:Record<string,string>;body:string}];
    expect(url).toBe('/api/v1/cases/cp-case-c/reset-blocked-variant');
    expect(init.method).toBe('POST'); expect(init.headers['X-CSRF-Token']).toBe('csrf-123');
    expect(JSON.parse(init.body)).toEqual({contractVersion:1,commandId:'cp-reset-7',expectedRevision:7,variant:'blocked'});
    expect(Object.keys(JSON.parse(init.body)).sort()).toEqual(['commandId','contractVersion','expectedRevision','variant']);
    expect(refetch).toHaveBeenCalledTimes(1);

    const omitted=buildHangarCaseResetPayload(source,'cp-reset-7'); expect(omitted?.expectedRevision).toBe(7);
    const noCsrf=await submitHangarCaseReset({envelope:source,transport,commandId:'cp-reset-7'}); expect(noCsrf.rejected).toBe(true); expect(transport).toHaveBeenCalledTimes(1);
  });

  it('HangarCaseReset handles rejection and uncertain retry',async()=>{
    for(const status of [400,403,404,409,422]){
      const transport=vi.fn(async()=>({ok:false,status})); const refetch=vi.fn(); const source=envelope();
      const first=await submitHangarCaseReset({envelope:source,csrfToken:'csrf-123',commandId:'cp-reset-retry',transport,refetch});
      expect(first).toMatchObject({accepted:false,uncertain:false,rejected:true,status}); expect(refetch).not.toHaveBeenCalled();
    }
    const uncertainTransport=vi.fn(async()=>{throw new Error('network lost');});
    const source=envelope(); const originalPayload={contractVersion:1 as const,commandId:'cp-reset-7',expectedRevision:7,variant:'blocked' as const};
    rememberUncertainHangarCaseReset(originalPayload);
    expect(retryUncertainHangarCaseReset(originalPayload)).toEqual(originalPayload);
    expect(retryUncertainHangarCaseReset(originalPayload,{commandId:'cp-reset-new',expectedRevision:8})).toBeNull();
    expect(retryUncertainHangarCaseReset(originalPayload,{commandId:'cp-reset-7',expectedRevision:7})).toEqual(originalPayload);
    const first=await submitHangarCaseReset({envelope:source,csrfToken:'csrf-123',commandId:'cp-reset-7',transport:uncertainTransport});
    expect(first).toMatchObject({accepted:false,uncertain:true,rejected:false,status:null}); expect(first.payload).toEqual(originalPayload);
    const duplicateId=await submitHangarCaseReset({envelope:source,csrfToken:'csrf-123',commandId:'cp-reset-7',transport:uncertainTransport});
    expect(duplicateId.payload).toEqual(originalPayload);
  });
});
