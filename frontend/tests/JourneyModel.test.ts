import { describe, expect, it } from 'vitest';
import { activityFrom, actorKind, deriveProgress, waitingOn } from '../src/journey/model';
import type { AuditEvent, CaseRecord } from '../src/journey/api';

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
