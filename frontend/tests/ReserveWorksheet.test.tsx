import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import ReserveWorksheet from '../src/workspaces/ReserveWorksheet';

describe('ReserveWorksheet', () => {
  it('renders draft text, statuses, and decision actions from the server payload', () => {
    const onDecision = vi.fn();
    const { rerender } = render(
      <ReserveWorksheet
        draft={{
          status: 'Ready',
          revision: 11,
          payableTotal: 125.5,
          blockers: [{ code: 'missing-document', detail: 'Missing rate support' }],
          discrepancies: ['Usage report is stale'],
          lines: [
            { input: 'Pax frequency', value: null, source: 'source-a', freshness: '2 days ago', status: 'blocked' },
            { input: 'Rate', value: 12.3, source: 'source-b', freshness: '6 hours ago', status: 'stale' },
            { input: 'Amount', value: 125.5, source: 'source-c', freshness: 'fresh', status: 'ready' },
          ],
          allowedDecisions: [{ id: 'approve', label: 'Approve reserve' }, { id: 'return', label: 'Return reserve' }],
        }}
        decisionReason="Ready to review"
        onDecisionReasonChange={vi.fn()}
        onDecision={onDecision}
      />,
    );

    expect(screen.getByText('Draft only — no invoice is issued.')).toBeTruthy();
    expect(screen.getByText('Blocked')).toBeTruthy();
    expect(screen.getByText('Stale')).toBeTruthy();
    expect(screen.getByText('Not available')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Approve reserve' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Approve reserve' }));
    expect(onDecision).toHaveBeenCalledWith('approve');

    rerender(
      <ReserveWorksheet
        draft={{
          status: 'Ready',
          revision: 12,
          lines: [{ input: '<b>x</b>', value: 'literal', source: 'source-a', freshness: 'fresh', status: 'ready' }],
          allowedDecisions: [{ id: 'approve', label: 'Approve reserve' }],
        }}
        decisionReason=""
        onDecisionReasonChange={vi.fn()}
        onDecision={onDecision}
      />,
    );

    expect(screen.getByText('<b>x</b>')).toBeTruthy();
    expect(document.querySelector('b')).toBeNull();
    expect(document.querySelector('[style]')).toBeNull();
  });
});
