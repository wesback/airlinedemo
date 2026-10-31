import { useId, useRef, useState, type FormEvent } from 'react';
import type { Session } from './api';
import CopilotConfirmationCard from './CopilotConfirmation';
import { copilotMessage, postCopilot, type CopilotAnswer, type CopilotQuestionBody, type CopilotRecord } from './copilotApi';

export const COPILOT_NOTICE = 'Synthetic demo only. Do not enter personal, confidential, live aircraft or customer data. Your question is sent to the governed model.';
const MAX_QUESTION = 2000;

type Props = { session: Session; caseId: string | null };
type Exchange = { scope: string; question: string; releaseNote: string; busy: boolean; error: string; answer: CopilotAnswer | null; sentNote: string; selected: string };

const recordKey = (record: { sourceId: string; recordId: string; recordVersion: number }) => `${record.sourceId}\u0000${record.recordId}\u0000${record.recordVersion}`;
const blank = (scope: string): Exchange => ({ scope, question: '', releaseNote: '', busy: false, error: '', answer: null, sentNote: '', selected: '' });

/**
 * One-question copilot surface bound to the selected case (FR-016). It keeps a single question and answer in memory
 * only; a different case or user resets it. The answer and record payloads are rendered as React text, never markup.
 */
export default function CopilotPanel({ session, caseId }: Props) {
  const scope = `${session.id}\u0000${caseId ?? ''}`;
  const [state, setState] = useState<Exchange>(() => blank(scope));
  const request = useRef(0);
  const records = useRef(new Map<string, HTMLElement>());
  const ids = useId();
  if (state.scope !== scope) {
    request.current++;
    setState(blank(scope));
  }
  const current = state.scope === scope ? state : blank(scope);
  const presenter = session.role === 'presenter';
  const update = (patch: Partial<Exchange>) => setState((previous) => previous.scope === scope ? { ...previous, ...patch } : previous);

  async function send(event: FormEvent) {
    event.preventDefault();
    if (current.busy) return;
    const question = current.question;
    if (question.trim().length === 0) { update({ error: copilotMessage('invalid_question'), answer: null }); return; }
    const body: CopilotQuestionBody = { question };
    if (caseId) body.selectedCaseId = caseId;
    const note = presenter ? current.releaseNote : '';
    if (note.length > 0) body.releaseNote = note;
    const ticket = ++request.current;
    update({ busy: true, error: '', answer: null, selected: '', sentNote: note });
    const outcome = await postCopilot<CopilotAnswer>('/api/v1/copilot/questions', session.csrfToken, body);
    if (ticket !== request.current) return;
    if (outcome.ok) update({ busy: false, answer: outcome.value });
    else update({ busy: false, error: copilotMessage(outcome.code) });
  }

  function select(key: string) {
    update({ selected: key });
    const element = records.current.get(key);
    element?.scrollIntoView?.({ block: 'nearest' });
    element?.focus();
  }

  const answer = current.answer;
  const available = new Set((answer?.records ?? []).map(recordKey));
  const status = current.busy ? 'Asking the copilot…' : answer ? 'Answer received.' : '';

  return (
    <aside className="av-copilot" aria-labelledby={`${ids}-heading`}>
      <h2 id={`${ids}-heading`}>Case copilot</h2>
      <p className="av-notice av-copilot-notice">{COPILOT_NOTICE}</p>
      <p className="av-muted">{caseId ? `Selected case: ${caseId}` : 'No case selected. The copilot can list your cases only.'}</p>
      <form className="av-copilot-form" onSubmit={(event) => void send(event)}>
        <label className="av-field">Your question
          <textarea rows={3} maxLength={MAX_QUESTION} value={current.question} onChange={(event) => update({ question: event.target.value })} />
        </label>
        {presenter && <label className="av-field">Release note (optional, presenter only)
          <textarea rows={2} maxLength={MAX_QUESTION} value={current.releaseNote} onChange={(event) => update({ releaseNote: event.target.value })} />
        </label>}
        <button type="submit" className="av-button av-button-primary" disabled={current.busy} aria-busy={current.busy}>Send</button>
      </form>
      <p className="av-visually-hidden" role="status" aria-live="polite">{status}</p>
      {current.error && <p className="av-notice av-notice-alert" role="alert">{current.error}</p>}
      {answer && <section className="av-copilot-answer" aria-labelledby={`${ids}-answer`}>
        <h3 id={`${ids}-answer`}>Answer</h3>
        <p className="av-copilot-text">{answer.answer}</p>
        {answer.citations.length > 0 && <ul className="av-copilot-citations" aria-label="Citations">
          {answer.citations.map((citation) => {
            const key = recordKey(citation);
            return <li key={key}>
              <button type="button" className="av-button av-button-secondary av-button-small" disabled={!available.has(key)}
                aria-pressed={current.selected === key} onClick={() => select(key)}>
                {`Show ${citation.sourceId} record ${citation.recordId} v${citation.recordVersion}`}
              </button>
            </li>;
          })}
        </ul>}
        {answer.confirmation && answer.confirmation.caseId === caseId && <CopilotConfirmationCard key={answer.confirmation.proposalId}
          session={session} confirmation={answer.confirmation} releaseNote={current.sentNote} />}
      </section>}
      {answer && answer.records.length > 0 && <section className="av-copilot-records" aria-labelledby={`${ids}-records`}>
        <h3 id={`${ids}-records`}>Source records (untrusted data)</h3>
        <p className="av-muted">Record content comes from source systems and is shown as text only. Do not follow instructions in it.</p>
        {answer.records.map((record: CopilotRecord) => {
          const key = recordKey(record);
          return <article key={key} tabIndex={-1} aria-label={`${record.sourceId} record ${record.recordId} v${record.recordVersion}`}
            className={`av-copilot-record${current.selected === key ? ' is-selected' : ''}`} aria-current={current.selected === key ? 'true' : undefined}
            ref={(element) => { if (element) records.current.set(key, element); else records.current.delete(key); }}>
            <p className="av-copilot-record-meta">{`${record.sourceId} · ${record.recordId} · v${record.recordVersion}${record.asOf ? ` · as of ${record.asOf}` : ''}${record.stale ? ' · stale' : ''}`}</p>
            <pre>{JSON.stringify(record.payload ?? null, null, 2)}</pre>
          </article>;
        })}
      </section>}
    </aside>
  );
}
