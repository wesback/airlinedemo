# Controlled synthetic fixture load and demo smoke runbook

Status: operator procedure only. This document does not claim that a fixture
has been loaded, that a smoke check has passed, or that a case has been
accepted. Use one selected generator run for the entire procedure.

## 1. Preconditions and boundary

The operator must have:

- a deployed application base URL;
- the deployment image digest or Container Apps revision identifier;
- one approved generator output directory containing
  `generator-contract.json`, `reproducibility-receipt.json`, and the
  `application-inputs/` tree; and
- separate authenticated identities for the reviewer and the mock partner.

The selected run is the `configuration.runId` in
`generator-contract.json`. Confirm that it equals `case.runId`, and record that
value before copying anything. The selected input set is exactly
`pathBoundaries.selectedInitialInputManifest.entries`. Each entry must remain
under `application-inputs/`, have a non-empty document ID and version, and
match the entry's SHA-256 when calculated from the file on disk. The package
manifest must contain exactly those selected initial document IDs, versions,
file names, and hashes.

The initial load may copy only these approved application-input artifacts:

1. `generator-contract.json` for the selected-run boundary;
2. `application-inputs/package-001/manifest.json`;
3. `application-inputs/reference-data/requirements.json`; and
4. the files listed by the selected initial input manifest.

Do not copy the whole generator output directory. In particular, the initial
load must reject or omit `staged-responses/`, `evaluator-only/`,
`replay-only/`, answer keys, scenario metadata, evaluator truth, and every
application-input file not declared by the selected manifest or the explicit
requirements input above. A staged response is submitted later through the
mock-partner route; it is never installed as initial application storage.

## 2. Select and validate the run

Run these checks from the approved fixture directory. Replace the example
paths and values; do not select a run by scanning for the newest directory.

```bash
export FIXTURE_ROOT=/secure/operator-work/fixture-run
export LOAD_ROOT=/secure/operator-work/load
export API_BASE_URL=https://<deployed-app-host>

export RUN_ID="$(jq -er '.configuration.runId' "$FIXTURE_ROOT/generator-contract.json")"
test "$RUN_ID" = "$(jq -er '.case.runId' "$FIXTURE_ROOT/generator-contract.json")"
jq -e '
  (.pathBoundaries.selectedInitialInputManifest.entries | length > 0) and
  (.pathBoundaries.selectedInitialInputManifest.entries |
    all(.relativePath | startswith("application-inputs/"))) and
  (.pathBoundaries.selectedInitialInputManifest.entries |
    all(.sha256 | test("^[A-Fa-f0-9]{64}$")))
' "$FIXTURE_ROOT/generator-contract.json"

export SELECTED_MANIFEST="$FIXTURE_ROOT/application-inputs/package-001/manifest.json"
export SELECTED_MANIFEST_SHA256="$(sha256sum "$SELECTED_MANIFEST" | cut -d' ' -f1)"
jq -e --arg run "$RUN_ID" \
  --slurpfile contract "$FIXTURE_ROOT/generator-contract.json" '
  .runId == $run and .packageId == "PKG-0001" and
  (.manifest | length > 0) and
  (.manifest | all(.documentId and (.version >= 1) and .fileName and
    (.sha256 | test("^[A-Fa-f0-9]{64}$")))) and
  (.manifest | all(. as $document |
    any($contract[0].pathBoundaries.selectedInitialInputManifest.entries[];
      .documentId == $document.documentId and
      .version == $document.version and
      (.relativePath | endswith($document.fileName)) and
      .sha256 == $document.sha256)))
' "$SELECTED_MANIFEST"

jq -r '
  .pathBoundaries.selectedInitialInputManifest.entries[] |
  [.relativePath, .sha256] | @tsv
' "$FIXTURE_ROOT/generator-contract.json" |
while IFS=$'\t' read -r relative_path expected_sha256; do
  actual_sha256="$(sha256sum "$FIXTURE_ROOT/$relative_path" | cut -d' ' -f1)"
  test "$actual_sha256" = "$expected_sha256"
done
```

For each selected entry, compare the file's SHA-256 with the manifest entry
before copying it. Reject the run if an entry is outside
`application-inputs/`, contains `..`, is under `staged-responses/`,
`evaluator-only/`, or `replay-only/`, names an answer key, or is not present in
the package manifest. Reject the run if any package-directory file other than
the package `manifest.json` and the selected declared files would be copied.
Keep the generator receipt and the selected manifest hash with the operator
record; the receipt is evidence of generation, not an authorization to load
protected artifacts.

## 3. Copy only the approved initial inputs

Copy into the deployed application's document-storage root using the
deployment-specific storage operation. The following example uses the
development file-storage root; an Azure deployment must perform the equivalent
scoped copy without broad recursive upload:

```bash
export APP_DOCUMENT_ROOT=/secure/deployed-state/documents
mkdir -p "$APP_DOCUMENT_ROOT/application-inputs/package-001"
mkdir -p "$APP_DOCUMENT_ROOT/application-inputs/reference-data"
install -D "$FIXTURE_ROOT/generator-contract.json" \
  "$APP_DOCUMENT_ROOT/generator-contract.json"
install -D "$SELECTED_MANIFEST" \
  "$APP_DOCUMENT_ROOT/application-inputs/package-001/manifest.json"
install -D "$FIXTURE_ROOT/application-inputs/reference-data/requirements.json" \
  "$APP_DOCUMENT_ROOT/application-inputs/reference-data/requirements.json"

jq -r '.pathBoundaries.selectedInitialInputManifest.entries[].relativePath' \
  "$FIXTURE_ROOT/generator-contract.json" |
while IFS= read -r relative_path; do
  case "$relative_path" in
    application-inputs/*) ;;
    *) echo "selected path is outside application-inputs: $relative_path" >&2; exit 1 ;;
  esac
  install -D "$FIXTURE_ROOT/$relative_path" "$APP_DOCUMENT_ROOT/$relative_path"
done

find "$APP_DOCUMENT_ROOT/application-inputs/package-001" \
  -type f ! -name manifest.json -printf '%f\n' | sort \
  > "$LOAD_ROOT/copied-package-files.txt"
jq -r '.manifest[].fileName' "$SELECTED_MANIFEST" | sort \
  > "$LOAD_ROOT/declared-package-files.txt"
diff -u "$LOAD_ROOT/declared-package-files.txt" \
  "$LOAD_ROOT/copied-package-files.txt"
```

After the copy, enumerate the copied package directory and compare it with the
selected manifest. Any undeclared file is a load failure. Do not copy
`staged-responses/`, `evaluator-only/`, `replay-only/`, answer keys, or the
generator's `reproducibility-receipt.json` into application document storage.

## 4. Reviewer authentication and authoritative decision smoke check

Obtain the reviewer credential through the deployment's approved
authentication path. Do not reuse the mock-partner identity. The API's
authenticated scope must contain the selected `run`, `airline`, `aircraft`,
and `lease` values, with a reviewer subject such as
`subject=reviewer-001`:

```bash
export REVIEWER_AUTH="Bearer run=$RUN_ID;airline=<airline-id>;aircraft=<aircraft-id>;lease=<lease-id>;subject=reviewer-001"
```

Create the package submission body from the selected application-input
manifest. This constructs the event locally and does not read
`replay-only/events.json`:

```bash
mkdir -p "$LOAD_ROOT"
jq --arg eventId "EVT-$RUN_ID-INITIAL" \
   --arg correlationId "CORR-$RUN_ID-INITIAL" '
  {
    event: {
      schemaVersion: .schemaVersion,
      eventId: $eventId,
      type: "package.submitted",
      runId: .runId,
      caseId: .caseId,
      airlineId: .airlineId,
      aircraftId: .aircraftId,
      leaseId: .leaseId,
      occurredAt: .submittedAt,
      scenarioEffectiveAt: .scenarioEffectiveAt,
      correlationId: $correlationId,
      payload: { packageId: .packageId }
    },
    package: .
  }
' "$SELECTED_MANIFEST" > "$LOAD_ROOT/package-request.json"

curl --silent --show-error --fail-with-body \
  -X POST "$API_BASE_URL/api/packages" \
  -H "Authorization: $REVIEWER_AUTH" \
  -H "Content-Type: application/json" \
  --data-binary @"$LOAD_ROOT/package-request.json" \
  > "$LOAD_ROOT/load-response.json"

jq -e '.operationId and .caseId and .receiptId' "$LOAD_ROOT/load-response.json"
export OPERATION_ID="$(jq -er '.operationId' "$LOAD_ROOT/load-response.json")"
export CASE_ID="$(jq -er '.caseId' "$LOAD_ROOT/load-response.json")"
export LOAD_RECEIPT_ID="$(jq -er '.receiptId' "$LOAD_ROOT/load-response.json")"
```

The authoritative load outcome is HTTP `202` with `operationId`, `caseId`, and
`receiptId`. Record `LOAD_RECEIPT_ID`; do not substitute a locally generated
identifier. Start and poll the returned operation using the reviewer
credential:

```bash
curl --silent --show-error --fail-with-body \
  -X POST "$API_BASE_URL/api/operations/$OPERATION_ID/process" \
  -H "Authorization: $REVIEWER_AUTH" \
  -H "Content-Type: application/json" \
  > "$LOAD_ROOT/process-response.json"

curl --silent --show-error --fail-with-body \
  "$API_BASE_URL/api/operations/$OPERATION_ID" \
  -H "Authorization: $REVIEWER_AUTH" \
  > "$LOAD_ROOT/operation-status.json"
jq -e '.operationId == $id and .runId == $run and .status == "complete"' \
  --arg id "$OPERATION_ID" --arg run "$RUN_ID" \
  "$LOAD_ROOT/operation-status.json"
```

Fetch the server-owned case summary and use its current `caseRevision`,
`investigation.basisId`, and open review task IDs. Do not calculate any of
these values from fixture data in a script or browser. Select one open task
whose permitted decisions include `needs_evidence`, then submit an explicit
review decision with the returned revision:

```bash
curl --silent --show-error --fail-with-body \
  "$API_BASE_URL/api/cases/$CASE_ID" \
  -H "Authorization: $REVIEWER_AUTH" > "$LOAD_ROOT/case-before-review.json"
export CASE_REVISION="$(jq -er '.caseRevision' "$LOAD_ROOT/case-before-review.json")"
export FINDING_ID="$(jq -er '.openReviewTasks[0].findingId' "$LOAD_ROOT/case-before-review.json")"
export BASIS_ID="$(jq -er '.openReviewTasks[0].basisId' "$LOAD_ROOT/case-before-review.json")"

jq -n --arg finding "$FINDING_ID" --arg basis "$BASIS_ID" '
  { findingId: $finding, basisId: $basis, decision: "needs_evidence",
    reason: "Controlled deployment smoke decision recorded by the reviewer." }
' > "$LOAD_ROOT/review-command.json"
curl --silent --show-error --fail-with-body \
  -X POST "$API_BASE_URL/api/cases/$CASE_ID/reviews" \
  -H "Authorization: $REVIEWER_AUTH" \
  -H "If-Match: \"$CASE_REVISION\"" \
  -H "Content-Type: application/json" \
  --data-binary @"$LOAD_ROOT/review-command.json" \
  > "$LOAD_ROOT/review-response.json"
jq -e '.reviewId and .findingId == $finding and .basisId == $basis' \
  --arg finding "$FINDING_ID" --arg basis "$BASIS_ID" \
  "$LOAD_ROOT/review-response.json"

curl --silent --show-error --fail-with-body \
  "$API_BASE_URL/api/cases/$CASE_ID" \
  -H "Authorization: $REVIEWER_AUTH" > "$LOAD_ROOT/case-after-review.json"
jq -e '.caseId == $case and .caseRevision > ($revision | tonumber)' \
  --arg case "$CASE_ID" --arg revision "$CASE_REVISION" \
  "$LOAD_ROOT/case-after-review.json"
```

The authoritative reviewer outcome is HTTP `200` with a server-issued
`reviewId`, matching finding and basis IDs, followed by the case read above
showing a `caseRevision` greater than the value sent in `If-Match`. Record the
`reviewId` and the post-decision `caseRevision`. A review decision is not a
case acceptance.

## 5. Mock-partner authentication and scoped response smoke check

Use a separate mock-partner credential with the same selected resource scope
and the exact subject `mock-partner`. A reviewer credential must not be used
for `/api/partner-responses`, and a mock-partner credential must not be used
for the reviewer decision:

```bash
export MOCK_PARTNER_AUTH="Bearer run=$RUN_ID;airline=<airline-id>;aircraft=<aircraft-id>;lease=<lease-id>;subject=mock-partner"
```

With the reviewer credential, dispatch one eligible evidence request to the
mock inbox:

```bash
curl --silent --show-error --fail-with-body \
  -X POST "$API_BASE_URL/api/mock-inbox/dispatch" \
  -H "Authorization: $REVIEWER_AUTH" \
  -H "Content-Type: application/json" \
  --data '{}' > "$LOAD_ROOT/dispatch-response.json"
jq -e '.status == "delivered" and .requestId and .inboxItemId' \
  "$LOAD_ROOT/dispatch-response.json"
export REQUEST_ID="$(jq -er '.requestId' "$LOAD_ROOT/dispatch-response.json")"
```

Read the inbox with the mock-partner credential and verify that the returned
item is in the selected run/case scope and has `recipientRef` equal to
`mock-partner-inbox`. Do not accept an undelivered, out-of-scope, or
non-authoritative recipient item:

```bash
curl --silent --show-error --fail-with-body \
  "$API_BASE_URL/api/mock-inbox" \
  -H "Authorization: $MOCK_PARTNER_AUTH" > "$LOAD_ROOT/inbox-response.json"
jq -e --arg run "$RUN_ID" --arg request "$REQUEST_ID" '
  any(.[]; .requestId == $request and .context.runId == $run and
    .recipientRef == "mock-partner-inbox" and .deliveredAt != null)
' "$LOAD_ROOT/inbox-response.json"
```

Use the selected run's approved staged response manifest only as the body of
the response submission. Do not copy its file or manifest into initial
application storage, do not add an authoritative `requestId` to its manifest,
and do not use evaluator-only data or an answer key. Build the response event
with the authoritative `requestId` returned by the inbox:

```bash
export RESPONSE_MANIFEST="$FIXTURE_ROOT/staged-responses/package-002/manifest.json"
jq -e --arg run "$RUN_ID" \
  '.runId == $run and .packageId == "PKG-0002"' \
  "$RESPONSE_MANIFEST"
jq --arg request "$REQUEST_ID" \
   --arg eventId "EVT-$RUN_ID-PARTNER" \
   --arg correlationId "CORR-$RUN_ID-PARTNER" '
  {
    event: {
      schemaVersion: .schemaVersion,
      eventId: $eventId,
      type: "partner.response.received",
      runId: .runId,
      caseId: .caseId,
      airlineId: .airlineId,
      aircraftId: .aircraftId,
      leaseId: .leaseId,
      occurredAt: .submittedAt,
      scenarioEffectiveAt: .scenarioEffectiveAt,
      correlationId: $correlationId,
      payload: { packageId: .packageId, requestId: $request }
    },
    package: .
  }
' "$RESPONSE_MANIFEST" > "$LOAD_ROOT/partner-response-request.json"

curl --silent --show-error --fail-with-body \
  -X POST "$API_BASE_URL/api/partner-responses" \
  -H "Authorization: $MOCK_PARTNER_AUTH" \
  -H "Content-Type: application/json" \
  --data-binary @"$LOAD_ROOT/partner-response-request.json" \
  > "$LOAD_ROOT/partner-response.json"
jq -e '.operationId and .caseId and .receiptId and .caseId == $case' \
  --arg case "$CASE_ID" "$LOAD_ROOT/partner-response.json"
export PARTNER_OPERATION_ID="$(jq -er '.operationId' "$LOAD_ROOT/partner-response.json")"
export PARTNER_RESPONSE_RECEIPT_ID="$(jq -er '.receiptId' "$LOAD_ROOT/partner-response.json")"
```

The authoritative mock-partner outcome is HTTP `202` with a server-issued
`receiptId`, `operationId`, and the selected `caseId`. Record that receipt.
The response must make the inbox request `responded` and queue reassessment;
it does not itself accept the case. Fetch the returned operation and the case
summary using their respective scoped credentials and record the observable
server statuses.

```bash
curl --silent --show-error --fail-with-body \
  "$API_BASE_URL/api/operations/$PARTNER_OPERATION_ID" \
  -H "Authorization: $MOCK_PARTNER_AUTH" \
  > "$LOAD_ROOT/partner-operation-status.json"
jq -e '.operationId == $id and .runId == $run and .status' \
  --arg id "$PARTNER_OPERATION_ID" --arg run "$RUN_ID" \
  "$LOAD_ROOT/partner-operation-status.json"

curl --silent --show-error --fail-with-body \
  "$API_BASE_URL/api/cases/$CASE_ID" \
  -H "Authorization: $REVIEWER_AUTH" \
  > "$LOAD_ROOT/case-after-partner-response.json"
jq -e '.caseId == $case and .runId == $run and
  any(.packageProcessing[]; .packageId == "PKG-0002")' \
  --arg case "$CASE_ID" --arg run "$RUN_ID" \
  "$LOAD_ROOT/case-after-partner-response.json"
```

## 6. Operator record

Create or append one record for the selected run. Before executing this
procedure, every result field must remain `PENDING_OPERATOR_EXECUTION`; never
pre-fill `PASS`, `complete`, `delivered`, `reviewed`, or `accepted`. Replace
pending values only with the observed response and HTTP status after the
operator has run the corresponding command.

```text
runIdentifier: PENDING_OPERATOR_EXECUTION
selectedManifestSha256: PENDING_OPERATOR_EXECUTION
deployedImageOrRevision: PENDING_OPERATOR_EXECUTION
loadReceiptId: PENDING_OPERATOR_EXECUTION
reviewId: PENDING_OPERATOR_EXECUTION
reviewCaseRevision: PENDING_OPERATOR_EXECUTION
mockDispatchStatus: PENDING_OPERATOR_EXECUTION
mockInboxItemId: PENDING_OPERATOR_EXECUTION
partnerResponseReceiptId: PENDING_OPERATOR_EXECUTION
smokeOutcome: PENDING_OPERATOR_EXECUTION
```

After execution, set `smokeOutcome` to `PASS` only when every required
authoritative receipt and server outcome above was captured, or to `FAIL` with
the failing command, HTTP status, correlation ID, and selected run recorded.
Do not record a successful outcome based on the fixture contents, a local
calculation, or a UI message without the corresponding server response.
