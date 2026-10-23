# Hangar scene rendering: research for HG v0.4.0

Date: 2026-10-23. Scope: bounded research for the Aircraft Hangar 3D PRD amendment v0.4.0
(`docs/prd/aircraft-hangar-3d.md`). Evidence root: `docs`. Primary sources are the pinned packages installed from
`frontend/package-lock.json` (`three` 0.180.0, `@react-three/fiber` 9.8.1), the default branch at `6029b5c`, and the two
web references listed under E-7 and E-8.

## Questions

| ID | Question | Why it could change requirements |
|---|---|---|
| Q-1 | What does the delivered Hangar mode render today? | Decides whether the amendment adds scene requirements or only polish. |
| Q-2 | Can embedded GLB textures load in the supported browsers under the current CSP? | Decides whether textured models need a CSP change or a different asset strategy. |
| Q-3 | Can the scene's content, layout and pointer selection be tested in Vitest without WebGL? | DD-012 limits automated tests to logic; untested rendering is how the gap in Q-1 passed every gate. |
| Q-4 | Does the R3F `camera` prop follow later controller state? | Decides whether presets and orbit move the rendered camera. |
| Q-5 | What render-loop and context-loss behaviour does R3F give by default? | Decides performance and resilience requirements on the integrated-GPU demo baseline. |
| Q-6 | Can text labels be positioned over the canvas under `style-src 'self'`? | Decides whether status labels can be DOM text rather than textures. |

## Evidence

| ID | Source | Finding |
|---|---|---|
| E-1 | `frontend/src/hangar/entryController.ts` (`loadHangarScene`) at `6029b5c` | The canvas holds one `boxGeometry` of 3 x 1.5 x 1.5 with an ambient and a directional light on a flat `#dbe7f0` background. No aircraft, floor, rack or avatar geometry is drawn and no object is selectable by pointer. |
| E-2 | `frontend/src/assets/hangar/*.glb` at `6029b5c` | The five GLB files are 180 to 468 bytes. Their JSON chunks hold named nodes only (`AircraftA`, `EngineA`, `ComponentA1`, `ComponentA2` and the rack and avatar nodes) and no `meshes`. No source file imports them and the production build contains no `.glb`. |
| E-3 | `frontend/src/App.tsx` and `frontend/tests/hangarAppHarness.ts` at `6029b5c` | Hangar mode requests neither `GET /api/v1/cases` nor `GET /api/v1/cases/{caseId}/scene`; `hangarContractVersion` defaults to the constant `1`. `getHangarEntryDecision` treats an absent `contractVersion` as supported. The scene envelope validator (`sceneProjection.ts`) and refresh logic (`sceneRefresh.ts`) exist but are fed only by tests. |
| E-4 | `three/examples/jsm/loaders/GLTFLoader.js` lines 2676-2682 and 3390; `three/src/loaders/ImageBitmapLoader.js` line 167 | When `createImageBitmap` exists (current Chrome and Edge), GLTFLoader uses `ImageBitmapLoader`, turns each embedded image into a `blob:` URL with `URL.createObjectURL`, and loads it with `fetch()`. |
| E-5 | `backend/AirlineDemo.Api/Security/TransportSecurity.cs` line 14 | The served policy has no `connect-src`, so `fetch()` falls back to `default-src 'self'`, which does not allow `blob:`. |
| E-6 | `@react-three/fiber/dist/events-9ce18a08.esm.js` lines 1057 and 1471-1497; `declarations/src/core/store.d.ts` line 23 | The default `frameloop` is `'always'`; `'demand'` and `'never'` are supported. The `camera` prop creates and configures the default camera once (`state.camera === configuration.camera` never holds for an options object), so later `camera` prop values do not move the camera. |
| E-7 | `grep -c webglcontextlost` over `@react-three/fiber/dist/*.js` returns 0; MDN connect-src reference (`https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy/connect-src`) | R3F registers no WebGL context-loss handler; the application must handle `webglcontextlost`. MDN lists `fetch()` among the requests `connect-src` restricts, confirming E-4 and E-5. |
| E-8 | MDN style-src reference (`https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy/style-src`) | "Styles properties that are set directly on the element's style property will not be blocked." React `style` props use the element's style property, so DOM labels can be positioned with `transform` under `style-src 'self'`. |
| E-9 | Node probe against the pinned `three` 0.180.0 build (no WebGL, no DOM) | Built a group of `CapsuleGeometry`, `BoxGeometry` and an invisible `SphereGeometry` mesh; `Box3.setFromObject` returned the expected size; `Raycaster.intersectObjects` from a `PerspectiveCamera` hit the invisible mesh and returned its `userData.objectId`; `Vector3.project` returned normalized device coordinates. Output: `{"revision":"180","size":[22,4.3,23.6],"hit":"EngineA-hit","hitId":"SYN-CP-A-ENG-001","projectedNdc":[0,0]}`. |

## Answers

- **Q-1:** Hangar mode renders no aircraft, floor, rack or avatar and reads no scene data (E-1, E-2, E-3). The delivered
  stories passed because their criteria tested controllers and validators against a stubbed canvas (DD-012), and real
  rendering was left to the manual release check RC-006.
- **Q-2:** No. Embedded textures would fail in Chrome and Edge under the current policy (E-4, E-5, E-7). Either
  `connect-src` must allow `blob:` or the first release ships no textures.
- **Q-3:** Yes. three.js scene graphs, bounding boxes, raycasts and projections run in Node without WebGL (E-9), so
  geometry, layout, palette use and pointer picking can be tested against the real scene builder. Only pixels need a browser.
- **Q-4:** No. The camera must be set from controller state by a camera rig inside the canvas (E-6).
- **Q-5:** R3F renders continuously by default and does not handle context loss (E-6, E-7).
- **Q-6:** Yes (E-8).

## Gaps and dispositions

| Gap ID | Evidence IDs | Affected targets | Disposition in HG v0.4.0 |
|---|---|---|---|
| G-1 | E-1, E-2 | FR-002, FR-016, FR-017, AC-021, RC-004 | Code-built geometry from three.js built-in geometries (DD-029); exact layout and dimensions (Scene layout); FR-022 and AC-048 to AC-050. |
| G-2 | E-1, E-9 | DD-012 | A renderer-independent scene builder returns the three.js scene graph and is tested without WebGL (DD-030). |
| G-3 | E-3 | FR-002, FR-006 | FR-023 and AC-051, AC-052: Hangar mode reads the CP case list and scene envelopes at entry; an absent `contractVersion` is unsupported. |
| G-4 | E-4, E-5, E-7 | DD-010, FR-011, FR-012 | The first release ships no GLB and no texture (DD-029), so no CSP change is needed. A later textured GLB needs a reviewed amendment that adds `connect-src 'self' blob:`. |
| G-5 | E-6 | FR-004, FR-021 | Camera rig requirement FR-024 and AC-054. |
| G-6 | E-6, E-7 | NFR-001, NFR-002, NFR-006 | Render settings and context-loss handling FR-025, AC-055, AC-056; `sceneLoadDurationMs` ends at the first rendered frame (AC-057). |
| G-7 | E-8, E-9 | FR-002, FR-003, DD-004, DD-005 | DOM labels anchored by projection and enlarged pick targets (DD-032, DD-033, AC-050, AC-053). |
