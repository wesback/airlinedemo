import { useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import type { Session } from './api';
import CopilotConfirmationCard from './CopilotConfirmation';
import {
  copilotMessage, getCopilotConversation, postCopilot, type CopilotAnswer, type CopilotHistoryTurn,
  type CopilotQuestionBody, type CopilotRecord,
} from './copilotApi';

export const COPILOT_NOTICE = 'Synthetic demo only. Do not enter personal, confidential, live aircraft or customer data. Your question is sent to the governed model. Conversation history is service-managed; this page does not persist transcripts in browser storage.';
const MAX_QUESTION = 2000;

type Props = { session: Session; caseId: string | null };
type Exchange = {
  scope: string; question: string; releaseNote: string; busy: boolean; busyLabel: string; error: string;
  answer: CopilotAnswer | null; sentNote: string; selected: string; resumeHandle: string;
  conversationHandle: string | null; history: CopilotHistoryTurn[]; statusMessage: string;
};

const recordKey = (record: { sourceId: string; recordId: string; recordVersion: number }) => `${record.sourceId}\u0000${record.recordId}\u0000${record.recordVersion}`;
const isObject = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
export const WARNING_CHIP = 'Untrusted instructions detected in a source record';

/**
 * Renders payload fields as a definition list of text. Flagged fields are looked up where the platform screens them:
 * inside a `fields` object when the payload has one, otherwise at the top level. Values are never parsed as markup.
 */
function PayloadList({ value, flagged }: { value: Record<string, unknown>; flagged: ReadonlySet<string> }): ReactNode {
  const screened = isObject(value.fields) ? value.fields : value;
  const list = (entries: Record<string, unknown>, marks: ReadonlySet<string>) => <dl className="av-copilot-payload">
    {Object.entries(entries).map(([name, item]) => <div key={name}>
      <dt>{name}</dt>
      <dd>{isObject(item) ? list(item, item === screened ? flagged : new Set())
        : marks.has(name) ? <mark>{typeof item === 'string' ? item : JSON.stringify(item)}</mark>
          : typeof item === 'string' ? item : JSON.stringify(item ?? null)}</dd>
    </div>)}
  </dl>;
  return list(value, screened === value ? flagged : new Set());
}

const blank = (scope: string): Exchange => ({
  scope, question: '', releaseNote: '', busy: false, busyLabel: '', error: '', answer: null, sentNote: '',
  selected: '', resumeHandle: '', conversationHandle: null, history: [], statusMessage: '',
});
const terminalConversationErrors = new Set([
  'conversation_expired', 'conversation_context_mismatch', 'conversation_turn_limit', 'conversation_unavailable',
  'conversation_history_invalid', 'conversation_not_found',
]);

/**
 * One-question copilot surface bound to the selected case (FR-016). It keeps a single question and answer in memory
 * only; a different case or user resets it. The answer and record payloads are rendered as React text, never markup.
 */
export default function CopilotPanel({ session, caseId }: Props) {
  const scope = `${session.id}\u0000${session.role}\u0000${caseId ?? ''}`;
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
    if (current.conversationHandle) body.conversationHandle = current.conversationHandle;
    update({ busy: true, busyLabel: 'Asking the copilot…', error: '', answer: null, selected: '', sentNote: note, statusMessage: '' });
    const outcome = await postCopilot<CopilotAnswer>('/api/v1/copilot/questions', session.csrfToken, body);
    if (ticket !== request.current) return;
    if (outcome.ok) {
      const handle = outcome.value.conversationState === 'retired'
        ? null
        : outcome.value.conversationHandle ?? current.conversationHandle;
      const history = handle
        ? [...current.history, { question, answer: outcome.value.answer, citations: outcome.value.citations }]
        : current.history;
      update({
        busy: false, busyLabel: '', answer: outcome.value, conversationHandle: handle,
        resumeHandle: handle ?? '', history, error: '', statusMessage: '',
      });
    } else {
      const terminal = terminalConversationErrors.has(outcome.code);
      update({
        busy: false, busyLabel: '', error: copilotMessage(outcome.code),
        ...(terminal ? { answer: null, conversationHandle: null, resumeHandle: '', history: [] } : {}),
      });
    }
  }

  async function resume() {
    if (current.busy) return;
    const handle = current.resumeHandle.trim();
    if (!handle) {
      update({ error: copilotMessage('invalid_conversation_handle'), statusMessage: '' });
      return;
    }
    const ticket = ++request.current;
    update({
      busy: true, busyLabel: 'Loading conversation history…', error: '', answer: null, selected: '',
      conversationHandle: null, history: [], statusMessage: '',
    });
    const outcome = await getCopilotConversation(handle);
    if (ticket !== request.current) return;
    if (!outcome.ok) {
      update({ busy: false, busyLabel: '', error: copilotMessage(outcome.code), conversationHandle: null, history: [] });
      return;
    }
    const conversation = outcome.value;
    if (conversation.conversationState !== 'active' || conversation.conversationHandle !== handle
      || !Array.isArray(conversation.turns)
      || conversation.turns.some((turn) => typeof turn.question !== 'string' || typeof turn.answer !== 'string' || !Array.isArray(turn.citations))) {
      update({ busy: false, busyLabel: '', error: copilotMessage('invalid_response'), conversationHandle: null, history: [] });
      return;
    }
    update({
      busy: false, busyLabel: '', error: '', conversationHandle: conversation.conversationHandle,
      resumeHandle: conversation.conversationHandle, history: conversation.turns, statusMessage: 'Conversation history restored.',
    });
  }

  function startNew() {
    request.current++;
    setState({ ...blank(scope), statusMessage: 'New conversation started.' });
  }

  function select(key: string) {
    update({ selected: key });
    const element = records.current.get(key);
    element?.scrollIntoView?.({ block: 'nearest' });
    element?.focus();
  }

  const answer = current.answer;
  const available = new Set((answer?.records ?? []).map(recordKey));
  const detected = (answer?.records ?? []).filter((record) => record.screening?.state === 'detected');
  const unavailable = (answer?.records ?? []).some((record) => record.screening?.state === 'unavailable');
  const status = current.busy ? current.busyLabel : current.statusMessage || (answer ? 'Answer received.' : '');
  const previousTurns = answer ? current.history.slice(0, -1) : current.history;

  return (
    <aside className="av-copilot" aria-labelledby={`${ids}-heading`}>
      <h2 id={`${ids}-heading`}>Case copilot</h2>
      <p className="av-notice av-copilot-notice">{COPILOT_NOTICE}</p>
      <p className="av-muted">{caseId ? `Selected case: ${caseId}` : 'No case selected. The copilot can list your cases only.'}</p>
      <section className="av-copilot-conversation" aria-labelledby={`${ids}-conversation-heading`}>
        <h3 id={`${ids}-conversation-heading`}>Conversation</h3>
        <p className="av-muted">Resume with an opaque handle from this same user. The server rechecks access before returning history.</p>
        <label className="av-field">Conversation handle
          <input type="text" value={current.resumeHandle} readOnly={Boolean(current.conversationHandle)}
            onChange={(event) => update({ resumeHandle: event.target.value })} />
        </label>
        <div className="av-copilot-actions">
          <button type="button" className="av-button av-button-secondary" disabled={current.busy || !current.resumeHandle.trim() || Boolean(current.conversationHandle)}
            onClick={() => void resume()}>Resume conversation</button>
          <button type="button" className="av-button av-button-ghost" disabled={current.busy}
            onClick={startNew}>Start new conversation</button>
        </div>
      </section>
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
      {previousTurns.length > 0 && <section className="av-copilot-history" aria-labelledby={`${ids}-history-heading`}>
        <h3 id={`${ids}-history-heading`}>Conversation history</h3>
        {previousTurns.map((turn, index) => <article key={`${index}-${turn.question}`}>
          <h4>Question</h4>
          <p className="av-copilot-text">{turn.question}</p>
          <h4>Answer</h4>
          <p className="av-copilot-text">{turn.answer}</p>
        </article>)}
      </section>}
      {answer && <section className="av-copilot-answer" aria-labelledby={`${ids}-answer`}>
        <h3 id={`${ids}-answer`}>Answer</h3>
        <p className="av-copilot-text">{answer.answer}</p>
        {(detected.length > 0 || unavailable) && <ul className="av-copilot-chips" aria-label="Screening">
          {detected.map((record) => {
            const key = recordKey(record);
            return <li key={key}>
              <button type="button" className="av-chip av-chip-warning" aria-pressed={current.selected === key} onClick={() => select(key)}>
                {WARNING_CHIP}<span className="av-chip-detail">{`: ${record.sourceId} ${record.recordId} v${record.recordVersion}`}</span>
              </button>
            </li>;
          })}
          {unavailable && <li><span className="av-chip av-chip-neutral">Screening unavailable</span></li>}
        </ul>}
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
            {isObject(record.payload)
              ? <PayloadList value={record.payload}
                flagged={new Set(current.selected === key && record.screening?.state === 'detected' ? record.screening.flaggedFields : [])} />
              : <dl className="av-copilot-payload"><div><dt>payload</dt><dd>{JSON.stringify(record.payload ?? null)}</dd></div></dl>}
          </article>;
        })}
      </section>}
    </aside>
  );
}
