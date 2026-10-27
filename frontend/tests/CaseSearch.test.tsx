import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import CaseSearch from '../src/components/CaseSearch';

describe('CaseSearch contract', () => {
  it('AC_012_scoped_case_search_contract', () => {
    const onNavigate = vi.fn();
    const fetchSpy = vi.spyOn(window, 'fetch');
    const caseScopes = ['cp-case-a', 'cp-case-b'];

    render(<CaseSearch caseScopes={caseScopes} onNavigate={onNavigate} />);

    const input = screen.getByRole('searchbox', { name: 'Case search' });
    fireEvent.change(input, { target: { value: 'CP-CASE-B' } });
    expect(screen.getByRole('option', { name: 'cp-case-b' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'cp-case-a' })).toBeNull();

    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter', charCode: 13 });
    expect(onNavigate).toHaveBeenCalledWith('/cases/cp-case-b/overview');
    expect(fetchSpy).not.toHaveBeenCalled();

    fireEvent.change(input, { target: { value: 'case-001' } });
    expect(screen.queryByRole('option')).toBeNull();
    expect(onNavigate).toHaveBeenCalledTimes(1);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
