import { describe, expect, it } from 'vitest';
import { activityFrom, actorKind, deriveProgress, waitingOn } from '../src/journey/model';
import type { AuditEvent, CaseHistory, CaseRecord } from '../src/journey/api';
import { aiBriefFor, aiChipFor } from '../src/journey/aiBriefs';

const at = '2026-10-30T10:00:00Z';
const event = (sequence: number, actor: string, action: string, detail = ''): AuditEvent => ({ sequence, revision: sequence, actor, action, detail, at });
const record = (audit: AuditEvent[], basis = true): CaseRecord => ({ caseId: 'cp-case-a', revision: audit.length, basis: basis ? [{ version: 1 }] : [], audit });

const investigated = [
  event(1, 'presenter', 'journey', 'Package received.'),
  event(2, 'records-agent', 'journey', 'Cross-checked records.'),
  event(3, 'assessment-service', 'create-work-item', 'review-cp-case-a'),
];

describe('deriveProgress', () => {
  it('places a freshly arrived aircraft at records intake', () => {
    const progress = deriveProgress(record([], false));
    expect(progress.stepIndex).toBe(1);
    expect(progress.states.arrival).toBe('done');
    expect(progress.states.intake).toBe('current');
    expect(waitingOn(progress)).toEqual({ actor: 'rules', label: 'Waiting for the records package' });
  });

  it('waits for the technical reviewer once the AI agent has raised findings', () => {
    const progress = deriveProgress(record(investigated));
    expect(progress.stepIndex).toBe(3);
    expect(progress.states.investigation).toBe('done');
    expect(waitingOn(progress)).toEqual({ actor: 'human', label: 'Waiting for technical reviewer' });
  });

  it('moves through finance to legal when both decisions are positive', () => {
    const progress = deriveProgress(record([
      ...investigated,
      event(4, 'reviewer', 'technical-review.decided', '{"Decision":"accept","Reason":"ok"}'),
      event(5, 'fin-1', 'reserve.decided', '{"decision":"approve","reason":"ok"}'),
    ]));
    expect(progress.stepIndex).toBe(5);
    expect(progress.states.finance).toBe('done');
    expect(progress.states.legal).toBe('current');
  });

  it('is ready for release after legal sign-off and released once the transition lead releases it', () => {
    const signedOff = [
      ...investigated,
      event(4, 'reviewer', 'technical-review.decided', '{"Decision":"accept","Reason":"ok"}'),
      event(5, 'fin-1', 'reserve.decided', '{"decision":"approve","reason":"ok"}'),
      event(6, 'legal-1', 'obligation.legal-decided', '{"Decision":"signed-off","Reason":"ok"}'),
    ];
    const ready = deriveProgress(record(signedOff));
    expect(ready.stepIndex).toBe(6);
    expect(ready.states.release).toBe('current');
    expect(ready.released).toBe(false);
    expect(waitingOn(ready)).toEqual({ actor: 'human', label: 'Ready for release' });

    const released = deriveProgress(record([...signedOff, event(7, 'presenter', 'aircraft.released', '{"decision":"released","note":"Handed over."}')]));
    expect(released.states.release).toBe('done');
    expect(released.released).toBe(true);
    expect(waitingOn(released)).toEqual({ actor: 'human', label: 'Released to the next lessee' });
    expect(activityFrom(record([event(7, 'presenter', 'aircraft.released', '{"decision":"released","note":"Handed over."}')]).audit)[0])
      .toMatchObject({ actor: 'human', who: 'Transition lead', what: 'Aircraft released to the next lessee', reason: 'Handed over.' });
  });

  it('marks a rejected technical review as returned and blocked', () => {
    const progress = deriveProgress(record([...investigated, event(4, 'reviewer', 'technical-review.decided', '{"Decision":"reject","Reason":"gap"}')]));
    expect(progress.states.technical).toBe('returned');
    expect(progress.blocked).toBe(true);
    expect(waitingOn(progress).label).toBe('Returned at technical review');
  });
});

describe('activity', () => {
  it('labels AI, rules and human actors and keeps decision reasons', () => {
    expect(actorKind('records-agent', 'journey')).toBe('ai');
    expect(actorKind('assessment-service', 'create-work-item')).toBe('rules');
    expect(actorKind('presenter', 'journey')).toBe('rules');
    expect(actorKind('fin-1', 'reserve.decided')).toBe('human');

    const items = activityFrom([
      ...investigated,
      event(4, 'reviewer', 'technical-review.decided', '{"Decision":"accept","Reason":"Verified."}'),
      event(5, 'technical-reviewer', 'journey', 'Technical reviewer accepted the AI findings.'),
    ]);
    expect(items).toHaveLength(4);
    expect(items[0]).toMatchObject({ actor: 'human', who: 'Technical reviewer', what: 'Technical review accepted the findings', reason: 'Verified.' });
    expect(items[3]).toMatchObject({ actor: 'rules', who: 'Intake service' });
  });
});

describe('AI briefing', () => {
  const aiHistory = (summary: string): CaseHistory => ({ events: [{ eventId: 'e1', sequence: 1, eventType: 'findings-raised', actorRole: 'records-agent', summary, capabilityLabel: 'AI agent',
    witnesses: [{ witnessId: 'w1', documentId: 'x/a.txt', documentVersion: 1, page: 1 }, { witnessId: 'w2', documentId: 'x/b.txt', documentVersion: 1, page: 1 }] }] });

  it('stays hidden until the records agent has reported', () => {
    expect(aiBriefFor('cp-case-a', null)).toBeNull();
    expect(aiBriefFor('cp-case-a', { events: [{ ...aiHistory('x').events[0], capabilityLabel: 'Rules-based' }] })).toBeNull();
  });

  it('uses the case-pack briefing for known aircraft and summarises its findings for the board', () => {
    expect(aiBriefFor('cp-case-b', aiHistory('raised'))?.findings.map((finding) => finding.severity)).toEqual(['blocker', 'attention']);
    expect(aiChipFor('cp-case-b')).toEqual({ label: 'AI: 2 findings, 1 blocking', severity: 'blocker' });
    expect(aiChipFor('cp-case-c')).toEqual({ label: 'AI: 1 finding', severity: 'attention' });
    expect(aiChipFor('case-001')).toBeNull();
  });

  it('falls back to the recorded agent summary for other cases', () => {
    const brief = aiBriefFor('case-001', aiHistory('Agent found a gap.'))!;
    expect(brief.headline).toBe('Agent found a gap.');
    expect(brief.documentsRead).toBe(2);
    expect(brief.findings).toEqual([]);
  });

  it('turns live finding-raised events into cited findings', () => {
    const witness = (documentId: string, page: number) => ({ witnessId: `w-${documentId}`, documentId, documentVersion: 1, page });
    const brief = aiBriefFor('case-001', { events: [
      { eventId: 'p-agent', sequence: 2, eventType: 'evidence-reviewed', actorRole: 'records-agent', summary: 'Read 3 documents.', capabilityLabel: 'AI agent',
        witnesses: [witness('a.pdf', 1), witness('b.pdf', 1), witness('c.pdf', 1)] },
      { eventId: 'p-finding-1', sequence: 3, eventType: 'finding-raised', actorRole: 'records-agent', summary: 'No installation record for ESN-DEMO1-212 is included.', capabilityLabel: 'AI agent',
        witnesses: [witness('b.pdf', 1)] },
    ] })!;
    expect(brief.headline).toBe('The agent raised 1 finding from 3 documents, each quoted from its source page.');
    expect(brief.findings).toEqual([{ id: 'p-finding-1', severity: 'attention', title: 'No installation record for ESN-DEMO1-212 is included.',
      detail: 'Quoted word for word from the cited page. The citation check found the quote on that page before the finding was kept.',
      sources: [{ document: 'b.pdf', note: 'page 1' }] }]);
    expect(brief.checks.map((check) => check.result)).toEqual(['pass']);
  });

  it('explains an escalated live run instead of claiming findings', () => {
    const brief = aiBriefFor('case-002', { events: [
      { eventId: 'p-agent', sequence: 2, eventType: 'investigation-escalated', actorRole: 'records-agent', summary: 'The AI agent could not reach the model service.', capabilityLabel: 'AI agent',
        witnesses: [{ witnessId: 'w1', documentId: 'a.pdf', documentVersion: 1, page: 1 }] },
    ] })!;
    expect(brief.headline).toBe('The AI agent could not reach the model service.');
    expect(brief.findings).toEqual([]);
    expect(brief.whyHuman).toBe('The agent produced no usable findings, so a technical reviewer checks the records package directly before the case can move on.');
  });
});
