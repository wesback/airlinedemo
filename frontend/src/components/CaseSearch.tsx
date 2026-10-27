import { useMemo, useState, type KeyboardEvent } from 'react';

export function CaseSearch({ caseScopes, onNavigate, ariaLabel = 'Search cases' }: {
  caseScopes: string[];
  onNavigate: (path: string) => void;
  ariaLabel?: string;
}) {
  const [query, setQuery] = useState('');

  const matches = useMemo(() => {
    const normalized = query.trim();
    if (!normalized) return [];
    return caseScopes.filter((caseId) => caseId.toLowerCase().startsWith(normalized.toLowerCase()));
  }, [caseScopes, query]);

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key !== 'Enter') return;
    const match = matches[0];
    if (!match) return;
    event.preventDefault();
    onNavigate(`/cases/${encodeURIComponent(match)}/overview`);
  };

  return (
    <div style={{ position: 'relative' }}>
      <input
        aria-label={ariaLabel}
        value={query}
        onChange={(event) => setQuery(event.target.value)}
        onKeyDown={handleKeyDown}
        placeholder="Search cases"
        style={{ width: 220, padding: '8px 12px', borderRadius: 8, border: '1px solid #d0d7de', background: '#fff' }}
      />
      {matches.length > 0 && (
        <ul style={{ position: 'absolute', top: 'calc(100% + 4px)', left: 0, right: 0, margin: 0, padding: 0, background: '#fff', listStyle: 'none', boxShadow: '0 4px 12px rgba(0,0,0,0.12)', borderRadius: 8, zIndex: 10 }}>
          {matches.map((caseId) => (
            <li key={caseId}>
              <button type="button" onClick={() => onNavigate(`/cases/${encodeURIComponent(caseId)}/overview`)} style={{ width: '100%', textAlign: 'left', border: 'none', background: 'transparent', padding: '8px 12px', cursor: 'pointer' }}>
                {caseId}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
