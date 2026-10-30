import { useCallback, useEffect, useState } from 'react';
import { caseUrl, getJson, type CaseRecord, type Session } from './api';
import { deriveProgress, type CaseProgress } from './model';

export type FleetEntry = { record: CaseRecord; progress: CaseProgress };

export function useFleet(session: Session) {
  const [entries, setEntries] = useState<FleetEntry[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');

  const load = useCallback(async () => {
    try {
      const records = await Promise.all(session.caseScopes.map(async (caseId) => {
        try { return await getJson<CaseRecord>(caseUrl(caseId)); } catch { return null; }
      }));
      setEntries(records.filter((record): record is CaseRecord => record !== null)
        .map((record) => ({ record, progress: deriveProgress(record) })));
      setState('ready');
    } catch {
      setState('error');
    }
  }, [session.caseScopes]);

  useEffect(() => { void load(); }, [load]);

  return { entries, state, reload: load };
}
