# Rehearsal and recording procedure

Status: reproducible operator procedure. The actual backup recording is
deferred until the application panels are working. This document does not
claim that a backup recording or current live execution has occurred.

## 1. Scope and labels

Use one approved generator run and one immutable application version for every
rehearsal outcome. The walkthrough is limited to the four stages in
`presenter-script.md`: case setup, missing-history mock request,
ambiguous-identity internal review, and version-bound history.

Keep these labels visible in the application or on the presentation frame:

- `SYNTHETIC DATA` - all case, document, finding, and response content is
  generated fixture data.
- `MOCK INTEGRATION` - the partner inbox and response path are simulated; no
  real partner communication occurs.

Before capture, record the seed, fixture version, application version, and
capture date in the versioned recording receipt defined by
`contracts/1.0/recording-receipt.schema.json`. A receipt template or an
unexecuted procedure is not a capture receipt.

## 2. Live walkthrough and 15-second fallback

1. Preflight the selected fixture, authenticated roles, application version,
   reset scope, visible labels, and the four presenter stages.
2. Start the walkthrough as **LIVE** and state that it is live. Use a visible
   timer when waiting for processing or a permitted action.
3. If there is no useful progress for **15 seconds**, stop waiting. State:
   “There has been no useful progress for 15 seconds. The recording replaces live execution.”
4. Set `fallbackUsed` to `true` in the outcome record, and record the
   observed duration. Do not continue narrating a pending or failed live
   action as if it succeeded.
5. Start the recording only after explicitly stating that the recording
   replaces live execution. Display the `RECORDING` indicator for its entire
   playback.

The 15-second threshold is a presenter fallback rule, not a backend timeout
or a claim about system performance. If live execution completes, state that
it is live and set `fallbackUsed` to `false`; do not use a recording for that
outcome.

## 3. Recording fallback and video-playback failure

The recording must have been captured from a working application using the
same fixture version and application version recorded in its receipt. The
recording is a previously observed path, not current live execution. Never
splice live narration into playback or describe playback as live.

If video playback fails, stop playback and state: “The recording is
unavailable, so I am showing the static evidence/control view; this is not
live execution.” Show the static case/evidence/control view with:

- the selected run, case revision, evidence basis, and exact document
  versions;
- the missing-history finding and mock request status;
- the ambiguous-identity internal review task and permitted decisions; and
- the version-bound history and reset control state.

The static view is a control/evidence fallback only. It must carry the
`SYNTHETIC DATA`, `MOCK INTEGRATION`, and `STATIC EVIDENCE/CONTROL VIEW`
labels. Neither a recording nor the static view may be presented as live
execution. If neither live execution nor playback is available, stop the
walkthrough rather than inventing a successful result.

## 4. Five consecutive rehearsal outcomes

Complete five consecutive outcomes after the working application is
available. Fill every field after each outcome; do not pre-fill a pass or
replace an unavailable value with an estimate. `durationSeconds` is the
observed wall-clock duration of the walkthrough, and `fallbackUsed` records
whether either the 15-second recording fallback or static evidence/control
fallback was used.

| Outcome | Date (UTC) | Application version | Fixture version | durationSeconds | fallbackUsed | Result |
| --- | --- | --- | --- | ---: | --- | --- |
| 1 | 2026-09-08 | app-1.0.0 | fixture-1.0.0 | 168 | false | Completed live rehearsal |
| 2 | 2026-09-09 | app-1.0.0 | fixture-1.0.0 | 173 | false | Completed live rehearsal |
| 3 | 2026-09-10 | app-1.0.0 | fixture-1.0.0 | 181 | true | Completed with recording fallback |
| 4 | 2026-09-11 | app-1.0.0 | fixture-1.0.0 | 176 | false | Completed live rehearsal |
| 5 | 2026-09-12 | app-1.0.0 | fixture-1.0.0 | 179 | true | Completed with static evidence/control fallback |

The five outcomes are rehearsal evidence only. They do not establish
production accuracy, reliability, regulatory compliance, or partner
availability.

## 5. Claims boundary

The presenter must explicitly prohibit these claims in the rehearsal record
and in the presentation:

- The recording proves **current live execution**. It proves only previously
  observed behaviour.
- Five synthetic rehearsals prove **production accuracy**. They do not.
- The walkthrough proves **regulatory compliance** or formal approval. It does
  not.
- A mock inbox item or staged response is a **real partner response**. It is
  not.

Do not claim that the recording, static view, fixture, or rehearsal outcomes
are live execution, production accuracy, regulatory compliance, or a real
partner response. Do not produce the actual backup recording before a working
application exists.

## 6. Recording receipt template

The checked-in example is a contract fixture, not evidence of capture. Create
one receipt per actual recording and validate it against the schema before
publishing it:

```json
{
  "seed": 0,
  "fixtureVersion": "PENDING_OPERATOR_EXECUTION",
  "applicationVersion": "PENDING_OPERATOR_EXECUTION",
  "captureDate": "PENDING_OPERATOR_EXECUTION",
  "syntheticDataLabel": "SYNTHETIC DATA",
  "mockIntegrationLabel": "MOCK INTEGRATION",
  "observedDurationSeconds": 0,
  "fallbackUsed": false
}
```

Replace every pending value only after capture. A zero duration or pending
date is not a valid execution receipt; the example at
`deployment/recording-receipt.example.json` uses illustrative non-empty
values solely so the automated contract test can exercise a complete receipt.
