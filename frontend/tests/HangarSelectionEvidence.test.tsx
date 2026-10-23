import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import * as THREE from 'three';
import HangarSelectionEvidence, { resolveHangarSelection, buildHangarSelectedObjectLabel } from '../src/hangar/selectionPanel';
import { HangarCanvasAdapter, createHangarOwnedPerspectiveCamera } from '../src/hangar/HangarCanvasAdapter';

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
    const crossCaseManifest={objectIds:[...manifest.objectIds,'SYN-CP-B-AIR-001']};
    const crossCaseRead=vi.fn(); render(<HangarSelectionEvidence envelope={crossCaseEnvelope} manifest={crossCaseManifest} identifier="SYN-CP-B-AIR-001" reader={crossCaseRead}/>);
    expect(screen.getByText('Unknown or out-of-scope identifier')).toBeTruthy(); expect(screen.queryByRole('region',{name:/Selection/})).toBeNull(); expect(crossCaseRead).not.toHaveBeenCalled();
    cleanup();
    // A CP-A identifier passes the case prefix check; only the projected object's caseId (CP-B) rejects it.
    const foreignOwnerEnvelope=envelope({objects:[{objectId:'SYN-CP-A-AIR-001',kind:'aircraft',caseId:'cp-case-b',evidenceIds:[],status:{assessment:'ready'},owner:'Cross-case owner',nextAction:{text:'Cross-case action'},capabilityLabel:'Rules-based'}]});
    expect(resolveHangarSelection(foreignOwnerEnvelope,'SYN-CP-A-AIR-001',manifest)).toBeNull();
    const foreignOwnerRead=vi.fn(); render(<HangarSelectionEvidence envelope={foreignOwnerEnvelope} manifest={manifest} reader={foreignOwnerRead}/>);
    fireEvent.click(screen.getByRole('button',{name:'Select SYN-CP-A-AIR-001'}));
    expect(screen.getByText('Unknown or out-of-scope identifier')).toBeTruthy(); expect(screen.queryByRole('region',{name:/Selection/})).toBeNull(); expect(screen.queryByText(/Cross-case/)).toBeNull(); expect(foreignOwnerRead).not.toHaveBeenCalled();
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
    cleanup();
    const encodedRead=vi.fn(); const encoded=envelope({documentRefs:[{evidenceRefId:'ref-1',documentId:'case-a/manual ?draft.txt',documentVersion:2,basisId:'basis&beta=evil',basisVersion:2,displayLabel:'Encoded evidence',processingState:'accepted',retryable:false,usableAsEvidence:true}],evidence:[{witnessId:'wit-encoded',evidenceRefId:'ref-1',documentId:'case-a/manual ?draft.txt',documentVersion:2,page:3,basisId:'basis&beta=evil',basisVersion:2,displayLabel:'Encoded witness'}]});
    render(<HangarSelectionEvidence envelope={encoded} manifest={manifest} identifier="SYN-CP-A-CMP-001" onContentOpen={encodedRead}/>);
    fireEvent.click(screen.getByRole('button',{name:'Open content'}));
    expect(encodedRead).toHaveBeenCalledWith('case-a/manual%20%3Fdraft.txt?version=2&basis=basis%26beta%3Devil&basisVersion=2');
  });
  it('Hangar labels use projected server text',()=>{
    const scene=new THREE.Group(); for(const [letter,x] of [['A',-28],['B',-4],['C',25]] as const){const node=new THREE.Group();node.name=`Aircraft${letter}`;node.position.set(x,3.5,0);scene.add(node);}
    const engine=new THREE.Group();engine.name='EngineA';engine.userData.objectId='SYN-CP-A-ENG-001';engine.position.set(-28,3,0);scene.add(engine);
    const source={cases:[{caseId:'cp-case-a',name:'Case Alpha',category:'narrow-body',nextAction:{text:'Review evidence'},capabilityLabel:'Rules-based'},{caseId:'cp-case-b',name:'Case Beta',category:'regional',nextAction:{text:'Request evidence'},capabilityLabel:'AI agent'},{caseId:'cp-case-c',name:'Case Gamma',category:'wide-body',nextAction:null,capabilityLabel:'Unknown'}],objects:[{objectId:'SYN-CP-A-ENG-001',kind:'engine',displayName:'Left engine'},{objectId:'SYN-CP-A-CMP-001',kind:'component',displayName:'Main component'}]};
    const camera=createHangarOwnedPerspectiveCamera();camera.position.set(0,30,50);camera.lookAt(0,0,0);camera.updateProjectionMatrix();
    const Canvas=()=>createElement('div',{'data-canvas':'true'});
    render(createElement(HangarCanvasAdapter,{sceneGroup:scene,camera,sceneData:source,selection:'SYN-CP-A-ENG-001',canvasComponent:Canvas}));
    expect(document.querySelectorAll('[data-hangar-label]')).toHaveLength(4);
    expect(document.querySelector('[data-hangar-label="SYN-CP-A-AIR-001"]')?.textContent).toBe('Case Alpha\nnarrow-body\nRules-based\nReview evidence');
    expect(document.querySelector('[data-hangar-label="SYN-CP-A-AIR-001"]')?.getAttribute('aria-hidden')).toBe('true');
    expect(document.querySelector('[data-hangar-label="SYN-CP-A-AIR-001"]')?.getAttribute('role')).toBeNull();
    expect(document.querySelector('[data-hangar-label="SYN-CP-A-ENG-001"]')?.textContent).toBe('Left engine');
    expect(document.querySelector('[data-hangar-label="SYN-CP-C-AIR-001"]')?.textContent).toBe('Case Gamma\nwide-body\nUnknown\nNo next action');

    cleanup();
    const unknownScene=new THREE.Group(); const unknownCanvas=()=>createElement('div',{'data-canvas':'true'});
    render(createElement(HangarCanvasAdapter,{sceneGroup:unknownScene,camera,sceneData:source,selection:'DOES-NOT-EXIST',canvasComponent:unknownCanvas}));
    expect(screen.getByRole('alert').textContent).toBe('Visible mapping error');
    expect(document.querySelector('[data-hangar-label="DOES-NOT-EXIST"]')).toBeNull();
    cleanup();

    const missingScene=new THREE.Group(); for(const [letter,x] of [['B',-4],['C',25]] as const){const node=new THREE.Group();node.name=`Aircraft${letter}`;node.position.set(x,3.5,0);missingScene.add(node);}
    render(createElement(HangarCanvasAdapter,{sceneGroup:missingScene,camera,sceneData:source,selection:null,canvasComponent:Canvas}));
    expect(screen.getByRole('alert').textContent).toBe('Visible mapping error');
    expect(document.querySelector('[data-hangar-label="SYN-CP-A-AIR-001"]')).toBeNull();
    cleanup();

    const duplicateScene=new THREE.Group(); for(const [letter,x] of [['A',-28],['A',-28],['B',-4],['C',25]] as const){const node=new THREE.Group();node.name=`Aircraft${letter}`;node.position.set(x,3.5,0);duplicateScene.add(node);}
    render(createElement(HangarCanvasAdapter,{sceneGroup:duplicateScene,camera,sceneData:source,selection:null,canvasComponent:Canvas}));
    expect(screen.getByRole('alert').textContent).toBe('Visible mapping error');
    expect(document.querySelector('[data-hangar-label="SYN-CP-A-AIR-001"]')).toBeNull();
  });
  it('Hangar labels hide and render as text only',async ()=>{
    const scene=new THREE.Group();const engine=new THREE.Group();engine.name='EngineA';engine.userData.objectId='SYN-CP-A-ENG-001';engine.position.set(0,3,0);scene.add(engine);
    const camera=createHangarOwnedPerspectiveCamera();camera.position.set(0,30,50);camera.lookAt(0,3,0);camera.updateProjectionMatrix();
    const source={objects:[{objectId:'SYN-CP-A-ENG-001',kind:'engine',displayName:'<b>Left engine</b>'}]};const Canvas=()=>createElement('div',{'data-canvas':'true'});
    const {container}=render(createElement(HangarCanvasAdapter,{sceneGroup:scene,camera,sceneData:source,selection:'SYN-CP-A-ENG-001',canvasComponent:Canvas}));
    const label=container.querySelector('[data-hangar-label="SYN-CP-A-ENG-001"]') as HTMLElement|null;expect(label?.textContent).toBe('<b>Left engine</b>');expect(container.querySelector('b')).toBeNull();expect(label?.getAttribute('aria-hidden')).toBe('true');expect(label?.querySelector('button')).toBeNull();
    camera.position.set(0,30,-50);camera.lookAt(0,3,-100);camera.updateProjectionMatrix();await waitFor(()=>expect(label?.style.display).toBe('none'));expect(resolveHangarSelection(envelope(), 'DOES-NOT-EXIST',manifest)).toBeNull();
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
