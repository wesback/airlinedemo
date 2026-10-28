import { useMemo, useState, type FormEvent } from 'react';

export type CaseSearchProps = {
  caseScopes?: string[];
  onNavigate?: (path: string) => void;
};

export default function CaseSearch({ caseScopes = [], onNavigate }: CaseSearchProps) {
  const [query, setQuery] = useState('');

  const matches = useMemo(() => {
    const term = query.trim().toLowerCase();
    if (!term) return [];
    return caseScopes.filter((caseId) => caseId.toLowerCase().startsWith(term));
  }, [caseScopes, query]);

  function submitMatch(nextQuery = query) {
    const term = nextQuery.trim();
    if (!term) return;
    const firstMatch = caseScopes.find((caseId) => caseId.toLowerCase() === term.toLowerCase())
      ?? caseScopes.find((caseId) => caseId.toLowerCase().startsWith(term.toLowerCase()));
    if (!firstMatch) return;

    const path = `/cases/${firstMatch}/overview`;
    onNavigate?.(path);
    if (typeof window !== 'undefined') {
      window.history.replaceState({}, '', path);
    }
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    submitMatch();
  }

  return (
    <form aria-label="Case search form" onSubmit={handleSubmit}>
      <label htmlFor="case-search-input">Case search</label>
      <input
        id="case-search-input"
        type="search"
        aria-label="Case search"
        value={query}
        onChange={(event) => setQuery(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Enter') {
            event.preventDefault();
            submitMatch(event.currentTarget.value);
          }
        }}
        placeholder="Find case"
      />
      {matches.length > 0 && (
        <ul aria-label="Matching cases" role="listbox">
          {matches.map((caseId) => (
            <li key={caseId} role="option">
              <button type="button" onClick={() => submitMatch(caseId)}>
                {caseId}
              </button>
            </li>
          ))}
        </ul>
      )}
    </form>
  );
}
