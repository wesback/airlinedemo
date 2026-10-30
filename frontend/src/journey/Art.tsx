import type { Actor, AircraftCategory } from './model';

export function AltivaneMark({ size = 36 }: { size?: number }) {
  return (
    <svg className="av-mark" width={size} height={size} viewBox="0 0 48 48" aria-hidden="true" focusable="false">
      <path className="av-mark-ring" d="M38.5 10.8A20 20 0 1 0 43.6 28" fill="none" strokeWidth="3.2" strokeLinecap="round" />
      <path className="av-mark-wing" d="M9 31.5c9.5-2.2 19.6-8.6 30.6-21.7l1.9 1.5C33 24.8 22.6 31.8 9.6 33.8z" />
      <path className="av-mark-swoosh" d="M14.5 36.5c8.8-1 17.4-4.6 26.2-12.3l1.2 1.6c-8.6 8.4-17.7 12.2-27 12.6z" />
    </svg>
  );
}

export function AltivaneLogo({ inverse = false }: { inverse?: boolean }) {
  return (
    <span className={`av-logo${inverse ? ' av-logo-inverse' : ''}`}>
      <AltivaneMark />
      <span className="av-logo-text">
        <span className="av-logo-word">ALTIVANE</span>
        <span className="av-logo-sub">Aviation Capital</span>
      </span>
    </span>
  );
}

type Dimensions = { length: number; radius: number; fin: number; engine: number };

const DIMENSIONS: Record<AircraftCategory, Dimensions> = {
  regional: { length: 250, radius: 13, fin: 2.7, engine: 0.95 },
  'narrow-body': { length: 300, radius: 15, fin: 2.5, engine: 1 },
  'wide-body': { length: 350, radius: 21, fin: 2.25, engine: 1.25 },
};

/** Side profile line-art of an airliner, drawn to relative scale per category. */
export function AircraftArt({ category, className = '', title }: { category: AircraftCategory; className?: string; title?: string }) {
  const { length: L, radius: r, fin, engine } = DIMENSIONS[category];
  const x0 = 20 + (350 - L) / 2;
  const cy = 72;
  const x = (f: number) => x0 + L * f;
  const top = cy - r;
  const bottom = cy + r;
  const body = [
    `M ${x0} ${cy + r * 0.25}`,
    `C ${x0} ${cy - r * 0.55} ${x0 + r * 1.2} ${top} ${x0 + r * 3.2} ${top}`,
    `L ${x(0.8)} ${top}`,
    `C ${x(0.9)} ${top} ${x(0.97)} ${cy - r * 0.95} ${x0 + L} ${cy - r * 0.62}`,
    `L ${x0 + L} ${cy - r * 0.2}`,
    `C ${x(0.9)} ${cy + r * 0.3} ${x(0.8)} ${bottom} ${x(0.7)} ${bottom}`,
    `L ${x0 + r * 2.4} ${bottom}`,
    `C ${x0 + r * 0.8} ${bottom} ${x0} ${cy + r * 0.8} ${x0} ${cy + r * 0.25}`,
    'Z',
  ].join(' ');
  const finTop = top - r * fin;
  const finPath = `M ${x(0.79)} ${top + 1} L ${x(0.9)} ${finTop} L ${x(0.965)} ${finTop} L ${x(0.985)} ${cy - r * 0.7} Z`;
  const stab = `M ${x(0.89)} ${cy - r * 0.45} L ${x(0.99)} ${cy - r * 0.62} L ${x(1.0)} ${cy - r * 0.45} L ${x(0.93)} ${cy - r * 0.25} Z`;
  const wing = `M ${x(0.36)} ${cy + r * 0.45} L ${x(0.6)} ${cy + r * 0.2} L ${x(0.63)} ${cy + r * 0.38} L ${x(0.41)} ${cy + r * 0.82} Z`;
  const ew = L * 0.13 * engine;
  const eh = r * 0.85 * engine;
  const ex = x(0.34);
  const ey = bottom - eh * 0.15;
  const windows: number[] = [];
  for (let wx = x0 + r * 4.4; wx < x(0.76); wx += category === 'wide-body' ? 9 : 8) windows.push(wx);
  const doorX = x0 + r * 3.4;
  return (
    <svg className={`av-aircraft ${className}`} viewBox="0 0 390 128" role={title ? 'img' : undefined} aria-label={title} aria-hidden={title ? undefined : true} focusable="false">
      <path className="av-aircraft-shadow" d={`M ${x(0.1)} 120 Q ${x(0.5)} 113 ${x(0.95)} 120 Z`} />
      <path className="av-aircraft-wing-far" d={wing} transform="translate(-6 -5)" />
      <path className="av-aircraft-fin" d={finPath} />
      <path className="av-aircraft-body" d={body} />
      <path className="av-aircraft-cheat" d={`M ${x0 + r * 2} ${cy + r * 0.32} L ${x(0.86)} ${cy + r * 0.05}`} />
      <path className="av-aircraft-stab" d={stab} />
      {windows.map((wx) => <circle key={wx} className="av-aircraft-window" cx={wx} cy={cy - r * 0.32} r={category === 'wide-body' ? 1.9 : 1.6} />)}
      <path className="av-aircraft-cockpit" d={`M ${x0 + r * 0.9} ${cy - r * 0.5} L ${x0 + r * 2.3} ${cy - r * 0.62} L ${x0 + r * 2.3} ${cy - r * 0.28} L ${x0 + r * 0.6} ${cy - r * 0.2} Z`} />
      <rect className="av-aircraft-door" x={doorX} y={cy - r * 0.62} width={r * 0.5} height={r * 1.05} rx="1.5" />
      <path className="av-aircraft-wing" d={wing} />
      <rect className="av-aircraft-engine" x={ex} y={ey} width={ew} height={eh} rx={eh / 2} />
      <rect className="av-aircraft-engine-lip" x={ex - 1} y={ey + 1} width={eh * 0.35} height={eh - 2} rx={eh * 0.17} />
      <g className="av-aircraft-tail-mark" transform={`translate(${x(0.885)} ${finTop + r * 0.5}) scale(${r / 30})`}>
        <path d="M2 22c6-1.5 12-5.5 19-13.5l1.2 1C17 18 11 22.4 2.4 23.4z" />
      </g>
    </svg>
  );
}

export function ActorGlyph({ actor, size = 16 }: { actor: Actor; size?: number }) {
  if (actor === 'ai') {
    return (
      <svg className="av-glyph av-glyph-ai" width={size} height={size} viewBox="0 0 16 16" aria-hidden="true" focusable="false">
        <path d="M8 1.5l1.5 4 4 1.5-4 1.5L8 12.5l-1.5-4-4-1.5 4-1.5z" />
        <path d="M13 10.5l.6 1.4 1.4.6-1.4.6-.6 1.4-.6-1.4-1.4-.6 1.4-.6z" />
      </svg>
    );
  }
  if (actor === 'human') {
    return (
      <svg className="av-glyph av-glyph-human" width={size} height={size} viewBox="0 0 16 16" aria-hidden="true" focusable="false">
        <circle cx="8" cy="5" r="2.8" />
        <path d="M2.5 14.2c.6-3.1 2.8-4.9 5.5-4.9s4.9 1.8 5.5 4.9z" />
      </svg>
    );
  }
  return (
    <svg className="av-glyph av-glyph-rules" width={size} height={size} viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path d="M8 1.8l1.2 1.6 2-.4.4 2 1.6 1.2-1 1.8 1 1.8-1.6 1.2-.4 2-2-.4L8 14.2l-1.2-1.6-2 .4-.4-2-1.6-1.2 1-1.8-1-1.8 1.6-1.2.4-2 2 .4z" />
      <circle className="av-glyph-hole" cx="8" cy="8" r="2.2" />
    </svg>
  );
}

export function ActorTag({ actor, label }: { actor: Actor; label: string }) {
  return (
    <span className={`av-actor av-actor-${actor}`}>
      <ActorGlyph actor={actor} size={14} />
      {label}
    </span>
  );
}

export function CheckIcon() {
  return (
    <svg className="av-check" width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path d="M3 8.5l3 3 7-7" fill="none" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

export function DocumentIcon() {
  return (
    <svg className="av-doc-icon" width="18" height="18" viewBox="0 0 20 20" aria-hidden="true" focusable="false">
      <path d="M5 2.5h7l3.5 3.5v11.5H5z" fill="none" strokeWidth="1.5" strokeLinejoin="round" />
      <path d="M12 2.5V6h3.5M7.5 10h5M7.5 13h5" fill="none" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  );
}
