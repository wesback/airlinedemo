# Readiness rehearsal checklist

Run this once the README prerequisites, approved deployment, approved model manifest, and synthetic case are available. The product owner performs and records the rehearsal from a clean approved revision. Stop if an approval or prerequisite is missing; do not substitute personal credentials, public endpoints, live records, or a scripted trace.

Use only synthetic cases and the approved identity. Record actual identifiers and elapsed times as observed. If a step fails or its deadline is missed, record the failure and use the recorded fallback transparently; never present a fallback as a live result.

## Rehearsal

- [ ] **Clean revision:** Start with a clean worktree at the approved revision. Record the full repository revision, application version/image digest, and deployment configuration; confirm the approved manifest and fixture/suite versions and hashes.
- [ ] **Seed synthetic case:** Deploy and smoke-check the approved application, then seed/reset one synthetic case using the documented procedure. Record build and release run IDs, smoke-check results, fixture ID, and the demo run ID. Stop on failed smoke checks or non-synthetic data.
- [ ] **Real 30-second trace capture:** In the live application, run the seeded case and capture a 30-second recording of its actual ordered investigation trace. The capture must come from the real frozen-version run, not a scripted, reconstructed, or fixture-generated trace. Record the trace reference and measured capture duration. Confirm the trace and audit evidence identify `service.name`, `service.version`, and `deployment.environment`, and contain no raw document, prompt, or response content.
- [ ] **Outcome within 20 seconds:** Start the outcome timer when the live investigation is triggered and stop when its readiness outcome first appears. Record the measured time and visible result; pass only at 20 seconds or less. Record timeouts and pending/error states as observed, not as success.
- [ ] **Live segment within 90 seconds or fallback:** Time the live presentation segment from its first action through the visible outcome, consequential decision, and response-driven reassessment. Record the measured duration; pass at 90 seconds or less. If the segment cannot be completed within 90 seconds or the live environment is unavailable, state the reason and switch to the recorded fallback, identifying its fixture ID, application version, location, and limitations. Do not report fallback timing as live timing.
- [ ] **Consequential simulated decision:** As the authorized simulated reviewer, make one decision that changes a visible case disposition or permitted next action. Record the role, action, reason, and actual business-audit event reference. Do not describe a proposed or unrecorded action as completed.
- [ ] **Response-driven reassessment:** Process a real synthetic mock-inbox response and verify the resulting reassessment is recorded, changes the current assessment as appropriate, and preserves prior history. Record the response and reassessment audit references and observed result. Do not claim reassessment from a response that was not processed.
- [ ] **Evidence and result review:** Complete every evidence field below from the run and approved evidence location. Reconcile the trace, audit references, run IDs, timings, and displayed results; record failures and limitations. Leave any unavailable value explicitly unrecorded. The product owner verifies the actual trace, audit evidence, and fallback before committing the record.

## Capture boundaries

Use the live investigation trigger and first visible outcome for the 20-second measurement. Time the live segment separately; do not infer either duration from logs or from the 30-second trace recording. Preserve the actual run's trace and audit event identifiers in the approved restricted evidence location. The committed record contains references and redacted metadata only, never secrets, credentials, raw documents, prompts, or responses.

If the fallback cannot be reproduced from its cited fixture and application version, it is not an acceptable fallback. Report the rehearsal incomplete rather than filling gaps with estimates or generated evidence.
