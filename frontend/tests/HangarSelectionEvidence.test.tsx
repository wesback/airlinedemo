import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarSelectionEvidence, { resolveHangarSelection } from '../src/hangar/selectionPanel';

const envelope=(overrides:Record<string,unknown>={})=>({contractVersion:1,case:{caseId:'cp-case-a',caseRevision:7,status:{assessment:'ready',review:'pending'},owner:'Technical reviewer',nextAction:{actionId:'review',text:'Review evidence'},capabilityLabel:'Rules-based'},objects:[{objectId:'SYN-CP-A-AIR-001',kind:'aircraft',caseId:'cp-case-a',evidenceIds:[],status:{assessment:'ready',review:'pending'},owner:'Technical reviewer',nextAction:{actionId:'review',text:'Review evidence'},capabilityLabel:'Rules-based'},{objectId:'SYN-CP-A-ENG-001',kind:'engine',caseId:'cp-case-a',evidenceIds:[],status:{assessment:'ready',review:'pending'},owner:'Technical reviewer',nextAction:{actionId:'review',text:'Review evidence'},capabilityLabel:'Rules-based'},{objectId:'SYN-CP-A-CMP-001',kind:'component',caseId:'cp-case-a',evidenceIds:['ref-1'],status:{assessment:'ready',review:'pending'},owner:'Technical reviewer',nextAction:{actionId:'review',text:'Review evidence'},capabilityLabel:'Rules-based'},{objectId:'SYN-CP-A-CMP-002',kind:'component',caseId:'cp-case-a',evidenceIds:[],status:{assessment:'ready',review:'pending'},owner:'Technical reviewer',nextAction:{actionId:'review',text:'Review evidence'},capabilityLabel:'Rules-based'}],documentRefs:[{evidenceRefId:'ref-1',documentId:'case-a/manual.txt',documentVersion:2,basisId:'basis-v2',basisVersion:2,displayLabel:'Engine manual',processingState:'accepted',retryable:false,usableAsEvidence:true}],evidence:[{witnessId:'wit-1',evidenceRefId:'ref-1',documentId:'case-a/manual.txt',documentVersion:2,page:3,basisId:'basis-v2',basisVersion:2,displayLabel:'Engine manual'}],history:[],canResetBlockedVariant:false,allowedActions:[],...overrides});
const manifest={objectIds:['SYN-CP-A-AIR-001','SYN-CP-A-ENG-001','SYN-CP-A-CMP-001','SYN-CP-A-CMP-002']};

afterEach(()=>{cleanup();vi.unstubAllGlobals();});

describe('HangarSelectionEvidence',()=>{
  it('HangarSelectionEvidence only opens projected objects',()=>{
    const read=vi.fn(); render(<HangarSelectionEvidence envelope={envelope()} manifest={manifest} identifier="SYN-CP-A-AIR-001" reader={read}/>);
    expect(screen.getByRole('region',{name:'Selection SYN-CP-A-AIR-001'})).toBeTruthy();
    expect(screen.getByText(/assessment: ready; review: pending/)).toBeTruthy(); expect(screen.getByText('Owner: Technical reviewer')).toBeTruthy();
    expect(screen.getByText('Next action: Review evidence')).toBeTruthy(); expect(screen.getByText('Capability: Rules-based')).toBeTruthy();
    expect(read).not.toHaveBeenCalled();
    cleanup(); const unknownRead=vi.fn(); render(<HangarSelectionEvidence envelope={envelope()} manifest={manifest} browserIdentifier="SYN-CP-B-AIR-001" reader={unknownRead}/>);
    expect(screen.getByRole('alert').textContent).toContain('Browser-supplied'); expect(screen.queryByRole('region',{name:/Selection/})).toBeNull(); expect(unknownRead).not.toHaveBeenCalled();
  });
  it('HangarSelectionEvidence lists exact evidence tuples',()=>{
    const read=vi.fn(); render(<HangarSelectionEvidence envelope={envelope()} manifest={manifest} identifier="SYN-CP-A-CMP-001" onContentOpen={read}/>);
    expect(screen.getByText('case-a/manual.txt v2 / basis-v2 v2')).toBeTruthy(); expect(screen.getByRole('button',{name:'Open content'})).toBeTruthy(); expect(screen.getByRole('button',{name:'Page witness 3'})).toBeTruthy();
    fireEvent.click(screen.getByRole('button',{name:'Page witness 3'})); expect(read).toHaveBeenCalledWith('case-a/manual.txt?version=2&page=3&basis=basis-v2&basisVersion=2');
    cleanup(); const missing=resolveHangarSelection(envelope({documentRefs:[]}), 'SYN-CP-A-CMP-001', manifest); expect(missing?.error).toBe('Unresolved evidence mapping');
  });
  it('HangarSelectionEvidence blocks witnesses and renders markup as text',()=>{
    const read=vi.fn(); const blocked=envelope({documentRefs:[{evidenceRefId:'ref-1',documentId:'case-a/blocked.txt',documentVersion:4,basisId:'basis-v1',basisVersion:1,displayLabel:'<img src=x onerror=alert(1)>',processingState:'rejected-retryable',retryable:true,usableAsEvidence:false}],evidence:[{witnessId:'secret',evidenceRefId:'ref-1',documentId:'case-a/blocked.txt',documentVersion:4,page:9,basisId:'basis-v1',basisVersion:1,displayLabel:'SECRET-WITNESS'}]});
    const {container}=render(<HangarSelectionEvidence envelope={blocked} manifest={manifest} identifier="SYN-CP-A-CMP-001" onContentOpen={read}/>);
    expect(screen.getByRole('status').textContent).toContain('Evidence processing/retry: rejected-retryable'); expect(screen.queryByRole('button',{name:'Open content'})).toBeNull(); expect(screen.queryByRole('button',{name:/witness/i})).toBeNull(); expect(screen.queryByText('SECRET-WITNESS')).toBeNull(); expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeTruthy(); expect(container.querySelector('img')).toBeNull(); expect(read).not.toHaveBeenCalled();
  });
});
