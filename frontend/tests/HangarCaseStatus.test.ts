import { describe, expect, it } from 'vitest';
import { describeHangarCaseStatus } from '../src/hangar/HangarMode';

const envelope = (status: Record<string, string | null>) => ({ case: { caseId: 'cp-case-a', status } });

describe('describeHangarCaseStatus', () => {
  it.each([
    [{ legal: 'signed-off' }, false, { tone: 'done', text: 'Ready for release' }],
    [{ legal: 'signed-off' }, true, { tone: 'done', text: 'Released to the next lessee' }],
    [{ finance: 'approve' }, false, { tone: 'human', text: 'Waiting for legal review' }],
    [{ acceptance: 'reject' }, false, { tone: 'human', text: 'Returned for rework' }],
    [{}, false, { tone: 'ai', text: 'AI agent preparing evidence' }],
  ])('maps %j (released: %s) to the hangar pill', (status, released, expected) => {
    expect(describeHangarCaseStatus(envelope(status), released)).toEqual(expected);
  });
});
