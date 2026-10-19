# Source-system telemetry: approved additions

This file is the recorded approval required by `aircraft-source-systems` DD-025. It is read at startup by
`TelemetryRedaction.ReadApprovedSourceAttributes`: a source-specific attribute is emitted only when it is listed under
`source-specific-attributes` below. Merging this file to the default branch is the approval; the person who merges it is the approver.

Approver: the person who merges the pull request that adds or changes this file.
Scope: structured logs for source reads, source-mode changes and fixture loads (FR-010, NFR-006).

## source-specific-attributes

- source.id
- case.id
- event.outcome
- duration

## metric-dimensions

None of the attributes above is a metric dimension. Raw record identifiers and payloads stay excluded from every log, span and metric.
