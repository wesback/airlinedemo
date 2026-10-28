import { useMemo, useState } from 'react';
import { tokens } from '@fluentui/react-components';

export type WorklistItem = {
  caseId: string;
  itemId: string;
  capabilityId: string;
  state: string;
  owner: string;
  ageSeconds: number;
  label?: unknown;
};

export type TechnicalWorkspaceProps = {
  items?: WorklistItem[];
  title?: string;
  onSelect?: (caseId: string, itemId: string) => void;
};

function normalizeStatus(label: unknown): string {
  return typeof label === 'string' && label.trim().length > 0 ? label.trim() : 'Unknown';
}

function formatAge(ageSeconds: number): string {
  if (!Number.isFinite(ageSeconds)) return 'Unknown';
  const total = Math.max(0, Math.round(ageSeconds));
  if (total < 60) return `${total}s`;
  if (total < 3600) return `${Math.round(total / 60)}m`;
  if (total < 86400) return `${Math.round(total / 3600)}h`;
  return `${Math.round(total / 86400)}d`;
}

export default function TechnicalWorkspace({
  items = [],
  title = 'Technical My work',
  onSelect,
}: TechnicalWorkspaceProps) {
  const [caseFilter, setCaseFilter] = useState('all');
  const [capabilityFilter, setCapabilityFilter] = useState('all');
  const [ageSort, setAgeSort] = useState<'server' | 'asc' | 'desc'>('server');

  const caseOptions = useMemo(() => Array.from(new Set(items.map((item) => item.caseId))), [items]);
  const capabilityOptions = useMemo(() => Array.from(new Set(items.map((item) => item.capabilityId))), [items]);

  const visibleItems = useMemo(() => {
    const filtered = items.filter((item) => {
      const matchesCase = caseFilter === 'all' || item.caseId === caseFilter;
      const matchesCapability = capabilityFilter === 'all' || item.capabilityId === capabilityFilter;
      return matchesCase && matchesCapability;
    });

    if (ageSort === 'server') return filtered;
    return [...filtered].sort((left, right) => {
      const delta = left.ageSeconds - right.ageSeconds;
      return ageSort === 'asc' ? delta : -delta;
    });
  }, [ageSort, caseFilter, capabilityFilter, items]);

  return (
    <section aria-labelledby="technical-worklist-heading">
      <h2 id="technical-worklist-heading">{title}</h2>
      <div aria-label="Worklist filters" style={{ display: 'flex', gap: tokens.spacingHorizontalM, alignItems: 'center', marginBottom: tokens.spacingHorizontalM }}>
        <label>
          Case
          <select value={caseFilter} onChange={(event) => setCaseFilter(event.target.value)}>
            <option value="all">All cases</option>
            {caseOptions.map((caseId) => (
              <option key={caseId} value={caseId}>{caseId}</option>
            ))}
          </select>
        </label>
        <label>
          Capability
          <select value={capabilityFilter} onChange={(event) => setCapabilityFilter(event.target.value)}>
            <option value="all">All capabilities</option>
            {capabilityOptions.map((capabilityId) => (
              <option key={capabilityId} value={capabilityId}>{capabilityId}</option>
            ))}
          </select>
        </label>
      </div>
      <table>
        <thead>
          <tr>
            <th>Item</th>
            <th>Aircraft case</th>
            <th>Capability</th>
            <th>
              <button type="button" onClick={() => setAgeSort((current) => current === 'desc' ? 'asc' : current === 'asc' ? 'server' : 'desc')}>
                Age
              </button>
            </th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {visibleItems.map((item) => (
            <tr key={`${item.caseId}:${item.itemId}`} onClick={() => onSelect?.(item.caseId, item.itemId)} style={{ cursor: onSelect ? 'pointer' : undefined }}>
              <td>{item.itemId}</td>
              <td>{item.caseId}</td>
              <td>{item.capabilityId}</td>
              <td>{formatAge(item.ageSeconds)}</td>
              <td>
                <span>{normalizeStatus(item.label)}</span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
