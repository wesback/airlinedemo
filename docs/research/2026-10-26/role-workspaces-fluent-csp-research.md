# Fluent UI React under a nonce-based CSP: research for role workspaces

Date: 2026-10-26. Scope: bounded research for `docs/prd/role-workspaces.md` (DD-002, DD-010, FR-016). Evidence root:
`docs`. Primary sources are the npm registry metadata and the published package contents of `@fluentui/react-components`
9.74.11, `@fluentui/react-provider` 9.22.22 and `@griffel/core` 1.21.4 (inspected with `npm pack`), and this repository
at the PRD branch.

## Questions

| ID | Question | Why it could change requirements |
|---|---|---|
| Q-1 | How does Fluent UI React v9 add its styles at runtime? | Decides whether the CSP `style-src 'self'` blocks it. |
| Q-2 | Can every `<style>` element Fluent creates carry a CSP nonce? | Decides whether a nonce, instead of `'unsafe-inline'`, is enough. |
| Q-3 | Is the current Fluent release compatible with React 19? | The client uses React 19.3.0. |
| Q-4 | How is `index.html` served today? | Decides where a per-response nonce can be written. |
| Q-5 | Which existing tests pin the CSP? | Repo rule: a story names the tests that assert the old value. |

## Evidence

| ID | Source | Finding |
|---|---|---|
| E-1 | `@griffel/core` 1.21.4 `src/renderer/createDOMRenderer.d.ts` | `createDOMRenderer(document, options)` accepts `styleElementAttributes?: Record<string, string>`, documented as "passed to the generated style elements. Is useful to set "nonce" attribute." |
| E-2 | `@griffel/core` 1.21.4 `src/renderer/getStyleSheetForBucket.js:72` | Each bucket `<style>` element is created with `...renderer.styleElementAttributes`. |
| E-3 | `@fluentui/react-provider` 9.22.22 `useFluentProvider.js:38,81` and `useFluentProviderThemeStyleTag.js:99` | The `FluentProvider` theme `<style>` tag (CSS variables) is created with the renderer's `styleElementAttributes`. |
| E-4 | `npm view @fluentui/react-components@9.74.11` | Licence MIT. Peer dependency `react >=16.14.0 <20.0.0`. Depends on `@fluentui/react-provider ^9.22.22` and `@griffel/react ^1.5.32`. `@fluentui/react-icons` latest is 2.0.344. |
| E-5 | `backend/AirlineDemo.Api/StaticClient.cs:13-39` | `UseDefaultFiles` and `UseStaticFiles` serve `index.html` for `/` as a static file; the fallback sends the same file unchanged for client routes. |
| E-6 | `backend/AirlineDemo.Api/Security/TransportSecurity.cs:13-15` | One constant CSP is added to every response. |
| E-7 | `backend/AirlineDemo.Api.Tests/TransportSecurityTests.cs:43-56`, `backend/AirlineDemo.Api.Tests/StaticClientTests.cs:144-148` | `Csp_matches_dd_028`, `Csp_matches_hangar_baseline` and `AssertSecurityHeaders` assert the constant CSP literally. |
| E-8 | `docs/research/2026-10-23/hangar-scene-rendering-research.md` E-8 | Style properties set through an element's `style` property are not blocked by `style-src`; React `style` props and Fluent positioning use it. |

## Answers

- Q-1: Griffel inserts rules into `<style>` elements it creates in `<head>` (E-2), and `FluentProvider` adds one more for theme variables (E-3). Under `style-src 'self'` both are blocked.
- Q-2: Yes. One `RendererProvider` with `createDOMRenderer(document, { styleElementAttributes: { nonce } })` covers Griffel's buckets and the `FluentProvider` theme tag (E-1 to E-3). A nonce is sufficient; `'unsafe-inline'` is not needed.
- Q-3: Yes, 9.74.11 accepts React 19 (E-4).
- Q-4: `index.html` is static (E-5). A nonce needs the server to write `index.html` per response for `/`, `/index.html` and the client-route fallback, with `Cache-Control: no-store`.
- Q-5: Three tests (E-7). API and other non-HTML responses can keep the existing CSP, so only the document-response assertion changes.

## Gaps and dispositions

| Finding | Disposition |
|---|---|
| Griffel and FluentProvider nonce support (Q-1, Q-2) | Adopted: DD-002, DD-010, FR-016. |
| Static `index.html` (Q-4) | Adopted: FR-016 renders it per response; AC-020. |
| CSP tests (Q-5) | Adopted: AC-020 and AC-021 name the tests and which assertions change. |
| A Fluent component that creates a `<style>` element outside the renderer | None found in the inspected packages. AC-022 is a unit test that fails when the rendered shell has a `<style>` element without the nonce. |

Planning blockers: None.
