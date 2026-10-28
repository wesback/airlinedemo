import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ReserveWorksheet from '../src/workspaces/ReserveWorksheet';

describe('ReserveWorksheet', () => {
  afterEach(() => cleanup());

  it('AC_010_reserve_worksheet_contract', () => {
    const onDecision = vi.fn();
    const { rerender } = render(
      <ReserveWorksheet
        draft={{
          status: 'Blocked',
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
    expect(screen.getByRole('status').textContent).toBe('Blocked');
    expect(screen.getByText('Input')).toBeTruthy();
    expect(screen.getByText('Value')).toBeTruthy();
    expect(screen.getByText('Source')).toBeTruthy();
    expect(screen.getByText('Freshness')).toBeTruthy();
    expect(screen.getByText('Status')).toBeTruthy();
    expect(screen.getByText('Pax frequency')).toBeTruthy();
    expect(screen.getByText('Rate')).toBeTruthy();
    expect(screen.getByText('Amount')).toBeTruthy();
    expect(screen.getByText('source-a')).toBeTruthy();
    expect(screen.getByText('source-b')).toBeTruthy();
    expect(screen.getByText('source-c')).toBeTruthy();
    expect(screen.getByText('2 days ago')).toBeTruthy();
    expect(screen.getByText('6 hours ago')).toBeTruthy();
    expect(screen.getByText('fresh')).toBeTruthy();
    expect(screen.getAllByText('Blocked')).toHaveLength(2);
    expect(screen.getAllByText('Stale')).toHaveLength(1);
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

  it('finance_reserve_decision_contract', async () => {
    const onDecisionReasonChange = vi.fn();
    const onDecision = vi.fn();
    const { rerender } = render(
      <ReserveWorksheet
        draft={{
          status: 'Ready',
          revision: 11,
          allowedDecisions: [{ id: 'approve', label: 'Approve reserve' }, { id: 'return', label: 'Return reserve' }],
        }}
        decisionReason=""
        onDecisionReasonChange={onDecisionReasonChange}
        onDecision={onDecision}
      />,
    );

    const textarea = screen.getByLabelText('Reason for decision', { selector: 'textarea' });
    fireEvent.change(textarea, { target: { value: 'Ready to review' } });
    expect(onDecisionReasonChange).toHaveBeenCalledWith('Ready to review');

    fireEvent.click(screen.getByRole('button', { name: 'Return reserve' }));
    expect(onDecision).toHaveBeenCalledWith('return');

    rerender(
      <ReserveWorksheet
        draft={{
          status: 'Ready',
          revision: 11,
          allowedDecisions: [{ id: 'approve', label: 'Approve reserve' }],
        }}
        decisionReason=""
        onDecisionReasonChange={vi.fn()}
        onDecision={onDecision}
      />,
    );

    expect(screen.getByRole('button', { name: 'Approve reserve' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Return reserve' })).toBeNull();
  });

  it('reserve_text_and_status_remain_inert', () => {
    const { rerender } = render(
      <ReserveWorksheet
        draft={{
          status: 'Blocked',
          lines: [
            { input: '<b>x</b>', value: '<script>evil()</script>', source: 'https://example.test/ok', freshness: 'fresh', status: 'stale' },
          ],
          allowedDecisions: [{ id: 'approve', label: 'Approve reserve' }],
        }}
      />,
    );

    expect(screen.getAllByText('<b>x</b>').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Stale').length).toBeGreaterThan(0);
    expect(screen.getByText('https://example.test/ok')).toBeTruthy();
    expect(document.querySelector('script')).toBeNull();
    expect(document.querySelector('b')).toBeNull();
    expect(document.querySelector('[href]')).toBeNull();
    expect(document.querySelector('[action]')).toBeNull();
    expect(document.querySelector('[style]')).toBeNull();
    expect(document.querySelector('[src]')).toBeNull();
  });
});
