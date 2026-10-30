import type { AuditEvent, CaseRecord } from './api';

export type Actor = 'ai' | 'human' | 'rules';
export type AircraftCategory = 'narrow-body' | 'regional' | 'wide-body';

export type AircraftProfile = {
  caseId: string;
  model: string;
  manufacturer: string;
  category: AircraftCategory;
  registration: string;
  msn: string;
  engines: string;
  outgoingLessee: string;
  nextLessee: string;
  returnDate: string;
  seats: number;
};

// Fictional demo fleet. Registrations, lessees and serial numbers are synthetic; only the aircraft types are real.
const FLEET: Record<string, AircraftProfile> = {
  'cp-case-a': {
    caseId: 'cp-case-a', manufacturer: 'Airbus', model: 'A320neo', category: 'narrow-body', registration: 'N-CPAX',
    msn: 'MSN 10412', engines: '2 × CFM LEAP-1A', outgoingLessee: 'Northwind Air', nextLessee: 'Meridian Airways',
    returnDate: '14 Oct 2026', seats: 180,
  },
  'cp-case-b': {
    caseId: 'cp-case-b', manufacturer: 'Embraer', model: 'E195-E2', category: 'regional', registration: 'N-CPBX',
    msn: 'MSN 19020455', engines: '2 × PW1900G', outgoingLessee: 'Fjord Regional', nextLessee: 'Coastline Connect',
    returnDate: '14 Oct 2026', seats: 132,
  },
  'cp-case-c': {
    caseId: 'cp-case-c', manufacturer: 'Airbus', model: 'A350-900', category: 'wide-body', registration: 'CP-WB-003',
    msn: 'MSN 0583', engines: '2 × Trent XWB-84', outgoingLessee: 'Equator Long Haul', nextLessee: 'Polaris International',
    returnDate: '21 Oct 2026', seats: 315,
  },
  'case-001': {
    caseId: 'case-001', manufacturer: 'Boeing', model: '737-8', category: 'narrow-body', registration: 'N-DEMO1',
    msn: 'MSN 64101', engines: '2 × CFM LEAP-1B', outgoingLessee: 'Sunline Charter', nextLessee: 'To be assigned',
    returnDate: '3 Nov 2026', seats: 178,
  },
  'case-002': {
    caseId: 'case-002', manufacturer: 'Airbus', model: 'A321neo', category: 'narrow-body', registration: 'N-DEMO2',
    msn: 'MSN 11208', engines: '2 × PW1100G', outgoingLessee: 'Harbor Air', nextLessee: 'To be assigned',
    returnDate: '17 Nov 2026', seats: 206,
  },
};

export function aircraftFor(caseId: string): AircraftProfile {
  return FLEET[caseId] ?? {
    caseId, manufacturer: 'Aircraft', model: caseId, category: 'narrow-body', registration: caseId.toUpperCase(),
    msn: 'Synthetic', engines: '2 engines', outgoingLessee: 'Outgoing lessee', nextLessee: 'To be assigned',
    returnDate: 'Not scheduled', seats: 0,
  };
}

export type StepId = 'arrival' | 'intake' | 'investigation' | 'technical' | 'finance' | 'legal' | 'release';

export type StepDefinition = {
  id: StepId;
  title: string;
  short: string;
  actor: Actor;
  actorLabel: string;
  owner: string;
  ownerRole?: string;
  description: string;
};

export const STEPS: StepDefinition[] = [
  { id: 'arrival', title: 'Arrival in the return pool', short: 'Arrival', actor: 'rules', actorLabel: 'Automated', owner: 'Asset system',
    description: 'The aircraft comes off lease and joins the transition pool with its contract, return conditions and target redelivery date.' },
  { id: 'intake', title: 'Records package intake', short: 'Records intake', actor: 'rules', actorLabel: 'Automated', owner: 'Intake service',
    description: 'The outgoing lessee uploads the records package. Every document is parsed, versioned and pinned to an evidence basis.' },
  { id: 'investigation', title: 'AI records investigation', short: 'AI investigation', actor: 'ai', actorLabel: 'AI agent', owner: 'Records agent',
    description: 'The records agent cross-checks engine identity, life-limited parts and maintenance history, cites each source page, and raises findings for a human.' },
  { id: 'technical', title: 'Technical review', short: 'Technical review', actor: 'human', actorLabel: 'Human decision', owner: 'Technical reviewer', ownerRole: 'technical-reviewer',
    description: 'The AI can find problems in the records but may not judge airworthiness or waive a return condition. A technical reviewer verifies each finding against the cited records and accepts or rejects it. Nothing moves on without this decision.' },
  { id: 'finance', title: 'Maintenance reserve', short: 'Finance reserve', actor: 'human', actorLabel: 'Rules + human approval', owner: 'Finance reviewer', ownerRole: 'finance-reviewer',
    description: 'The reserve is calculated by contract rules from utilisation and rate reports. A finance reviewer approves it or returns it.' },
  { id: 'legal', title: 'Return obligations sign-off', short: 'Legal sign-off', actor: 'human', actorLabel: 'Rules + human sign-off', owner: 'Legal reviewer', ownerRole: 'legal-reviewer',
    description: 'Contract obligations are evaluated against the evidence. A legal reviewer signs off; no notice is ever sent automatically.' },
  { id: 'release', title: 'Release to the next lessee', short: 'Release', actor: 'human', actorLabel: 'Human decision', owner: 'Transition lead', ownerRole: 'presenter',
    description: 'With every gate passed, the transition lead releases the aircraft for redelivery to the next lessee.' },
];

export type StepState = 'done' | 'current' | 'upcoming' | 'returned';

export type CaseProgress = {
  caseId: string;
  revision: number;
  stepIndex: number;
  states: Record<StepId, StepState>;
  technical: string | null;
  finance: string | null;
  legal: string | null;
  lastActivity: string | null;
  blocked: boolean;
  released: boolean;
};

function decisionOf(audit: AuditEvent[], action: string): string | null {
  const event = [...audit].reverse().find((item) => item.action === action);
  if (!event) return null;
  try {
    const parsed = JSON.parse(event.detail) as Record<string, unknown>;
    const value = parsed.decision ?? parsed.Decision;
    return typeof value === 'string' ? value : null;
  } catch {
    return null;
  }
}

const AI_ACTORS = new Set(['records-agent', 'assessment-service']);

export function actorKind(actor: string, action = ''): Actor {
  if (action === 'create-work-item' || actor === 'system' || actor === 'intake') return 'rules';
  if (action === 'journey' && actor === 'presenter') return 'rules';
  if (AI_ACTORS.has(actor)) return 'ai';
  return 'human';
}

export function deriveProgress(record: CaseRecord): CaseProgress {
  const audit = record.audit ?? [];
  const hasBasis = (record.basis ?? []).length > 0;
  const investigated = audit.some((item) => AI_ACTORS.has(item.actor) || item.action === 'create-work-item');
  const technical = decisionOf(audit, 'technical-review.decided');
  const finance = decisionOf(audit, 'reserve.decided');
  const legal = decisionOf(audit, 'obligation.legal-decided');
  const released = audit.some((item) => item.action === 'aircraft.released');

  const done: Record<StepId, boolean> = {
    arrival: true,
    intake: hasBasis,
    investigation: hasBasis && investigated,
    technical: technical === 'accept',
    finance: technical === 'accept' && finance === 'approve',
    legal: technical === 'accept' && finance === 'approve' && legal === 'signed-off',
    release: released,
  };
  let stepIndex = STEPS.findIndex((step) => !done[step.id]);
  if (stepIndex < 0) stepIndex = STEPS.length - 1;
  const states = Object.fromEntries(STEPS.map((step, index) => [step.id,
    done[step.id] ? 'done' : index === stepIndex ? 'current' : 'upcoming'])) as Record<StepId, StepState>;
  if (technical === 'reject') states.technical = 'returned';
  if (finance === 'return') states.finance = 'returned';
  if (legal === 'rejected') states.legal = 'returned';
  const blocked = technical === 'reject' || finance === 'return' || legal === 'rejected';
  const last = audit.length ? audit[audit.length - 1].at : null;
  return { caseId: record.caseId, revision: record.revision, stepIndex, states, technical, finance, legal, lastActivity: last, blocked, released };
}

export function waitingOn(progress: CaseProgress): { actor: Actor; label: string } {
  const step = STEPS[progress.stepIndex];
  if (progress.states[step.id] === 'returned') return { actor: 'human', label: `Returned at ${step.short.toLowerCase()}` };
  if (step.id === 'intake') return { actor: 'rules', label: 'Waiting for the records package' };
  if (step.id === 'investigation') return { actor: 'ai', label: 'AI agent investigating records' };
  if (progress.released) return { actor: 'human', label: 'Released to the next lessee' };
  if (step.id === 'release') return { actor: 'human', label: 'Ready for release' };
  return { actor: step.actor, label: `Waiting for ${step.owner.toLowerCase()}` };
}

export type Persona = { id: string; label: string; role: string; description: string };

export const PERSONAS: Persona[] = [
  { id: 'presenter', label: 'Demo presenter', role: 'presenter', description: 'Runs the demo and releases aircraft as transition lead.' },
  { id: 'reviewer', label: 'Technical reviewer', role: 'technical-reviewer', description: 'Accepts or rejects AI findings.' },
  { id: 'fin-1', label: 'Finance reviewer', role: 'finance-reviewer', description: 'Approves maintenance reserves.' },
  { id: 'legal-1', label: 'Legal reviewer', role: 'legal-reviewer', description: 'Signs off return obligations.' },
  { id: 'exec-1', label: 'Executive observer', role: 'executive', description: 'Follows the portfolio, read only.' },
];

export const personaForRole = (role: string) => PERSONAS.find((persona) => persona.role === role);

export function humanizeAction(event: AuditEvent): string {
  switch (event.action) {
    case 'technical-review.decided': return `Technical review ${decisionOf([event], event.action) === 'accept' ? 'accepted the findings' : 'rejected the findings'}`;
    case 'reserve.decided': return `Reserve ${decisionOf([event], event.action) === 'approve' ? 'approved' : 'returned'} by finance`;
    case 'obligation.legal-decided': return `Obligations ${decisionOf([event], event.action) === 'signed-off' ? 'signed off' : 'rejected'} by legal`;
    case 'create-work-item': return 'Review task created for the technical reviewer';
    case 'change-work-item-state': return 'Review task closed';
    case 'aircraft.released': return 'Aircraft released to the next lessee';
    case 'journey': return event.detail;
    default: return event.action.replace(/[.-]/g, ' ');
  }
}

export function actorName(actor: string): string {
  const map: Record<string, string> = {
    'records-agent': 'Records agent',
    'assessment-service': 'Routing rules',
    presenter: 'Demo presenter',
    reviewer: 'Technical reviewer',
    'technical-reviewer': 'Technical reviewer',
    'fin-1': 'Finance reviewer',
    'finance-reviewer': 'Finance reviewer',
    'legal-1': 'Legal reviewer',
    'legal-reviewer': 'Legal reviewer',
    'exec-1': 'Executive observer',
  };
  return map[actor] ?? actor;
}

export type ActivityItem = { key: string; actor: Actor; who: string; what: string; reason: string | null; at: string };

const ROLE_NARRATION = new Set(['technical-reviewer', 'finance-reviewer', 'legal-reviewer']);

/** Audit trail as a readable timeline, newest first. Narration events that repeat a recorded decision are dropped. */
export function activityFrom(audit: AuditEvent[]): ActivityItem[] {
  return audit
    .filter((event) => !(event.action === 'journey' && ROLE_NARRATION.has(event.actor)))
    .map((event) => {
      let reason: string | null = null;
      if (event.detail.startsWith('{')) {
        try {
          const parsed = JSON.parse(event.detail) as Record<string, unknown>;
          const value = parsed.reason ?? parsed.Reason ?? parsed.note;
          reason = typeof value === 'string' ? value : null;
        } catch { reason = null; }
      }
      return {
        key: `${event.sequence}`,
        actor: actorKind(event.actor, event.action),
        who: event.action === 'journey' && event.actor === 'presenter' ? 'Intake service'
          : event.action === 'aircraft.released' ? 'Transition lead' : actorName(event.actor),
        what: humanizeAction(event),
        reason,
        at: event.at,
      };
    })
    .reverse();
}

export function formatWhen(iso: string | null): string {
  if (!iso) return 'No activity yet';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
}
