import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import FinanceWorkspace from '../src/workspaces/FinanceWorkspace';

describe('FinanceWorkspace', () => {
  it('AC_010_finance_worklist_contract', () => {
    const onOpenCase = vi.fn();
    const items = [
      { caseId: 'case-002', itemId: 'reserve-2', capabilityId: 'reserve', state: 'Blocked', owner: 'finance-reviewer', ageSeconds: 120, label: 'Reserve case 002' },
      { caseId: 'case-001', itemId: 'reserve-1', capabilityId: 'reserve', state: 'Stale', owner: 'finance-reviewer', ageSeconds: 3600, label: 'Reserve case 001' },
      { caseId: 'case-001', itemId: 'reserve-3', capabilityId: 'legal', state: 'Ready', owner: 'finance-reviewer', ageSeconds: 100, label: 'Legal case 001' },
      { caseId: 'case-003', itemId: 'reserve-4', capabilityId: 'reserve', state: 'Ready', owner: 'technical-reviewer', ageSeconds: 10, label: 'Other reviewer case' },
    ];

    render(<FinanceWorkspace items={items} onOpenCase={onOpenCase} />);

    expect(screen.getByRole('heading', { name: 'My work' })).toBeTruthy();
    expect(screen.getByText('Reserve case 001')).toBeTruthy();
    expect(screen.getByText('Reserve case 002')).toBeTruthy();
    expect(screen.queryByText('Legal case 001')).toBeNull();
    expect(screen.queryByText('Other reviewer case')).toBeNull();

    fireEvent.change(screen.getByLabelText('Case'), { target: { value: 'case-001' } });
    expect(screen.queryByText('Reserve case 002')).toBeNull();

    fireEvent.change(screen.getByLabelText('Capability'), { target: { value: 'reserve' } });
    expect(screen.getByText('Reserve case 001')).toBeTruthy();

    fireEvent.click(screen.getByText('Reserve case 001'));
    expect(onOpenCase).toHaveBeenCalledWith('case-001');
    expect(screen.getAllByText('Reserve case 001')).toHaveLength(1);

    const reserveRows = screen
      .getAllByRole('row')
      .filter((row) => row.textContent?.includes('Reserve case'));
    expect(reserveRows).toHaveLength(1);
    expect(reserveRows[0].textContent).toContain('Reserve case 001');
  });

  it('shows a no-items state for a filtered list with no matching reserve items', () => {
    render(<FinanceWorkspace items={[{ caseId: 'case-001', itemId: 'reserve-1', capabilityId: 'reserve', state: 'Ready', owner: 'finance-reviewer', ageSeconds: 8, label: 'Reserve case 001' }]} />);

    fireEvent.change(screen.getByLabelText('Case'), { target: { value: 'case-002' } });
    expect(screen.getByText('Reserve case 001')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Case'), { target: { value: 'case-009' } });
    expect(screen.getByText('No reserve worklist items in scope.')).toBeTruthy();
  });
});
