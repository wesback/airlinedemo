import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ObligationWorkspace from '../src/workspaces/ObligationWorkspace';

describe('obligation workspace', () => {
  afterEach(() => {
    cleanup();
  });

  it('AC_011_obligation_notice_text_contract', () => {
    const onDecisionSubmit = vi.fn().mockResolvedValue(true);
    const obligation = {
      id: 'OBL-1',
      title: 'Obligation OBL-1',
      summary: 'Case case-001: OBL-1',
      status: 'Awaiting legal review',
      basisVersion: 2,
      expectedRevision: 7,
      rows: [{ label: 'Case', value: 'case-001' }, { label: 'Obligation', value: 'OBL-1' }],
      clauseReferences: [{ id: 'clause-1', text: 'Clause: contracts/con-clause-return-notice-001 § 1' }],
      noticeDraft: '<b>x</b>',
      banner: 'Simulated — no notice is sent.',
      allowedDecisions: [{ id: 'signed-off', label: 'Signed off' }, { id: 'rejected', label: 'Rejected' }],
    };
    const { container } = render(
      <ObligationWorkspace
        worklist={[{ caseId: 'case-001', itemId: 'OBL-1', capabilityId: 'contract-obligation', state: 'open', owner: 'legal', ageSeconds: 300, label: 'Human decision' }]}
        caseId="case-001"
        itemId="OBL-1"
        obligation={obligation}
        decisionReason=""
        onDecisionReasonChange={() => {}}
        onDecisionSubmit={onDecisionSubmit}
      />,
    );

    expect(screen.getByText('Simulated — no notice is sent.')).toBeTruthy();
    expect(screen.getByText('<b>x</b>')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /send notice/i })).toBeNull();
    expect(container.querySelector('b, img, a, [href], [src], [role="link"]')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Signed off' }));
    expect(onDecisionSubmit).toHaveBeenCalledWith('signed-off', '');
  });

  it('legal_obligation_decision_contract', async () => {
    const onDecisionSubmit = vi.fn().mockResolvedValue(true);
    render(
      <ObligationWorkspace
        worklist={[{ caseId: 'case-001', itemId: 'OBL-1', capabilityId: 'contract-obligation', state: 'open', owner: 'legal', ageSeconds: 300, label: 'Human decision' }]}
        caseId="case-001"
        itemId="OBL-1"
        obligation={{
          id: 'OBL-1',
          title: 'Obligation OBL-1',
          summary: 'Case case-001: OBL-1',
          status: 'Awaiting legal review',
          basisVersion: 2,
          expectedRevision: 7,
          rows: [{ label: 'Case', value: 'case-001' }, { label: 'Obligation', value: 'OBL-1' }],
          clauseReferences: [{ id: 'clause-1', text: 'Clause: contracts/con-clause-return-notice-001 § 1' }],
          noticeDraft: '<b>x</b>',
          banner: 'Simulated — no notice is sent.',
          allowedDecisions: [{ id: 'signed-off', label: 'Signed off' }, { id: 'rejected', label: 'Rejected' }],
        }}
        decisionReason="reason text"
        onDecisionReasonChange={() => {}}
        onDecisionSubmit={onDecisionSubmit}
      />,
    );

    fireEvent.click(screen.getAllByRole('button', { name: 'Signed off' })[0]);
    await waitFor(() => expect(onDecisionSubmit).toHaveBeenCalledWith('signed-off', 'reason text'));
  });

  it('AI_trace_and_obligation_text_remain_inert', () => {
    const hostile = ['<b>x</b>', '<img src=x onerror=alert(1)>', 'javascript:alert(1)', 'navigation: /cases/evil'];
    const { container } = render(
      <ObligationWorkspace
        worklist={[{ caseId: 'case-001', itemId: 'OBL-1', capabilityId: 'contract-obligation', state: 'open', owner: 'legal', ageSeconds: 300, label: 'Human decision' }]}
        caseId="case-001"
        itemId="OBL-1"
        obligation={{
          id: 'OBL-1',
          title: 'Obligation OBL-1',
          summary: 'Case case-001: OBL-1',
          status: 'Awaiting legal review',
          clauseReferences: [{ id: 'c1', text: hostile[0] }],
          noticeDraft: hostile[1],
          banner: hostile[2],
          allowedDecisions: [{ id: 'rejected', label: hostile[3] }],
        }}
        decisionReason=""
        onDecisionReasonChange={() => {}}
      />,
    );

    for (const text of hostile) {
      expect(screen.getAllByText(text).length).toBeGreaterThan(0);
    }
    expect(container.querySelector('b, img, a, [href], [src], [role="link"]')).toBeNull();
  });
});
