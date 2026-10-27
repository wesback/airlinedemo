import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { CaseSearch } from '../src/components/CaseSearch';

describe('CaseSearch', () => {
  it('AC_012_scoped_case_search_contract', () => {
    const onNavigate = vi.fn();
    render(<CaseSearch caseScopes={['cp-case-a', 'cp-case-b']} onNavigate={onNavigate} />);

    const input = screen.getByLabelText('Search cases');
    fireEvent.change(input, { target: { value: 'CP-CASE-B' } });
    expect(screen.getByRole('button', { name: 'cp-case-b' })).toBeTruthy();
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' });
    expect(onNavigate).toHaveBeenCalledWith('/cases/cp-case-b/overview');

    fireEvent.change(input, { target: { value: 'case-001' } });
    expect(screen.queryByRole('button', { name: 'case-001' })).toBeNull();
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' });
    expect(onNavigate).toHaveBeenCalledTimes(1);
  });
});
