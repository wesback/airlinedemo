import { useCallback, useEffect, useRef, useState } from 'react';
import { caseUrl, getJson, type CaseRecord, type Session } from './api';
import { deriveProgress, type CaseProgress } from './model';

export type FleetEntry = { record: CaseRecord; progress: CaseProgress };

export function useFleet(session: Session) {
  const [entries, setEntries] = useState<FleetEntry[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [loadedForSession, setLoadedForSession] = useState<string | null>(null);
  const generation = useRef(0);

  const load = useCallback(async () => {
    const activeGeneration = ++generation.current;
    try {
      const records = await Promise.all(session.caseScopes.map(async (caseId) => {
        try { return await getJson<CaseRecord>(caseUrl(caseId)); } catch { return null; }
      }));
      if (activeGeneration !== generation.current) return;
      setEntries(records.filter((record): record is CaseRecord => record !== null)
        .map((record) => ({ record, progress: deriveProgress(record) })));
      setLoadedForSession(session.id);
      setState('ready');
    } catch {
      if (activeGeneration !== generation.current) return;
      setLoadedForSession(session.id);
      setState('error');
    }
  }, [session.caseScopes, session.id]);

  useEffect(() => {
    void load();
    return () => { generation.current += 1; };
  }, [load]);

  const currentSession = loadedForSession === session.id;
  return { entries: currentSession ? entries : [], state: currentSession ? state : 'loading', reload: load };
}
