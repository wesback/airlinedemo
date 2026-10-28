import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import ExecutiveCaseSummary from '../src/workspaces/ExecutiveCaseSummary';

describe('ExecutiveCaseSummary', () => {
  it('AC_017_executive_read_only_contract', () => {
    render(<ExecutiveCaseSummary
      caseId="case-001"
      summary="Executive read-only view"
      rows={[
        { role: 'technical-reviewer', count: 2 },
        { role: 'finance-reviewer', count: 1 },
      ]}
      latestActivity={{ actor: 'Ada Reviewer', label: 'Human decision', outcome: 'technical-review.decided' }}
      showDecisionControls={false}
    />);

    expect(screen.getByText('Case case-001')).toBeTruthy();
    expect(screen.getByText('Executive read-only view')).toBeTruthy();
    expect(screen.getByText('technical-reviewer')).toBeTruthy();
    expect(screen.getByText('2')).toBeTruthy();
    expect(screen.getByText('Read-only summary')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /decision/i })).toBeNull();
    expect(screen.queryByRole('form')).toBeNull();
  });
});
