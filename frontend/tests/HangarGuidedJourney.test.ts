import { cleanup, render, screen } from '@testing-library/react';
import { createElement } from 'react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import GuidedJourney, { buildCaseAJourney, buildCaseBJourney, buildCaseCJourney, openCaseAOlderHistory } from '../src/hangar/guidedJourney';

const witness=(id:string, caseId='cp-case-a')=>({witnessId:id,evidenceRefId:'ref-a',caseId,documentId:'case-a/llp.txt',documentVersion:2,page:4,basisId:'basis-a',basisVersion:2,displayLabel:`Evidence ${id}`});
const event=(eventId:string,sequence:number,eventType:string,witnessIds:string[]=[])=>({eventId,sequence,eventType,runId:'run-a',caseId:'cp-case-a',basisId:'basis-a',basisVersion:2,actorRole:'technical-reviewer',summary:`Recorded ${eventType}`,capabilityLabel:'Rules-based',witnessIds});
const scene=()=>({case:{caseId:'cp-case-a'},currentRunId:'run-a',olderHistoryAvailable:true,history:[event('finding-1',1,'finding-recorded',['wit-finding']),event('review-1',2,'review-completed',['wit-review']),event('request-1',3,'request-created',['wit-request']),event('response-1',4,'partner-response-accepted',['wit-response']),event('basis-1',5,'new-basis-created',['wit-basis']),event('assessment-1',6,'reassessment-completed',['wit-assessment'])],evidence:[witness('wit-finding'),witness('wit-review'),witness('wit-request'),witness('wit-response'),witness('wit-basis'),witness('wit-assessment')]});
afterEach(()=>cleanup());

describe('Case A guided journey',()=>{
  it('Case A journey preserves server history and evidence links',()=>{
    const journey=buildCaseAJourney(scene());
    expect(journey.caseId).toBe('cp-case-a'); expect(journey.runId).toBe('run-a'); expect(journey.basisId).toBe('basis-a'); expect(journey.basisVersion).toBe(2);
    expect(journey.steps.map(s=>s.label)).toEqual(['Finding','Review','Request','Response','New basis','Reassessment']);
    expect(journey.steps.map(s=>s.sequence)).toEqual([1,2,3,4,5,6]);
    journey.steps.forEach(step=>{expect(step.completed).toBe(true);expect(step.available).toBe(true);expect(step.actorLabel).toBe('technical-reviewer');expect(step.sourceEventId).toBeTruthy();expect(step.sourceEventLink).toBe(`#${step.sourceEventId}`);expect(step.runId).toBe('run-a');expect(step.basisId).toBe('basis-a');expect(step.basisVersion).toBe(2);});
    expect(journey.steps[0].witnessIds).toEqual(['wit-finding']); expect(journey.steps[0].witnessId).toBe('wit-finding'); expect(journey.steps[0].evidence[0].page).toBe(4);
    const missing=buildCaseAJourney({...scene(),history:[event('finding-x',1,'finding-recorded',['missing']),event('review-x',2,'review-completed',[]),event('request-x',3,'request-created')]});
    expect(missing.steps[0].available).toBe(false); expect(missing.steps[0].invalidReason).toContain('witness');
    expect(buildCaseAJourney({...scene(),history:[{...event('finding-x',1,'finding-recorded',['missing']),runId:''}]}).steps[0].invalidReason).toBe('Missing run');
    const cross=buildCaseAJourney({...scene(),evidence:[witness('wit-finding','cp-case-b')]}); expect(cross.steps[0].available).toBe(false);

    const historyOnly=buildCaseAJourney({...scene(),history:[event('finding-x',1,'finding-recorded',['wit-finding'])]});
    expect(historyOnly.steps[0].completed).toBe(true);
    historyOnly.steps.slice(1).forEach(step=>{expect(step.completed).toBe(false);expect(step.available).toBe(false);expect(step.future).toBe(true);expect(step.invalidReason).toBe('Future unrecorded step');});
  });

  it('Case A journey opens exact older history',async()=>{
    const user=userEvent.setup(); const adapter=vi.fn(async()=>({caseId:'cp-case-a',runId:'run-a',basisId:'basis-a',events:[event('old-finding',1,'finding-recorded',['old-witness'])],evidence:[witness('old-witness')]}));
    const journey=buildCaseAJourney({...scene(),olderHistoryAvailable:true,currentRunId:'run-a'});
    const result=await openCaseAOlderHistory(journey,{read:adapter},{runId:'run-a',basisId:'basis-a'});
    expect(result.ok).toBe(true);expect(adapter).toHaveBeenCalledWith({caseId:'cp-case-a',runId:'run-a',basisId:'basis-a'});
    const keyboardAdapter=vi.fn(async()=>({caseId:'cp-case-a',runId:'run-a',basisId:'basis-a',events:[event('old-finding',1,'finding-recorded',['old-witness'])],evidence:[witness('old-witness')]}));
    expect(result.history?.steps[0].sourceEventId).toBe('old-finding');expect(result.history?.steps[0].runId).toBe('run-a');expect(result.history?.steps[0].witnessId).toBe('old-witness');expect(result.history?.steps[0].evidence).toEqual([{witnessId:'old-witness',documentId:'case-a/llp.txt',documentVersion:2,page:4,basisId:'basis-a',basisVersion:2,displayLabel:'Evidence old-witness'}]);
    const {container}=render(createElement(GuidedJourney,{scene:scene(),adapter:keyboardAdapter})); const control=screen.getByRole('button',{name:'Open delivered ARR older history'}); expect(keyboardAdapter).toHaveBeenCalledTimes(0); control.focus(); await user.keyboard('{Enter}'); await screen.findByRole('status'); await screen.findByText('Evidence old-witness page 4'); expect(keyboardAdapter).toHaveBeenCalledTimes(1); expect(keyboardAdapter).toHaveBeenCalledWith({caseId:'cp-case-a',runId:'run-a',basisId:'basis-a'}); expect(screen.getByText('Evidence old-witness page 4')).toBeTruthy(); expect(control).toBeTruthy(); expect(container.querySelector('[aria-label="Open delivered ARR older history"]')).toBeTruthy();
    expect((await openCaseAOlderHistory(journey,adapter,{runId:'run-other',basisId:'basis-a'})).error).toContain('Out-of-scope');
    expect((await openCaseAOlderHistory(journey,null)).error).toBe('Unable to access delivered ARR older history');
    expect((await openCaseAOlderHistory(journey,async()=>({caseId:'cp-case-b',runId:'run-old',basisId:'basis-a',events:[]}))).error).toContain('Out-of-scope');
  });

  it('Case A journey rejects markup and extra payload fields',()=>{
    const payload={...scene(),history:[{...event('finding-1',1,'finding-recorded',['wit-finding']),summary:'<script>window.payloadCanary=true</script>',extraPersonalField:'PERSON-123',email:'private@example.test'},...scene().history.slice(1)],extraPayload:'PAYLOAD-CANARY'};
    const {container}=render(createElement(GuidedJourney,{scene:payload}));
    expect(container.querySelector('script')).toBeNull();expect(container.textContent).not.toContain('<script>window.payloadCanary=true</script>');expect(container.textContent).not.toContain('PERSON-123');expect(container.textContent).not.toContain('PAYLOAD-CANARY');expect(container.textContent).not.toContain('private@example.test');
    const future=screen.getAllByRole('button',{name:'Review'}); expect(future).toBeTruthy();
  });

  it('Case B journey has no request step',()=>{
    const bEvent=(id:string,seq:number,type:string,witnessIds:string[])=>({...event(id,seq,type,witnessIds),caseId:'cp-case-b',runId:'run-b'});
    const b={...scene(),case:{caseId:'cp-case-b'},history:[bEvent('b-finding',1,'finding-recorded',['wit-finding']),bEvent('b-review',2,'internal-review-completed',['wit-review']),bEvent('b-request',3,'request-created',['wit-request'])],evidence:[witness('wit-finding','cp-case-b'),witness('wit-review','cp-case-b'),witness('wit-request','cp-case-b')],currentRunId:'run-b'};
    const journey=buildCaseBJourney(b);
    expect(journey.steps.map(step=>step.label)).toEqual(['Finding','Internal review']);
    expect(journey.steps.some(step=>/request/i.test(step.label))).toBe(false);
    expect(buildCaseBJourney({...b,history:[...b.history,{...event('b-synthetic',4,'request-created',[]),label:'Request'}]}).steps.some(step=>/request/i.test(step.label))).toBe(false);
  });

  it('Case C journey labels blocked variant',()=>{
    const c={...scene(),case:{caseId:'cp-case-c'},variant:'blocked',currentRunId:'run-c',history:[{...event('c-finding',1,'finding-recorded',['wit-finding']),caseId:'cp-case-c',runId:'run-c'},{...event('c-review',2,'review-completed',['wit-review']),caseId:'cp-case-c',runId:'run-c'},{...event('c-accept',3,'acceptance-recorded',['wit-request']),caseId:'cp-case-c',runId:'run-c'}],evidence:[witness('wit-finding','cp-case-c'),witness('wit-review','cp-case-c'),witness('wit-request','cp-case-c')]};
    const blocked=buildCaseCJourney(c);
    expect(blocked.variant).toBe('blocked');expect(blocked.steps.map(step=>step.label)).toEqual(['Finding','Review','blocked','retryable']);expect(blocked.steps.map(step=>step.label)).not.toContain('Acceptance');expect(blocked.steps.map(step=>step.label)).not.toContain('Request');
    const baselineHistory=[{...event('c-finding',1,'finding-recorded',['wit-finding']),caseId:'cp-case-c',runId:'run-c'},{...event('c-review',2,'review-completed',['wit-review']),caseId:'cp-case-c',runId:'run-c'},{...event('c-response',4,'partner-response-recorded',['wit-response']),caseId:'cp-case-c',runId:'run-c'},{...event('c-basis',5,'new-basis-created',['wit-basis']),caseId:'cp-case-c',runId:'run-c'}];
    const baselineEvidence=[witness('wit-finding','cp-case-c'),witness('wit-review','cp-case-c'),witness('wit-response','cp-case-c'),witness('wit-basis','cp-case-c')];
    const baseline=buildCaseCJourney({...c,variant:'baseline',history:baselineHistory,evidence:baselineEvidence});expect(baseline.variant).toBe('baseline');expect(baseline.steps.map(step=>step.label)).toEqual(['Finding','Review','Response','New basis']);expect(baseline.steps.map(step=>step.label)).not.toContain('Acceptance');expect(baseline.steps.map(step=>step.label)).not.toContain('blocked');expect(baseline.steps.map(step=>step.label)).not.toContain('retryable');
    const summaryOnly=buildCaseCJourney({...c,variant:undefined,history:[{...event('c-finding',1,'finding-recorded',['wit-finding']),summary:'Blocked retryable processing reset',caseId:'cp-case-c',runId:'run-c'}]});expect(summaryOnly.variant).toBe('baseline');expect(summaryOnly.steps.map(step=>step.label)).not.toContain('blocked');expect(summaryOnly.steps.map(step=>step.label)).not.toContain('retryable');
  });

  it('Case B and C journey preserve older-history omission semantics',async()=>{
    const b=buildCaseBJourney({...scene(),case:{caseId:'cp-case-b'},olderHistoryAvailable:true,currentRunId:'run-b',history:[]});
    expect((await openCaseAOlderHistory(b,null)).error).toBe('Unable to access delivered ARR older history');
    const adapter=vi.fn(async()=>({caseId:'cp-case-b',runId:'run-b',basisId:'basis-a',events:[event('old-b-review',1,'internal-review-completed',['wit-review'])]}));
    expect((await openCaseAOlderHistory({...b,basisId:'basis-a'},adapter)).ok).toBe(true);
    const c=buildCaseCJourney({...scene(),case:{caseId:'cp-case-c'},olderHistoryAvailable:false,currentRunId:'run-c',history:[]});
    expect((await openCaseAOlderHistory(c,adapter)).error).toBe('Older history access is unavailable');
    render(createElement(GuidedJourney,{scene:{...scene(),case:{caseId:'cp-case-c'},olderHistoryAvailable:false,currentRunId:'run-c',history:[]}}));
    expect(screen.getByRole('status').textContent).toBe('Older history unavailable');expect(screen.queryByRole('button',{name:'Open delivered ARR older history'})).toBeNull();cleanup();
    expect((await openCaseAOlderHistory({...b,olderHistoryAvailable:true,basisId:'basis-a'},async()=>({caseId:'cp-case-c',runId:'run-b',basisId:'basis-a',events:[]}))).error).toContain('Out-of-scope');
    const window=buildCaseBJourney({...scene(),case:{caseId:'cp-case-b'},olderHistoryAvailable:true,currentRunId:'run-b',history:[{...event('b-finding',1,'finding-recorded',['wit-finding']),caseId:'cp-case-b',runId:'run-b'}],evidence:[witness('wit-finding','cp-case-b')]});expect(window.steps[0].future).toBe(false);
  });
});
