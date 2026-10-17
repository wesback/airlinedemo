import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import InvestigationTrace, { type InvestigationTraceStep } from '../src/InvestigationTrace';

afterEach(() => cleanup());

describe('stored investigation trace steps', () => {
  it('renders every supported step mapping in ascending sequence order', () => {
    const steps: InvestigationTraceStep[] = [
      { sequence: 7, toolName: '<img src=x onerror="sentinel">', documentId: 'SENSITIVE-DOCUMENT-ID', page: 919191, citationCheckOutcome: 'requested', correctionCode: 'None', policyHandover: 'None' },
      { sequence: 5, toolName: 'claim-validation', documentId: 'SENSITIVE-DOCUMENT-ID', page: 919191, citationCheckOutcome: 'None', correctionCode: 'None', policyHandover: 'None' },
      { sequence: 3, toolName: 'citation-check', documentId: 'SENSITIVE-DOCUMENT-ID', page: 919191, citationCheckOutcome: 'quote-not-on-page', correctionCode: 'None', policyHandover: 'None' },
      { sequence: 6, toolName: 'policy-handover', documentId: 'SENSITIVE-DOCUMENT-ID', page: 919191, citationCheckOutcome: 'None', correctionCode: 'None', policyHandover: 'deterministic-policy' },
      { sequence: 2, toolName: 'policy-handover', documentId: 'SENSITIVE-DOCUMENT-ID', page: 919191, citationCheckOutcome: 'None', correctionCode: 'None', policyHandover: 'None' },
      { sequence: 4, toolName: 'claim-validation', documentId: 'SENSITIVE-DOCUMENT-ID', page: 919191, citationCheckOutcome: 'None', correctionCode: 'Downgraded', policyHandover: 'None' },
      { sequence: 1, toolName: 'citation-check', documentId: 'SENSITIVE-DOCUMENT-ID', page: 919191, citationCheckOutcome: 'verified', correctionCode: 'None', policyHandover: 'None' },
    ];
    const { container } = render(<InvestigationTrace view={{ status: 'loaded', trace: { steps } }} />);
    const list = screen.getByRole('list', { name: 'Investigation trace steps' });

    expect(Array.from(list.querySelectorAll('li')).map((item) => item.textContent)).toEqual([
      'Citation check — Outcome: verified',
      'Policy handover — Outcome: None',
      'Citation check — Outcome: quote-not-on-page',
      'Claim validation — Correction: Downgraded',
      'Claim validation — Correction: None',
      'Policy handover — Outcome: Deterministic policy took over',
      'Tool call — <img src=x onerror="sentinel"> — Outcome: requested',
    ]);
    expect(within(list).getByText('Tool call')).toBeTruthy();
    expect(within(list).getAllByText('Citation check')).toHaveLength(2);
    expect(within(list).getAllByText('Claim validation')).toHaveLength(2);
    expect(within(list).getAllByText('Policy handover')).toHaveLength(2);
    expect(container.textContent).not.toContain('SENSITIVE-DOCUMENT-ID');
    expect(container.textContent).not.toContain('919191');
    expect(container.querySelector('img')).toBeNull();
    expect(within(list).getByText('<img src=x onerror="sentinel">', { exact: true })).toBeTruthy();
  });
});
