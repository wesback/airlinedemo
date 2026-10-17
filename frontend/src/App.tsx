import { useEffect, useRef, useState } from 'react';
import CapabilityDashboard, { type DashboardData } from './CapabilityDashboard';
import SignIn, { type SignedInSession } from './components/SignIn';
<<<<<<< HEAD
import ReadinessSummary, { type ReadinessSummaryData } from './ReadinessSummary';
=======
import InvestigationTrace, { type InvestigationTraceRecord, type InvestigationTraceView } from './InvestigationTrace';
>>>>>>> origin/main

type CaseProjection = { revision: number; basis: unknown[] };
type TechnicalReviewProjection = { basisVersion: number; decisions: { decision: string; current: boolean }[] };
type Projections = { caseState: CaseProjection; dashboard: DashboardData; review: TechnicalReviewProjection; readiness: ReadinessSummaryData };

class RequestError extends Error { constructor(readonly status: number) { super(`Request failed with status ${status}`); } }

async function getProjection<T>(url: string): Promise<T> { const response = await fetch(url); if (!response.ok) throw new RequestError(response.status); return response.json(); }

function loadError(error: unknown): string {
  return error instanceof RequestError && error.status === 401 ? 'Sign in again to view this case.'
    : error instanceof RequestError && error.status === 404 ? 'This case is not available to your account.'
      : 'Unable to load case data. Try again later.';
}

export default function App() {
  const [apiState, setApiState] = useState<'checking' | 'ready' | 'unavailable'>('checking');
  const [session, setSession] = useState<SignedInSession | null>(null);
  const [data, setData] = useState<Projections | null>(null);
  const [loadingCase, setLoadingCase] = useState(false);
  const [caseError, setCaseError] = useState('');
  const [reason, setReason] = useState('');
  const [decisionBusy, setDecisionBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [traceView, setTraceView] = useState<InvestigationTraceView | null>(null);
  const traceRequestId = useRef(0);

  useEffect(() => {
    let active = true;
    fetch('/api/health')
      .then((response) => {
        if (!response.ok) throw new Error('API health check failed');
        return response.json();
      })
      .then(() => { if (active) setApiState('ready'); })
      .catch(() => { if (active) setApiState('unavailable'); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (!session) {
      traceRequestId.current += 1;
      setData(null);
      setCaseError('');
      setLoadingCase(false);
      setReason('');
      setTraceView(null);
      return;
    }
    let active = true;
    setLoadingCase(true);
    setCaseError('');
    (async () => {
      try {
        const caseProjection = await getProjection<CaseProjection>('/api/cases/case-001');
        const dashboardProjection = await getProjection<DashboardData>('/api/cases/case-001/dashboard');
        const reviewProjection = await getProjection<TechnicalReviewProjection>('/api/cases/case-001/technical-reviews');
        const readiness = await getProjection<ReadinessSummaryData>('/api/cases/case-001/readiness');
        if (!active) return;
        setData({ caseState: caseProjection, dashboard: dashboardProjection, review: reviewProjection, readiness });
      } catch (error) {
        if (!active) return;
        setData(null);
        setCaseError(loadError(error));
      } finally {
        if (active) setLoadingCase(false);
      }
    })();
    return () => { active = false; };
  }, [session, refresh]);

  async function showTrace(url: string) {
    const requestId = ++traceRequestId.current;
    setTraceView({ status: 'loading' });
    try {
      const trace = await getProjection<InvestigationTraceRecord>(url);
      if (requestId === traceRequestId.current) setTraceView({ status: 'loaded', trace });
    } catch (error) {
      if (requestId !== traceRequestId.current) return;
      setTraceView(error instanceof RequestError && error.status === 404
        ? { status: 'not-found' }
        : { status: 'error' });
    }
  }

  async function submitDecision(capabilityId: string, decision: string) {
    if (
      capabilityId !== 'technical-review'
      || !['accept', 'reject'].includes(decision)
      || !data?.dashboard.allowedDecisions.some((action) => action.id === decision)
      || !session || !data
      || !Number.isInteger(data.caseState.revision) || !Number.isInteger(data.review.basisVersion)
      || !reason.trim()
    ) return;

    setDecisionBusy(true);
    setCaseError('');
    try {
      const response = await fetch('/api/cases/case-001/technical-reviews', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': session.csrfToken },
        body: JSON.stringify({
          expectedRevision: data.caseState.revision,
          expectedBasisVersion: data.review.basisVersion,
          decision,
          reason: reason.trim(),
        }),
      });
      if (response.status === 403) {
        setCaseError('You are not permitted to record this decision.');
        return;
      }
      if (response.status === 409) {
        setCaseError('The case changed. Refresh the case before deciding.');
        return;
      }
      if (!response.ok) throw new RequestError(response.status);
      setRefresh((value) => value + 1);
    } catch {
      setCaseError('Unable to record the decision. Try again later.');
    } finally {
      setDecisionBusy(false);
    }
  }

  const currentDecision = data?.review.decisions.filter((item) => item.current).at(-1)?.decision;
  const hasDecision = data?.dashboard.allowedDecisions.some((action) => action.id === 'accept' || action.id === 'reject');
  const healthMessage = {
    checking: 'Checking service connection…',
    ready: 'Service connected',
    unavailable: 'Service unavailable. Try again later.',
  }[apiState];

  return (
    <main>
      <h1>Airline asset review</h1>
      <p>Reviewer workspace</p>
      <p role="status" aria-live="polite">{healthMessage}</p>
      <SignIn onSessionChange={setSession} />
      {session && (
        <section aria-labelledby="case-heading">
          {data && <ReadinessSummary summary={data.readiness} />}
          <h2 id="case-heading">Case case-001</h2>
          {loadingCase && <p role="status">Loading case data…</p>}
          {caseError && <p role="alert">{caseError}</p>}
          {data && (
            <>
              <p>Revision: {data.caseState.revision}</p>
              <p>Evidence basis: {data.caseState.basis.length}</p>
              <p>Technical-review decision: {currentDecision ?? 'No current decision'}</p>
              {hasDecision && (
                <label>Reason for decision<textarea value={reason} onChange={(event) => setReason(event.target.value)} /></label>
              )}
              <CapabilityDashboard data={data.dashboard} onTraceSelect={(url) => { void showTrace(url); }} onDecision={(capabilityId, decision) => {
                if (!decisionBusy) void submitDecision(capabilityId, decision);
              }} />
              {traceView && <InvestigationTrace view={traceView} />}
            </>
          )}
        </section>
      )}
    </main>
  );
}
