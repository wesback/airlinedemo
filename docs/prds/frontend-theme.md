# Frontend Theme Specification - Altivane Aviation Capital

Source: supplied design image, 11 September 2026.
Status: visual direction for the synthetic demonstration.

## Design intent

The interface should feel like an aviation asset-management control room for executives: calm, precise, trustworthy and evidence-led. It should not look like a generic chatbot, developer console or consumer airline booking site.

## Visual language

- Generous white space and a pale, almost-white background.
- Deep navy for headings, primary text, outlines and high-trust controls.
- Bright aviation green for progress, positive status, approved actions and emphasis.
- Pale mint/green for backgrounds, paths and low-emphasis highlights.
- Thin navy linework and restrained rounded corners.
- Minimal shadows; use elevation only to separate evidence cards and review panels.
- Use aircraft/route/progress motifs sparingly. Do not decorate operational evidence with unnecessary illustration.

## Initial design tokens

These are approximate tokens sampled or inferred from the supplied image. Confirm against the original PowerPoint theme or logo asset before production use.

```css
:root {
  --aviation-navy: #062B55;
  --aviation-navy-strong: #032247;
  --aviation-green: #16B364;
  --aviation-green-dark: #07894A;
  --aviation-mint: #E5F7EF;
  --aviation-mint-strong: #C8F0DE;
  --aviation-sky: #EAF5FA;
  --aviation-ink: #19324D;
  --aviation-muted: #60748A;
  --aviation-line: #B8C7D5;
  --aviation-surface: #FFFFFF;
  --aviation-background: #F8FBFC;
  --aviation-warning: #B7791F;
  --aviation-warning-surface: #FFF7E6;
  --aviation-danger: #B42318;
  --aviation-danger-surface: #FEECEB;
  --focus-ring: #0B84F3;
  --radius-card: 12px;
  --radius-control: 7px;
}
```

## Typography

Use the PowerPoint theme font if available. If not, use a modern sans-serif fallback:

```css
font-family: "Segoe UI", Arial, sans-serif;
```

- Page title: deep navy, bold, large but compact.
- Section headings: deep navy, semibold.
- Primary status and outcome values: navy or green, never colour alone.
- Supporting text: navy/ink with muted text for metadata.
- Avoid all-caps body copy; reserve uppercase for small labels.

## Component direction

### Shell

Top bar with Altivane mark on the left, environment/fixture indicator and user role on the right. The product name should be descriptive, for example **Transition Readiness**.

### Aircraft readiness header

Show:

- Fictional aircraft identifier.
- Airline/lease scope.
- Planned return date.
- Current readiness state.
- Last evidence package received.

Use a thin green progress/route motif as a secondary visual, not as the sole status indicator.

### Evidence cards

White cards with thin navy or neutral borders. Include document ID, version, page, source system, processing state and a clear **View evidence** action.

### Status badges

Use text plus icon:

- `Evidence complete`
- `Evidence request permitted`
- `Internal review required`
- `Processing blocked`
- `Awaiting response`
- `Accepted for mock checklist`

Never display a generic `Airworthy` status in the demonstration.

### Review queue

Use green for permitted progress, amber for uncertainty or waiting, and red only for a confirmed system/security failure. The ambiguous serial-number case should be amber and explicitly say **Internal review required**.

### Mock partner inbox

Use the same shell and cards, but visually distinguish the simulated external view with a small **Synthetic partner portal** label. The outgoing message should use neutral, non-accusatory wording.

## Demonstration styling

The live demo should make the control boundary visually obvious:

1. Green route: clearly missing evidence -> bounded request permitted.
2. Amber route: unreadable/ambiguous identity -> internal review.
3. No red alarm for uncertainty; uncertainty is a controlled state, not proof of noncompliance.

Show the source evidence and policy basis beside the action. The audience should understand why the system acted or stopped without reading implementation details.

## Accessibility requirements

- Meet WCAG-oriented contrast for text and controls.
- Do not rely on green, amber or red alone; include labels and icons.
- Provide visible keyboard focus using `--focus-ring`.
- Preserve readable text at presentation projection scale.
- Avoid thin green text on white for small labels.
- Keep motion optional and non-essential.

## Asset guidance

Use the original transparent Altivane logo from the PowerPoint or source asset when available. Do not crop the logo from a screenshot for production UI. The supplied image may be used as a visual reference and presentation background, but the frontend should use an optimised SVG/PNG asset.

Do not reuse the aircraft illustration as a background behind evidence text. Use it only for the empty state, landing state or presentation/demo cover if licensing and source ownership permit.
