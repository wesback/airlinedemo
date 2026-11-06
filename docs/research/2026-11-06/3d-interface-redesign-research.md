<!-- markdownlint-disable-file -->
# Task Research: 3D interface redesign

| Field | Value |
|---|---|
| Date | 2026-11-06 |
| Researcher | RPI Agent / rpi-research |
| Output mode | convergence |

## Executive Summary

The complaints are substantially reproduced. The opening camera overrides Overview with a
close avatar-follow pose; Overview then jumps to a hangar-wide distance. The rack is real but
remote, unlabelled and disconnected from the prominent workflow controls. Arrival works
only after dossier-guided travel. Aircraft and avatar detail is absent by construction.

Research completed on the current local branch with source tracing, a real WebGL browser
probe and 40 passing existing tests. The recommendation is a coherent presentation-space
redesign with one interaction/camera state, an entry-side data rack, explicit dossier and
arrival panels, and project-authored stylized aircraft and an articulated human.

This exceeds one pipeline story and changes approved product constraints. A human must
approve a PRD amendment through a merge before production implementation. The browser
probe used synthetic API responses, not a deployed authenticated server; no claims about
production performance, live documents or server integration are made.

## What You May Not Know

The existing tests deliberately replace the renderer or test separate controllers, so all
40 selected tests pass while the visible first frame remains unusable. The current approved
PRD itself requires the problematic radius-6 opening follow camera and capsule avatar.
This is partly a design-contract problem, not only an implementation regression.

## Findings

### F1: The opening camera hides the purpose of the scene

The page enables follow mode on entry and supplies a fixed radius-6 pose around the avatar,
overriding the scene's overview camera. The initial local rendering shows the capsule avatar
and empty floor, with zero visible aircraft labels. The overview then jumps to radius
154.434, showing the aircraft but reducing the human to a tiny marker. This reproduces the
reported loss of scale; changing only zoom sensitivity will not solve the composition.

* Questions: Q2
* Evidence state: evidence-backed finding
* Evidence: C1, C2, C3, C8
* Confidence and limits: high for the current branch; local rendering uses synthetic API
  responses and Microsoft Edge, not the deployed application.

### F2: The rack exists but is placed out of the user's natural path

The avatar starts at (-4, 20); the rack zone is at (-4, -19.5), 39.5 scene units away.
The regional aircraft footprint lies between them. The current successful test walks
19 left, 79 up and 16 right key steps merely to reach the rack zone. The rack has no
world-anchored text label in the production label overlay. Clicking its mesh is explicitly
excluded from scene picking. The visible dossier dialog opens only after entering the
zone or pressing E/Enter within it. A separate "Open data rack" control is buried in the
disclosure-based legacy controls and drives another route/walk state, not the visible scene.

* Questions: Q1
* Evidence state: evidence-backed finding
* Evidence: C1, C3, C4, C5, C6, C8
* Confidence and limits: high; the real dialog was reached in the browser probe.

### F3: Arriving without first choosing a dossier produces no arrival state

The stage walk handler checks arrival only when playGuideCaseId is set. Walking from
the start straight to the regional marker (-4, 9) produced no announcement, arrival panel
or selected-arrival state. In contrast, the dossier-guided walk did show an arrival toast,
selected the aircraft and offered "Open journey". The capability is not missing
universally; it is conditional in a way the interface does not explain.

* Questions: Q3
* Evidence state: evidence-backed finding
* Evidence: C1, C5, C8
* Confidence and limits: high. The runtime reproduced both conditions with the production
  component; opening the journey itself was represented by a callback, not a real server.

### F4: These are deliberately primitive meshes, not broken high-quality assets

The avatar is a capsule body, same-blue spherical head and yellow half-sphere hat,
with no limbs or human facial structure. Aircraft use a capsule fuselage, box wings
and fin, cylinder engines and global per-axis normalization to satisfy bounding dimensions.
The regional label names an E195-E2 while its construction uses high-wing/T-tail
proportions. The approved PRD specifies generic fictional aircraft and disallows
photorealistic models, GLBs and textures without amendment. Asset-loading tweaks cannot
recover detail that does not exist.

* Questions: Q4
* Evidence state: evidence-backed finding
* Evidence: C3, C7, C8, C10
* Confidence and limits: high for mesh construction; perceived quality remains a human
  design judgment, supported here by local screenshots and the user's report.

### F5: Controllers and tests can pass while visible controls do not operate the camera

The real scene forwards followPose as the camera authority whenever follow is enabled.
Keyboard camera controls update cameraState instead; followPose continues to supply
radius 6 and azimuth 0. Pointer handling distinguishes a click from a drag, but discards
drags over six pixels rather than applying orbit, and no wheel zoom handler is wired
in the adapter. The play-mode tests substitute a canvas-only scene, while camera tests
test a separate controller. All 40 selected existing tests pass despite the reproduced UX.

* Questions: Q2, Q5
* Evidence state: evidence-backed finding
* Evidence: C1, C2, C5, C9
* Confidence and limits: high from wiring; not a claim that the user's particular
  deployed build has identical handlers.

### F6: Dossiers are disconnected from real source coverage

Hangar entry creates every source row as "available"/coverage true without requesting
the source-summary endpoint. The visible dossier dialog omits the five source rows
entirely. Legacy controls use a different local route and avatar from the main scene.
A redesign needs one production state and real read-only summary data, not another
third presentation path.

* Questions: Q1, Q5
* Evidence state: evidence-backed finding
* Evidence: C1, C4, C6, C10
* Confidence and limits: high for current frontend behavior; server summary authorization
  still needs a bounded endpoint check before new requirements name exact error behavior.

## Recommendation and Alternatives

Select a polished, stylized aviation presentation space, retaining Three.js/React Three
Fiber, synthetic cases, server authority, keyboard equivalence and the existing journey.
Do not treat this as flight simulation or promise photorealism.

The opening should be a composed working view: the entry-side rack, a readable human,
and aircraft context visible together. A fleet strip makes all three aircraft immediately
reachable. An explicit Overview fits the entire hangar; its human is necessarily small at
consistent world scale, so a labelled locator remains legible. Walk and Inspect are
separate, named views with appropriate framing. Do not solve scale by stretching the human
or aircraft.

Use one camera authority with aspect-aware framing from actual bounds, bounded zoom,
pointer drag, visible controls and keyboard equivalents. One walk/route/selection state
must back the canvas, dossier controls and non-3D UI. Move the rack to the entrance instead
of requiring traversal around an aircraft to discover it. Give it a visible Data Room
label, recognizable vertically stacked five-source units, an explicit Open dossiers
button and a click target that opens the same panel.

The dossier panel shows server source summaries with unavailable states, and starts the
same route from every entry point. A proximity arrival panel works without a preselected
route, shows the case, owner, next action and real mapped evidence (or a clear empty state),
offers Inspect aircraft and Open journey, and never changes server workflow state.
Repeated movement within a zone must not repeatedly reopen a dismissed panel; leaving
and re-entering makes it eligible again.

Visual direction: tapered fuselage and nose/tail, swept tapered wings and tail surfaces,
readable cockpit/window forms, hollow-looking nacelles, believable gear, coherent materials,
and restrained selection outlines instead of oversized glowing rings. The fictional human
needs separate head/neck/torso/pelvis, paired arms/hands/legs/boots, a face/hair silhouette,
workwear and correctly fitting hard hat, consistent physical scale and facing direction.
Use project-authored code geometry with the existing untextured pipeline for this amendment;
human visual sign-off is essential because mesh-name tests cannot establish visual quality.
Any later GLB/textured art direction needs a distinct rights, loader and CSP decision.

| Option | Benefits | Costs and risks | Evidence | Disposition |
|---|---|---|---|---|
| Coherent stylized redesign using current renderer and authored geometry | Addresses workflow and art direction without a new engine or asset licence dependency | Several small stories and human visual approval | C1-C11 | Selected |
| Increase follow distance and add a rack button only | Small repair | Leaves primitive assets, duplicate states and guided-only arrival; not a thorough redesign | C1-C9 | Rejected as complete answer |
| Remove walking and use only a 3D selector | Less camera complexity | Removes the user's expected rack-to-aircraft journey | User request, C10 | Viable only with separate user direction |
| Textured GLB assets and photoreal presentation | Higher potential fidelity | Unselected asset rights, CSP/loading work, larger budgets, approved-scope conflict | C7, C10, C13 | Deferred |

What could change the result: an explicitly different art direction, approved asset source,
or a user decision to remove walking. None is needed to diagnose the current failures.

## Scope and Questions

* Goal: establish causes and a coherent redesign direction for the 3D interface.
* Audience: application owner and subsequent redesign implementation.
* In scope: frontend 3D scene, models, camera, rack/dossier workflow, navigation, aircraft arrival, existing tests and product requirements.
* Out of scope: deployment, backend feature changes, tracker mutations, remote publication, purchasing or importing unlicensed assets.
* Criteria: readable first frame, stable useful scale, visible actionable rack, discoverable dossiers, continuous navigation, explicit aircraft arrival, recognizable aircraft and human.
* Requested output: diagnosis, evidence, alternatives, and supported redesign direction.

| ID | Question | Source | Status |
|---|---|---|---|
| Q1 | Why is the rack/dossier/navigation workflow not discoverable? | User | Answered, F2/F6 |
| Q2 | What causes the camera scale and initial framing failures? | User | Answered, F1/F5 |
| Q3 | What happens when the user reaches an aircraft? | User | Answered, F3 |
| Q4 | What assets and rendering choices cause the visual-quality problems? | User | Answered, F4 |
| Q5 | Which redesign preserves useful behavior and fits repository approval requirements? | Agent | Answered, recommendation and C11 |

## Decisions and Feedback

| Group | Decision or feedback item | Status | Owner | Rationale | Evidence | Impact |
|---|---|---|---|---|---|---|
| D1 | Work end to end with agent-owned ordinary decisions | Confirmed | User | Selected end-to-end workflow | Conversation 2026-11-06 | Automatic progression subject to repository human gates |
| D2 | Do not assume the reported symptoms identify their root causes | Confirmed | Agent | Verify production path and runtime | User report | Evidence-led research |
| D3 | Keep renderer and project-authored untextured assets; redesign their construction and composition | Confirmed | Agent | Current stack can render; missing detail is authored, not downloaded | C7-C10, C13 | Avoid unsupported asset/CSP expansion |
| D4 | Unify interaction state and make walking optional, not remove it | Confirmed | Agent | User expects rack, route and aircraft discovery; current paths diverge | C1-C6 | Preserve requested journey with direct access |
| D5 | Treat large redesign as PRD amendment, not immediate source edits | Confirmed | Repository constraint | Approved defaults and validators must change together | C10-C11 | Human approval merge blocks implementation |
| D6 | Capture real-renderer evidence in acceptance, not only helper outputs | Confirmed | Agent | Existing passing tests do not establish camera composition | C5, C8-C9 | Test logic numerically and review named browser views |

No unresolved research choice needs user acknowledgment. Human product approval remains a
separate repository gate; it is not assumed from the automatic workflow selection.

## Risks and Open Questions

| Priority | Type | Item | Impact | Smallest evidence needed | Owner |
|---|---|---|---|---|---|
| H | Approval gate | Broad redesign changes approved PRD defaults and exceeds one story | Implementation cannot begin | Draft amendment, obtain human approval merge | Product owner |
| M | Verification limitation | Browser probe used synthetic API edges and local development assets | No deployed-host, CSP or evidence-content claim | Production-build rehearsal with committed synthetic cases after implementation | Implementer |
| M | Visual acceptance | Recognizable geometry is not the same as satisfactory art direction | Risk of repeating primitive visuals with more named meshes | Human review of opening, walk, aircraft inspect and human close-up views | Product owner |
| M | Further research | GLB or textured assets, if requested later | Rights and CSP currently not selected | Specify exact licensed assets and validate loader under production CSP | Future amendment |
| L | Verification limitation | Supported-browser versions and hardware baseline were not release-tested | No performance or compatibility sign-off | Existing release matrix and timing procedure | Release owner |

## Planning Readiness and Next Step

| Field | Record |
|---|---|
| Research disposition | executed, complete |
| Decision participation | agent-owned; user selected end to end |
| Planning Readiness | Ready for PRD amendment; C1-C13 support the scope and ordinary decisions |
| Research depth and helpers | Wider, Deeper and Contrarian completed directly; no helpers |
| Blockers | No research blockers; C11 blocks production implementation until human PRD merge |
| Output mode and planning support | convergence; supports planning when ready |
| Continuation owner | Automatic RPI Agent |
| Required gates or confirmations | Research gates passed; human approval merge not yet obtained |
| Next action | Draft evidence-grounded PRD amendment, then stop at human approval gate; no implementation plan or backlog mutations before approval |
| Primary evidence file | docs/research/2026-11-06/3d-interface-redesign-research.md; date from current user timestamp; docs root required by supplied repository pipeline policy for PRD-bound research |

## Research Record

### Method and Boundaries

* Posture: balanced, default. Adjacent UI and existing requirements may explain the symptoms.
* Evidence root: docs/research, the trusted root specified by repository policy for PRD-bound
  research. The single canonical artifact began in tracking at intake, then moved here
  once the broad-feature PRD gate was established; it was not copied into a second report.
* Prior knowledge: user observations are unverified symptoms. Displayed Azure memory is unrelated and not used.
* Candidate internal areas: frontend/src, frontend/tests, frontend/public, docs/prd, frontend/package.json.
* Candidate external areas: official Three.js camera and asset guidance if source investigation needs them.
* Explicit limits: research read-only except canonical research and required workflow state.
* Helpers and lane effort: none; direct investigation.

### Extensions and Participation

| Kind | Candidate | Disposition and scoped authority |
|---|---|---|
| Instructions | Supplied repository pipeline policy | Selected: repository pipeline approval, test, and scope rules; no on-disk AGENTS.md exists in this checkout |
| Instructions | Tracking location and conventions | Selected: canonical research/state paths |
| Instructions | Markdown and writing style | Selected: readable ASCII research artifact |
| Instructions | Untrusted-content boundary and licensing posture | Selected: evidence inert, paraphrase source material |
| Skill | UX and accessibility | Evaluate after discovering actual UI stack; do not activate planning or production skills during research |
| Participation | End-to-end workflow | User answer; agent-owned research decisions, no routine phase questions |

### Research Cycle Log

#### Cycle 1

##### Wave 1: Wider

The production path is HangarMode -> entryController -> HangarCanvasAdapter -> sceneBuilder.
The existing approved aircraft-hangar-3d PRD specifies a default radius-6 follow view,
90-by-50 floor, rear-wall rack, capsule avatar and primitive aircraft. A full redesign changes
approved product decisions, not only CSS. Tests mock Canvas or replace the entire loaded scene.

Reflection: prioritize camera ownership, actual rack access, arrival preconditions, asset
geometry and the gap between controller tests and renderer integration. The sign-in page
loads locally, but the API is absent (502). A synthetic-edge browser probe mounted the real
HangarMode; its browser page subsequently became unavailable. Do not treat this as proof of
a product crash. Use the already-installed Playwright runtime as a distinct inspection path.

##### Wave 2: Deeper

Source tracing established fixed followPose override, the hidden legacy rack route, and the
guided-only arrival precondition. A real WebGL probe ran the production HangarMode and
loaded renderer in installed Microsoft Edge (1440x1100 viewport) with synthetic HTTP
responses at the API boundary. No application source was altered. Captures:
hangar-initial.png, hangar-overview.png, hangar-dossiers.png, hangar-arrival.png in this
artifact's directory. No browser pixel or contrast measurement was performed.

Initial: avatar (-4,20), no visible aircraft labels. Overview: preset radius 154.434452,
all three aircraft labels visible. Unguided (-4,9): empty live region and no arrival.
Guided arrival: (-5.5,9), exact arrival message and Open journey button; zero captured
runtime page errors. The test-fixture data intentionally contains no evidence documents,
so absence of evidence documents in the panel is not used as a finding.

Reflection: the report is substantially reproduced; render graphs and interaction ownership
are the causes to address, not deployment or missing GLB downloads. The integrated browser
page became unavailable during the first probe; standalone installed Edge provided a
valid alternative. Playwright's cached Chromium/headless binaries were absent; no
dependencies were installed. Forty existing tests across seven files passed.

##### Wave 3: Contrarian

Challenged "the rack is missing" and "arrival never works". The scene builder includes the
rack and beacon, the browser reached its dialog, and guided arrival produced its exact
toast and selected object. A geometric ray to the rack centre from Overview hit RackFrame
first (distance 169.01), not an aircraft. Therefore geometric occlusion at the rack centre
in Overview is not established; poor placement, small scale, absent rack label and
overlay composition explain discoverability better. In the initial follow view it is out
of the useful view altogether.

Challenged "a new engine or GLB download is necessary". Existing code already renders
aircraft, rack and avatar; same-origin generated meshes avoid new licence/CSP contracts.
The current CSP lacks connect-src blob:, consistent with the earlier approved research
warning about embedded GLB image loading. No new external API facts are needed for the
selected untextured direction.

Challenged "make the character large in a whole-hangar overview". At consistent physical
scale a human is small next to a 90-unit floor. Distinct working/walk/inspect/overview
compositions plus labelled location, rather than unrestricted zoom or inconsistent mesh
scaling, address the actual task. The source-summary service already exists, so source
coverage should be wired rather than invented. Its role/scope checks remain server-owned.

Reflection: counter-evidence narrows claims but does not weaken the redesign direction.
The existing stack is viable; the camera contract, visibility hierarchy and workflow
composition need amendment.

##### Synthesis and Re-entry

| Material | Evidence | Disposition | Rationale | User effect |
|---|---|---|---|---|
| Opening and scale mismatch | C1-C3, C8-C10 | Accepted | Reproduced through real renderer | Separate deliberate camera compositions |
| Rack entirely absent | C3, C6-C8, C12 | Rejected | Geometry and reachable dialog exist | Repair discovery and state ownership |
| Arrival entirely absent | C1, C5, C8 | Rejected | Guided arrival works; unguided does not | Make proximity meaningful without route precondition |
| Higher-quality authored assets | C7-C10 | Accepted | Detail does not exist in current geometry | Require art direction and visual sign-off |
| New renderer/third-party assets required | C7, C10, C13 | Rejected | Current renderer works; asset expansion adds constraints | Retain stack for this amendment |
| Immediate large implementation | C10-C11 | Rejected | Human-approved PRD is prerequisite | Draft proposal before production edits |

Another complete cycle is not needed for this direction: all material diagnosis questions
have source/runtime evidence, credible counterexamples are resolved, and further renderer
or asset searches would not alter the selected no-new-engine/no-GLB boundary. Exact new
camera and layout values belong to the amendment, with numerical framing criteria rather
than treating old radius constants as visual proof.

### Evidence Log

| ID | Claim or finding | Source or location | Retrieved and version | Tool | Confidence | Notes |
|---|---|---|---|---|---|---|
| C1 | Page owns two walk/menu states and fixed follow pose; arrival requires active route | frontend/src/hangar/HangarMode.tsx `playFollowPose`, `walkHangarAvatar`, `arriveAtHangarAircraft`, `diagnosticsSection` | Not applicable | Source read | High | Trace of production composition |
| C2 | Follow overrides preset state; pointer drag has no orbit action | frontend/src/hangar/entryController.ts `loadHangarScene`; frontend/src/hangar/HangarCanvasAdapter.tsx `pointerProps`, `HangarDemandFrame` | Not applicable | Source read | High | Actual camera path, not helper behavior |
| C3 | Rack behind aircraft; start 39.5 units away | frontend/src/hangar/sceneManifest.json `layout`; frontend/src/hangar/hangarPlay.ts `createHangarPlayLayout` | Not applicable | Source read | High | One production layout |
| C4 | Legacy menu has an explicit opener but independent state | frontend/src/hangar/rackMenu.ts `HangarRackMenu`; frontend/src/hangar/GuideLineAdapter.tsx `HangarGuideLineAdapter` | Not applicable | Source read | High | Does not set page playRoute |
| C5 | Tests require long rack walk and mock visible renderer | frontend/tests/HangarPlayMode.test.tsx `Hangar play`; frontend/tests/hangarAppHarness.ts `stubCaseApi` | Not applicable | Source read and Vitest | High | Three play-mode tests included in 40 passing selected tests |
| C6 | Labels only aircraft/selected component; rack clicks are ignored | frontend/src/hangar/HangarCanvasAdapter.tsx `HangarLabelOverlay`, `raycastHangarSelection` | Not applicable | Source read | High | Beacon is not a text label |
| C7 | Primitive geometry and global normalization | frontend/src/hangar/sceneBuilder.ts `buildAircraft`, `buildHangarAircraftSceneGroups` | Not applicable | Source read | High | Geometry exists and renders |
| C8 | Symptoms reproduced locally, guided arrival works | Production HangarMode in installed Edge with synthetic API edges; four screenshot files beside this artifact | 2026-11-06; current branch | Playwright / image inspection | High | Not authenticated production-host or release performance evidence |
| C9 | Separate helper camera tests pass | frontend/tests/HangarCameraController.test.ts; frontend/tests/HangarFollowCamera.test.ts; frontend/tests/HangarSceneBuilder.test.ts; frontend/tests/HangarRackMenu.test.tsx; frontend/tests/HangarSelectionEvidence.test.tsx | Not applicable | Vitest | High | Seven files, 40 tests passed including C5 |
| C10 | Product contract fixes follow radius and limits asset approach | docs/prd/aircraft-hangar-3d.md `Follow camera`, `Data Room walk`, `Scope Boundaries`, `Scene rendering`; frontend/src/hangar/sceneDescriptor.ts `validateHangarAircraftDescriptor` | Not applicable | Source read | High | Approved v0.4.1 contract; exact coordinates pinned in validator |
| C11 | Broad features need PRD approval merge before implementation | Supplied repository pipeline policy, `Big features: PRD first` | Not applicable | Session policy | High | Human approval cannot be replaced by end-to-end automation; not an on-disk AGENTS.md citation |
| C12 | Rack centre not occluded by an aircraft in Overview | frontend/src/hangar/sceneBuilder.ts with owned camera at 3*sqrt(2650), 45 degrees and aspect 1060/858 | 2026-11-06 | Real Three.js ray probe in Edge | High | First ray hit RackFrame at 169.01; no pixels sampled |
| C13 | Current asset budget and CSP remain constraints | frontend/src/hangar/buildBudgets.ts `validateHangarBuildBudgets`; backend/AirlineDemo.Api/Security/TransportSecurity.cs `ContentSecurityPolicy`; docs/research/2026-10-23/hangar-scene-rendering-research.md `Evidence` | Not applicable | Source read | High | 1,572,864 gzip JS bytes; 15,728,640 asset bytes; no connect-src blob: |
| C14 | Summary endpoint exists and enforces roles/scope | backend/AirlineDemo.Api/SourceSystems/SourceSummaryEndpoints.cs `Get`; backend/AirlineDemo.Api.Tests/SourceSummaryEndpointTests.cs `SummaryReturnsExactVersionedContractForFiveSources`; frontend/src/hangar/sourceSummary.ts `projectHangarSourceSummary` | Not applicable | Source read | High | Reuse read-only endpoint and validator; do not infer coverage |

#### Contradictions and Conflicts

* Geometric rack occlusion in Overview was a candidate, not an accepted cause; C12 rejects
  it at the centre ray. Actual discovery failure remains supported by C1/C3/C6/C8.
* Real manufacturer display names in the page do not make current generic geometry
  engineering-accurate. Do not promise manufacturer fidelity in a fictional demonstrator.

### Artifact Self-Check

* [x] Reader-first sections explain findings, decisions, alternatives and limits.
* [x] Every question is answered and material findings carry evidence state and confidence.
* [x] Evidence identifiers point to real source symbols or explicitly qualified runtime probes.
* [x] Wider, Deeper and Contrarian waves, reflections and re-entry decision are recorded.
* [x] Method, roots, controls, extensions and agent-owned participation are recorded.
* [x] Recommendation is supported; alternatives have explicit dispositions.
* [x] Research readiness is separate from the human implementation approval gate.
* [x] No helper was implied, no secrets recorded, no application source changed.
* [x] Screenshot evidence was visually inspected; no automated pixel/contrast claims made.
* Checked sections: all user-facing sections, cycle log, evidence, contradiction handling.
* Limited sections: deployed-host integration, production CSP rehearsal, release performance
  and real evidence-content display were not exercised and are not claimed.
