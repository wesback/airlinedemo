import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import LegalWorkspace, { type WorklistItem } from '../src/workspaces/LegalWorkspace';

const items: WorklistItem[] = [
  { caseId: 'case-001', itemId: 'OBL-1', capabilityId: 'contract-obligation', state: 'open', owner: 'legal', ageSeconds: 300, label: 'Human decision' },
  { caseId: 'case-001', itemId: 'OBL-2', capabilityId: 'contract-training', state: 'needs_review', owner: 'legal', ageSeconds: 900, label: 'Human decision' },
  { caseId: 'case-002', itemId: 'OBL-9', capabilityId: 'contract-obligation', state: 'open', owner: 'legal', ageSeconds: 120, label: 'Finance reserve' },
];

describe('legal worklist', () => {
  it('AC_011_legal_worklist_contract', () => {
    const onSelect = vi.fn();
    render(<LegalWorkspace items={items} onSelect={onSelect} />);

    expect(screen.getByText('OBL-1')).toBeTruthy();
    expect(screen.getByText('OBL-2')).toBeTruthy();
    expect(screen.getByText('OBL-9')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Case'), { target: { value: 'case-001' } });
    expect(screen.getByText('OBL-1')).toBeTruthy();
    expect(screen.getByText('OBL-2')).toBeTruthy();
    expect(screen.queryByText('OBL-9')).toBeNull();

    fireEvent.change(screen.getByLabelText('Capability'), { target: { value: 'contract-training' } });
    expect(screen.getByText('OBL-2')).toBeTruthy();
    expect(screen.queryByText('OBL-1')).toBeNull();

    fireEvent.click(screen.getByText('OBL-2'));
    expect(onSelect).toHaveBeenCalledWith('case-001', 'OBL-2');
  });
});
