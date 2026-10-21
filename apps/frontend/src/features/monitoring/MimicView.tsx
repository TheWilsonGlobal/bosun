import { memo } from 'react';
import { cn } from '@/lib/utils';
import type { MonitoringStore } from './store';
import type { MimicDoc, MimicShape } from './types';
import { Widget } from './Widgets';

// The engine-room drawing is data-driven: shapes come from seed/mimic.json, styled by role with theme tokens, and are
// drawn once. Live widgets are separate, memoised components placed over them. The whole shape layer gets a subtle
// turbulence displacement so it reads as hand-drawn; widgets stay crisp.

const FILL: Record<string, string> = {
  frame: 'fill-transparent stroke-border',
  panel: 'fill-muted/40 stroke-border',
  equipment: 'fill-secondary stroke-foreground/60',
  cylinder: 'fill-card stroke-primary',
  exhaust: 'stroke-warning/70',
  pipe: 'stroke-primary/60',
};
const STROKE_WIDTH: Record<string, number> = {
  frame: 2,
  panel: 1.5,
  equipment: 2.5,
  cylinder: 2,
  exhaust: 6,
  pipe: 4,
};

function ShapeLabel({
  x,
  y,
  text,
  anchor = 'middle',
  size = 11,
}: {
  x: number;
  y: number;
  text: string;
  anchor?: 'start' | 'middle' | 'end';
  size?: number;
}) {
  return (
    <text
      x={x}
      y={y}
      fontSize={size}
      textAnchor={anchor}
      className="fill-muted-foreground font-medium"
    >
      {text}
    </text>
  );
}

function ShapeView({ shape }: { shape: MimicShape }) {
  const cls = cn(FILL[shape.role] ?? 'fill-transparent stroke-border');
  const sw = STROKE_WIDTH[shape.role] ?? 1.5;
  switch (shape.type) {
    case 'rect': {
      const { x = 0, y = 0, width = 0, height = 0 } = shape;
      const labelAtTop = shape.role === 'panel';
      return (
        <g data-shape={shape.id} data-role={shape.role}>
          <rect
            x={x}
            y={y}
            width={width}
            height={height}
            rx={shape.rx}
            className={cls}
            strokeWidth={sw}
          />
          {shape.label &&
            (labelAtTop ? (
              <ShapeLabel x={x + 10} y={y + 17} text={shape.label} anchor="start" size={12} />
            ) : shape.role === 'cylinder' ? (
              <ShapeLabel x={x + width / 2} y={y + height - 8} text={shape.label} size={12} />
            ) : (
              <ShapeLabel
                x={x + 12}
                y={y + height - 10}
                text={shape.label}
                anchor="start"
                size={12}
              />
            ))}
        </g>
      );
    }
    case 'circle': {
      const { cx = 0, cy = 0, r = 0 } = shape;
      return (
        <g data-shape={shape.id} data-role={shape.role}>
          <circle cx={cx} cy={cy} r={r} className={cls} strokeWidth={sw} />
          {shape.label && <ShapeLabel x={cx} y={cy + 4} text={shape.label} size={12} />}
        </g>
      );
    }
    case 'line': {
      const { x1 = 0, y1 = 0, x2 = 0, y2 = 0 } = shape;
      return (
        <g data-shape={shape.id} data-role={shape.role}>
          <line
            x1={x1}
            y1={y1}
            x2={x2}
            y2={y2}
            className={cls}
            strokeWidth={sw}
            strokeLinecap="round"
          />
          {shape.label && (
            <ShapeLabel x={(x1 + x2) / 2} y={Math.min(y1, y2) - 8} text={shape.label} size={10} />
          )}
        </g>
      );
    }
    case 'text': {
      const size = shape.fontSize ?? 12;
      return (
        <text
          data-shape={shape.id}
          data-role={shape.role}
          x={shape.x}
          y={shape.y}
          fontSize={size}
          fontWeight={shape.role === 'title' ? 700 : 400}
          className={shape.role === 'title' ? 'fill-foreground' : 'fill-muted-foreground'}
        >
          {shape.text}
        </text>
      );
    }
    default:
      return null;
  }
}

const ShapeLayer = memo(function ShapeLayer({ shapes }: { shapes: MimicShape[] }) {
  return (
    <g filter="url(#mimic-rough)" strokeLinejoin="round" strokeLinecap="round">
      {shapes.map((s) => (
        <ShapeView key={s.id} shape={s} />
      ))}
    </g>
  );
});

export function MimicView({ doc, store }: { doc: MimicDoc; store: MonitoringStore }) {
  const { width, height } = doc.viewBox;
  return (
    <svg
      role="img"
      aria-label={doc.title}
      viewBox={`0 0 ${width} ${height}`}
      className="h-auto w-full"
      data-testid="mimic-svg"
    >
      <title>{doc.title}</title>
      <defs>
        <filter id="mimic-rough" x="-2%" y="-2%" width="104%" height="104%">
          <feTurbulence
            type="fractalNoise"
            baseFrequency="0.035"
            numOctaves="2"
            seed="7"
            result="n"
          />
          <feDisplacementMap in="SourceGraphic" in2="n" scale="2.4" />
        </filter>
      </defs>
      <ShapeLayer shapes={doc.shapes} />
      {doc.bindings.map((b) => (
        <Widget key={b.id} store={store} binding={b} info={b.tag ? doc.tags[b.tag] : undefined} />
      ))}
    </svg>
  );
}
