# Demo case DEMO-CASE-01

Synthetic demo case for the aircraft return-readiness journeys (DD-005, DD-018; PRD `docs/prd/aircraft-return-readiness.md`).

Every name, serial number, registration, approval number, work order and date here is fictional. No document is a real record.

## Case subjects

| Item | Identifier | Notes |
|------|------------|-------|
| Aircraft | Displayed as MOCK-AC-001; stored as SYN-AC-001 (registration SYN-001) | Fictional narrowbody |
| Engine | EN-SYN-7781 (model DEMO-ENG-A) | Installed on MOCK-AC-001, position 1 |
| Engine LLP | Engine disk, S/N SYN-LLP-0001 | Installed in EN-SYN-7781; back-to-birth history missing; later contradictory record |
| Component A | Fuel pump, S/N CMP-A-0001 | Complete history |

## Scenario

- Scenario clock: `scenarioNow` 2025-01-10. Return date: `returnDate` 2025-10-10, nine months later.
- Story: nine months before return, one engine LLP history gap (DOC-04 has no back-to-birth history for SYN-LLP-0001), one routine request (DOC-09 partner acknowledgement links back to DOC-03), and one ambiguous engine serial (DOC-05 reads EN-SYN-7718 for the engine EN-SYN-7781).
- The stored aircraft and engine identifiers keep their `SYN-` shapes (SYN-AC-001, EN-SYN-7781); only the display label is MOCK-AC-001.
- All case data and documents are synthetic.

## Documents

| Id | File | Type | Situation | What it shows |
|----|------|------|-----------|---------------|
| DOC-01 | DOC-01-release-aircraft-baseline.pdf | Certificate of release to service (aircraft) | baseline-complete | Complete aircraft release with every required field |
| DOC-02 | DOC-02-shop-visit-engine-baseline.pdf | Engine shop-visit report | baseline-complete | Complete engine record with time and cycle history |
| DOC-03 | DOC-03-release-component-a-baseline.pdf | Component release certificate | baseline-complete | Complete component release with back-to-birth history attached |
| DOC-04 | DOC-04-release-llp-missing-history.pdf | Engine life-limited part release certificate | missing-required-history | Engine LLP release certificate that lacks the required back-to-birth history |
| DOC-05 | DOC-05-shop-visit-engine-ambiguous-id.pdf | Engine shop-visit report | ambiguous-identity | ESN one digit away from DOC-02's engine, with identical hours and cycles |
| DOC-06 | DOC-06-corrupt-release-aircraft.pdf | Certificate of release to service (aircraft) | corrupt-file | Copy of DOC-01 with a damaged header and seeded byte flips |
| DOC-07 | DOC-07-truncated-release-aircraft.pdf | Certificate of release to service (aircraft) | truncated-file | Copy of DOC-01 cut off at 60 percent, so the end-of-file marker is missing |
| DOC-08 | DOC-08-contradictory-llp-disk-overhaul.pdf | Life-limited part overhaul record | contradictory-later-document | Later record for the same disk whose cycles since new are lower than DOC-04's |
| DOC-09 | DOC-09-partner-ack-component-a.pdf | Partner acknowledgement (mock partner) | partner-linked-response | Mock partner reply that links back to DOC-03 by serial number |

## Expected outcomes

`expected.json` holds, for each document, whether it is accepted, the rejection reason, the finding and the policy action. The generator writes it together with the PDFs.

## Regenerating

```sh
dotnet run -c Release --project backend/DemoCaseGenerator -- backend/AirlineDemo.Api.Tests/Fixtures/DemoCase
```

The generator uses fixed values and a fixed seed only, with no clock or randomness. `DemoCaseTests` regenerates the case into a temporary folder and fails unless every file is byte-identical to the committed one. Any change to the case therefore needs a new commit that the product owner reviews.
