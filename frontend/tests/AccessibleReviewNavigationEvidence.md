# Accessible review navigation evidence

Scope: bounded evidence only; this report does not claim formal WCAG conformance.

| Check | Environment and journey | Outcome |
|---|---|---|
| Application build | `npm run build`; Vite 8.3.4 | Passed |
| Automated review journey | Vitest 5.0.1 and Testing Library in jsdom; sign-in, service states, case loading/error, trace loading/loaded/not-found/error, decision reason, permitted decisions, and queue/activity refresh after recording a decision | Passed; all 140 frontend tests in 37 files pass (`npx vitest run`, Vitest 5.0.1) |
| Browser keyboard focus visibility | Browser: unavailable; Chromium, Chrome, and Firefox executables are not installed. Keyboard-only traversal was exercised by `userEvent.tab()` in jsdom. | Not run in a browser. Focus order passed the automated test; visibility of the painted focus indicator on each control is unverified. |
| Screen-reader smoke check | Assistive technology: unavailable; NVDA and Orca are not installed. Intended journey: keyboard evidence review through the trace, then recording a permitted decision and hearing the queue and activity updates. | Not run; no screen-reader outcome is claimed. |
| Contrast inspection | Searched `frontend/src` for stylesheet files (`.css`, `.scss`); none are present. | No token-based color pairs or contrast exceptions were available to inspect. Browser-default colors and focus-indicator contrast remain unmeasured. |

The browser keyboard-visibility and screen-reader checks remain outstanding; the automated results above do not substitute for those checks.
