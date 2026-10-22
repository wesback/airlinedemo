import { cleanup, render, screen } from '@testing-library/react';
import { createElement } from 'react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import GuidedJourney, { buildCaseAJourney, openCaseAOlderHistory } from '../src/hangar/guidedJourney';

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
    expect(result.history?.steps[0].sourceEventId).toBe('old-finding');expect(result.history?.steps[0].runId).toBe('run-a');expect(result.history?.steps[0].witnessId).toBe('old-witness');expect(result.history?.steps[0].evidence).toEqual([{witnessId:'old-witness',documentId:'case-a/llp.txt',documentVersion:2,page:4,basisId:'basis-a',basisVersion:2,displayLabel:'Evidence old-witness'}]);
    const {container}=render(createElement(GuidedJourney,{scene:scene(),adapter})); const control=screen.getByRole('button',{name:'Open delivered ARR older history'}); control.focus(); await user.keyboard('{Enter}'); await screen.findByRole('status'); expect(control).toBeTruthy(); expect(container.querySelector('[aria-label="Open delivered ARR older history"]')).toBeTruthy();expect(adapter).toHaveBeenCalled();
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
});
