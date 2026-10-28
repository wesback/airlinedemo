import { useMemo, useState } from 'react';

export type FinanceWorklistItem = {
  caseId: string;
  itemId: string;
  capabilityId: string;
  state: string;
  owner: string;
  ageSeconds: number;
  label: string;
};

type Props = {
  items?: FinanceWorklistItem[];
  onOpenCase?: (caseId: string) => void;
};

export default function FinanceWorkspace({ items = [], onOpenCase }: Props) {
  const [caseFilter, setCaseFilter] = useState('all');
  const [capabilityFilter, setCapabilityFilter] = useState('all');

  const reserveItems = useMemo(
    () => items.filter((item) => item.owner === 'finance-reviewer' && item.capabilityId === 'reserve'),
    [items],
  );
  const caseOptions = useMemo(() => Array.from(new Set(reserveItems.map((item) => item.caseId))).sort(), [reserveItems]);
  const capabilityOptions = useMemo(() => Array.from(new Set(reserveItems.map((item) => item.capabilityId))).sort(), [reserveItems]);
  const visibleItems = useMemo(() => reserveItems.filter((item) => {
    const matchesCase = caseFilter === 'all' || item.caseId === caseFilter;
    const matchesCapability = capabilityFilter === 'all' || item.capabilityId === capabilityFilter;
    return matchesCase && matchesCapability;
  }), [caseFilter, capabilityFilter, reserveItems]);

  return (
    <section aria-labelledby="finance-worklist-heading">
      <h2 id="finance-worklist-heading">My work</h2>
      <div>
        <label htmlFor="finance-case-filter">Case</label>
        <select id="finance-case-filter" value={caseFilter} onChange={(event) => setCaseFilter(event.target.value)}>
          <option value="all">All cases</option>
          {caseOptions.map((caseId) => <option key={caseId} value={caseId}>{caseId}</option>)}
        </select>
      </div>
      <div>
        <label htmlFor="finance-capability-filter">Capability</label>
        <select id="finance-capability-filter" value={capabilityFilter} onChange={(event) => setCapabilityFilter(event.target.value)}>
          <option value="all">All capabilities</option>
          {capabilityOptions.map((capabilityId) => <option key={capabilityId} value={capabilityId}>{capabilityId}</option>)}
        </select>
      </div>
      <table>
        <thead>
          <tr>
            <th>Item</th>
            <th>Case</th>
            <th>Capability</th>
            <th>Age</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {visibleItems.length === 0 && (
            <tr>
              <td colSpan={5}>No reserve worklist items in scope.</td>
            </tr>
          )}
          {visibleItems.map((item) => (
            <tr key={`${item.caseId}:${item.itemId}`} onClick={() => onOpenCase?.(item.caseId)} style={{ cursor: 'pointer' }}>
              <td>{item.label}</td>
              <td>{item.caseId}</td>
              <td>{item.capabilityId}</td>
              <td>{item.ageSeconds}s</td>
              <td>{item.state}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
