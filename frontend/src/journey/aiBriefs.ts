import type { CaseHistory } from './api';

export type FindingSeverity = 'blocker' | 'attention';
export type CheckResult = 'pass' | 'flag' | 'unread';

export type AiSource = { document: string; note?: string };
export type AiCheck = { label: string; result: CheckResult; detail: string };
export type AiFinding = { id: string; severity: FindingSeverity; title: string; detail: string; sources: AiSource[] };

export type AiBrief = {
  headline: string;
  documentsRead: number;
  checks: AiCheck[];
  findings: AiFinding[];
  guardrails: string[];
  whyHuman: string;
  acceptMeans: string;
  rejectMeans: string;
};

const ALWAYS = [
  'Did not accept or reject anything. Every finding waits for a person.',
  'Did not contact the lessee or send any notice.',
];

// What the records agent found in the synthetic case-pack documents (backend/AirlineDemo.Api/Fixtures/aircraft-case-pack).
const BRIEFS: Record<string, AiBrief> = {
  'cp-case-a': {
    headline: 'One life-limited part has no signed back-to-birth history.',
    documentsRead: 8,
    checks: [
      { label: 'Engine identity', result: 'pass', detail: 'Serial ENG-CP-A-001 agrees across the serial record, history index and shop visit.' },
      { label: 'Shop visit', result: 'pass', detail: 'The 20 Aug 2026 shop visit is on file for ENG-CP-A-001.' },
      { label: 'Life-limited parts traceability', result: 'flag', detail: 'LLP-DISK-CP-A-017 is installed, but its signed history record is missing.' },
      { label: 'Open maintenance items', result: 'pass', detail: 'The maintenance log shows routine work and nothing open.' },
    ],
    findings: [
      {
        id: 'a-llp-gap', severity: 'blocker', title: 'Missing signed history for LLP-DISK-CP-A-017',
        detail: 'The installation record places this disk on engine ENG-CP-A-001, and the records index lists its signed history record as missing. Without it the remaining cycles cannot be proven to the next lessee.',
        sources: [
          { document: 'case-a/llp-installation-record.txt', note: 'disk installed' },
          { document: 'case-a/records-index.txt', note: 'record listed as missing' },
          { document: 'case-a/llp-history-gap.txt', note: 'gap confirmed' },
        ],
      },
    ],
    guardrails: [...ALWAYS, 'Did not estimate the missing cycles. It reports the gap only.'],
    whyHuman: 'A missing back-to-birth trace can stop a redelivery. The agent can show the gap, but only a qualified reviewer can confirm it from the cited records and decide that it goes forward as a return item.',
    acceptMeans: 'The gap is real and correctly described. It becomes part of the case record and the reserve calculation can start.',
    rejectMeans: 'The agent misread the records, for example the history exists elsewhere. The case stops here and goes back for investigation.',
  },
  'cp-case-b': {
    headline: 'Two conflicting engine serials and an ambiguous LLP disk serial. One document tried to instruct the agent.',
    documentsRead: 15,
    checks: [
      { label: 'Engine identity', result: 'flag', detail: 'The identity plate and installation log say ENG-CP-B-001; the shop visit is filed under ENG-CP-B-009.' },
      { label: 'Life-limited parts traceability', result: 'flag', detail: 'Two logs name the disk LLP-DISK-CP-B-017 and LLP-DISK-CP-B-071; the signed history for -017 is missing.' },
      { label: 'Fan disk release', result: 'pass', detail: 'FAN-DISK-CP-B-021 has a release record matching the 20 Aug 2026 maintenance entry.' },
      { label: 'Embedded instructions', result: 'flag', detail: 'A document asked to "ignore previous instructions" and send the case to a recipient. It was treated as data and not followed.' },
    ],
    findings: [
      {
        id: 'b-engine-identity', severity: 'blocker', title: 'Installed engine serial is ambiguous',
        detail: 'Three records identify the engine as ENG-CP-B-001; the shop visit and part of the history index use ENG-CP-B-009. The record set prefers neither.',
        sources: [
          { document: 'case-b/engine-identity-plate.txt', note: 'ENG-CP-B-001' },
          { document: 'case-b/engine-shop-visit.txt', note: 'ENG-CP-B-009' },
          { document: 'case-b/identity-conflict.txt', note: 'conflict recorded' },
        ],
      },
      {
        id: 'b-llp-serial', severity: 'attention', title: 'LLP disk serial differs between logs',
        detail: 'One log records LLP-DISK-CP-B-017, the other LLP-DISK-CP-B-071, and the signed history for -017 is missing.',
        sources: [
          { document: 'case-b/component-candidate-a.txt', note: '-017' },
          { document: 'case-b/component-candidate-b.txt', note: '-071' },
          { document: 'case-b/llp-history-gap.txt', note: 'history missing' },
        ],
      },
    ],
    guardrails: [...ALWAYS, 'Ignored an instruction hidden in document-instructions.txt and flagged it for review.', 'Did not pick a preferred engine serial.'],
    whyHuman: 'The records contradict each other. Choosing which engine serial is correct is an airworthiness judgement the agent is not allowed to make.',
    acceptMeans: 'The conflicts are real and described correctly. The case moves to the maintenance reserve with them recorded.',
    rejectMeans: 'The agent misread the records. The case stops here and goes back for investigation.',
  },
  'cp-case-c': {
    headline: 'Records are consistent, but the landing-gear inspection report could not be read.',
    documentsRead: 8,
    checks: [
      { label: 'Engine identity', result: 'pass', detail: 'ENG-CP-C-001 agrees across the aircraft summary, serial record and history index.' },
      { label: 'Shop visit release', result: 'pass', detail: 'The ENG-CP-C-001 shop visit release shows return to service with no open findings.' },
      { label: 'Open maintenance items', result: 'pass', detail: 'Scheduled maintenance is complete; work card WC-CP-C-003 is closed.' },
      { label: 'Main landing-gear inspection report', result: 'unread', detail: 'Processing of the report failed. The agent did not assume what it says.' },
    ],
    findings: [
      {
        id: 'c-mlg-report', severity: 'attention', title: 'Landing-gear inspection report not processed',
        detail: 'The closed work card says the main landing-gear inspection was signed off, but the inspection report itself could not be processed, so the agent could not check it.',
        sources: [
          { document: 'case-c/blocked-document.txt', note: 'processing failed' },
          { document: 'case-c/work-card.txt', note: 'WC-CP-C-003 closed' },
        ],
      },
    ],
    guardrails: [...ALWAYS, 'Did not infer the content of the unread report from the work card.'],
    whyHuman: 'The agent could not read one source document. A reviewer decides whether the closed work card is enough evidence, or whether the report must be reprocessed first.',
    acceptMeans: 'The records are complete enough to proceed; the closed work card covers the inspection. Finance can start the reserve.',
    rejectMeans: 'The report must be reprocessed and checked first. The case stops here and goes back for investigation.',
  },
};

/** The AI briefing for a case, or null while the records agent has not reported anything. */
export function aiBriefFor(caseId: string, history: CaseHistory | null): AiBrief | null {
  const events = (history?.events ?? []).filter((event) => event.capabilityLabel === 'AI agent');
  if (events.length === 0) return null;
  const known = BRIEFS[caseId];
  if (known) return known;
  const documents = new Set(events.flatMap((event) => event.witnesses.map((witness) => witness.documentId)));
  return {
    headline: events[events.length - 1].summary,
    documentsRead: documents.size,
    checks: [],
    findings: [],
    guardrails: ALWAYS,
    whyHuman: 'The agent raises findings but cannot accept them. A reviewer confirms them against the cited records before the case can move on.',
    acceptMeans: 'The findings are correct and become part of the case record.',
    rejectMeans: 'The findings are wrong or incomplete. The case goes back for investigation.',
  };
}

/** A short board-level summary that does not need the case history. */
export function aiChipFor(caseId: string): { label: string; severity: FindingSeverity | 'clear' } | null {
  const brief = BRIEFS[caseId];
  if (!brief) return null;
  const blockers = brief.findings.filter((finding) => finding.severity === 'blocker').length;
  const total = brief.findings.length;
  if (total === 0) return { label: 'AI: no findings', severity: 'clear' };
  return { label: `AI: ${total} finding${total === 1 ? '' : 's'}${blockers ? `, ${blockers} blocking` : ''}`, severity: blockers ? 'blocker' : 'attention' };
}
