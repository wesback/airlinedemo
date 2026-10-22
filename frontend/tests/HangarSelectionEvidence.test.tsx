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
    cleanup();
    const unknownRead=vi.fn(); render(<HangarSelectionEvidence envelope={envelope()} manifest={manifest} browserIdentifier="SYN-CP-B-AIR-001" reader={unknownRead}/>);
    expect(screen.getByRole('alert').textContent).toContain('Browser-supplied'); expect(screen.queryByRole('region',{name:/Selection/})).toBeNull(); expect(unknownRead).not.toHaveBeenCalled();
    cleanup();
    const casesRead=vi.fn(); render(<HangarSelectionEvidence envelope={envelope()} manifest={manifest} identifier="SYN-CP-A-AIR-001" reader={casesRead}/>);
    fireEvent.click(screen.getByRole('button',{name:'Select SYN-CP-A-AIR-001'}));
    expect(casesRead).toHaveBeenCalledTimes(1); expect(casesRead).toHaveBeenCalledWith('SYN-CP-A-AIR-001');
    expect(screen.getByRole('region',{name:'Selection SYN-CP-A-AIR-001'})).toBeTruthy();
    cleanup();
    const crossCaseEnvelope=envelope({objects:[{objectId:'SYN-CP-B-AIR-001',kind:'aircraft',caseId:'cp-case-b',evidenceIds:[],status:{assessment:'ready'},owner:'Cross-case owner',nextAction:{text:'Cross-case action'},capabilityLabel:'Rules-based'}],case:{caseId:'cp-case-a',caseRevision:7,status:{assessment:'ready'},owner:'Technical reviewer',nextAction:{text:'Review evidence'},capabilityLabel:'Rules-based'}});
    const crossCaseRead=vi.fn(); render(<HangarSelectionEvidence envelope={crossCaseEnvelope} manifest={manifest} identifier="SYN-CP-B-AIR-001" reader={crossCaseRead}/>);
    expect(screen.getByText('Unknown or out-of-scope identifier')).toBeTruthy(); expect(screen.queryByRole('region',{name:/Selection/})).toBeNull(); expect(crossCaseRead).not.toHaveBeenCalled();
    cleanup();
    const absentRead=vi.fn(); render(<HangarSelectionEvidence envelope={envelope()} manifest={manifest} identifier="DOES-NOT-EXIST" reader={absentRead}/>);
    expect(screen.getByText('Unknown or out-of-scope identifier')).toBeTruthy(); expect(screen.queryByRole('region',{name:/Selection/})).toBeNull(); expect(absentRead).not.toHaveBeenCalled();
  });
  it('HangarSelectionEvidence lists exact evidence tuples',()=>{
    const read=vi.fn(); render(<HangarSelectionEvidence envelope={envelope()} manifest={manifest} identifier="SYN-CP-A-CMP-001" onContentOpen={read}/>);
    expect(screen.getByText('case-a/manual.txt v2 / basis-v2 v2')).toBeTruthy(); expect(screen.getByRole('button',{name:'Open content'})).toBeTruthy(); expect(screen.getByRole('button',{name:'Page witness 3'})).toBeTruthy();
    fireEvent.click(screen.getByRole('button',{name:'Page witness 3'})); expect(read).toHaveBeenCalledWith('case-a/manual.txt?version=2&page=3&basis=basis-v2&basisVersion=2');
    cleanup();
    const missing=resolveHangarSelection(envelope({documentRefs:[]}), 'SYN-CP-A-CMP-001', manifest); expect(missing?.error).toBe('Unresolved evidence mapping');
    const missingRead=vi.fn(); render(<HangarSelectionEvidence envelope={envelope({documentRefs:[]})} manifest={manifest} identifier="SYN-CP-A-CMP-001" reader={missingRead}/>);
    expect(screen.getByRole('alert').textContent).toBe('Unresolved evidence mapping'); expect(screen.getByText('SYN-CP-A-CMP-001')).toBeTruthy(); expect(screen.queryByText(/manual\.txt v|basis-v/)).toBeNull(); expect(missingRead).not.toHaveBeenCalled();
    cleanup();
    const crossCaseRef=envelope({documentRefs:[{evidenceRefId:'ref-1',caseId:'cp-case-b',documentId:'case-b/manual.txt',documentVersion:99,basisId:'basis-b',basisVersion:99,displayLabel:'Wrong case manual',processingState:'accepted',retryable:false,usableAsEvidence:true}],evidence:[{witnessId:'wrong-witness',evidenceRefId:'ref-1',caseId:'cp-case-b',documentId:'case-b/manual.txt',documentVersion:99,page:99,basisId:'basis-b',basisVersion:99}]});
    const crossRead=vi.fn(); render(<HangarSelectionEvidence envelope={crossCaseRef} manifest={manifest} identifier="SYN-CP-A-CMP-001" reader={crossRead}/>);
    expect(screen.getByRole('alert').textContent).toBe('Cross-case document reference'); expect(screen.queryByText(/case-b\/manual\.txt/)).toBeNull(); expect(screen.queryByText(/99 v99/)).toBeNull(); expect(crossRead).not.toHaveBeenCalled();
  });
  it('HangarSelectionEvidence blocks witnesses and renders markup as text',()=>{
    const read=vi.fn(); const blocked=envelope({documentRefs:[{evidenceRefId:'ref-1',documentId:'case-a/blocked.txt',documentVersion:4,basisId:'basis-v1',basisVersion:1,displayLabel:'<img src=x onerror=alert(1)>',processingState:'rejected-retryable',retryable:true,usableAsEvidence:false}],evidence:[{witnessId:'secret',evidenceRefId:'ref-1',documentId:'case-a/blocked.txt',documentVersion:4,page:9,basisId:'basis-v1',basisVersion:1,displayLabel:'SECRET-WITNESS'}]});
    const {container}=render(<HangarSelectionEvidence envelope={blocked} manifest={manifest} identifier="SYN-CP-A-CMP-001" onContentOpen={read}/>);
    expect(screen.getByRole('status').textContent).toContain('Evidence processing/retry: rejected-retryable'); expect(screen.queryByRole('button',{name:'Open content'})).toBeNull(); expect(screen.queryByRole('button',{name:/witness/i})).toBeNull(); expect(screen.queryByText('SECRET-WITNESS')).toBeNull(); expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeTruthy(); expect(container.querySelector('img')).toBeNull(); expect(read).not.toHaveBeenCalled();
    cleanup();
    const unknownState=envelope({documentRefs:[{evidenceRefId:'ref-1',documentId:'case-a/queued.txt',documentVersion:5,basisId:'basis-v1',basisVersion:1,displayLabel:'Queued evidence',processingState:'queued-review',retryable:false,usableAsEvidence:false}],evidence:[{witnessId:'hidden',evidenceRefId:'ref-1',documentId:'case-a/queued.txt',documentVersion:5,page:12,basisId:'basis-v1',basisVersion:1,displayLabel:'HIDDEN-WITNESS'}]});
    render(<HangarSelectionEvidence envelope={unknownState} manifest={manifest} identifier="SYN-CP-A-CMP-001" onContentOpen={read}/>);
    expect(screen.getByRole('status').textContent).toBe('Evidence processing/retry: queued-review'); expect(screen.queryByRole('button',{name:'Open content'})).toBeNull(); expect(screen.queryByRole('button',{name:/witness/i})).toBeNull(); expect(screen.queryByText('HIDDEN-WITNESS')).toBeNull(); expect(read).not.toHaveBeenCalled();
  });
});
