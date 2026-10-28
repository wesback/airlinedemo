import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import TechnicalWorkspace, { type WorklistItem } from '../src/workspaces/TechnicalWorkspace';

const items: WorklistItem[] = [
  { caseId: 'case-b', itemId: 'finding-2', capabilityId: 'records-investigation', state: 'open', owner: 'Ada', ageSeconds: 7200, label: 'AI agent' },
  { caseId: 'case-a', itemId: 'finding-1', capabilityId: 'technical-review', state: 'open', owner: 'Ada', ageSeconds: 90, label: 'Human decision' },
  { caseId: 'case-a', itemId: 'finding-3', capabilityId: 'records-investigation', state: 'open', owner: 'Ada', ageSeconds: 3600 },
  { caseId: 'case-b', itemId: 'finding-4', capabilityId: 'technical-review', state: 'open', owner: 'Ada', ageSeconds: 10, label: 'Totally new label' },
];

function rows() {
  return screen.getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell').map((cell) => cell.textContent));
}

afterEach(cleanup);

describe('technical worklist', () => {
  it('AC_008_technical_worklist_contract', () => {
    const onSelect = vi.fn();
    render(<TechnicalWorkspace items={items} onSelect={onSelect} />);

    expect(screen.getAllByRole('columnheader').map((header) => header.textContent)).toEqual(['Item', 'Aircraft case', 'Capability', 'Age', 'Status']);
    // Server order is kept, the backend label is the badge text, and a missing or unrecognized label is exactly "Unknown".
    expect(rows()).toEqual([
      ['finding-2', 'case-b', 'records-investigation', '2h', 'AI agent'],
      ['finding-1', 'case-a', 'technical-review', '2m', 'Human decision'],
      ['finding-3', 'case-a', 'records-investigation', '1h', 'Unknown'],
      ['finding-4', 'case-b', 'technical-review', '10s', 'Unknown'],
    ]);

    // Case filter: every row outside the case is excluded.
    fireEvent.change(screen.getByLabelText('Worklist case'), { target: { value: 'case-a' } });
    expect(rows().map((row) => row[0])).toEqual(['finding-1', 'finding-3']);
    // Capability filter narrows further.
    fireEvent.change(screen.getByLabelText('Worklist capability'), { target: { value: 'technical-review' } });
    expect(rows().map((row) => row[0])).toEqual(['finding-1']);
    fireEvent.change(screen.getByLabelText('Worklist case'), { target: { value: 'all' } });
    fireEvent.change(screen.getByLabelText('Worklist capability'), { target: { value: 'all' } });

    // Age sorting: first click oldest first, second youngest first, third back to server order.
    const age = screen.getByRole('button', { name: 'Age' });
    fireEvent.click(age);
    expect(rows().map((row) => row[0])).toEqual(['finding-2', 'finding-3', 'finding-1', 'finding-4']);
    fireEvent.click(age);
    expect(rows().map((row) => row[0])).toEqual(['finding-4', 'finding-1', 'finding-3', 'finding-2']);
    fireEvent.click(age);
    expect(rows().map((row) => row[0])).toEqual(['finding-2', 'finding-1', 'finding-3', 'finding-4']);

    // Selecting a row reports that row's case and item.
    fireEvent.click(screen.getByText('finding-1'));
    expect(onSelect).toHaveBeenCalledWith('case-a', 'finding-1');
  });
});
