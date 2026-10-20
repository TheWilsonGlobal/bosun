import { useRef, useState, type KeyboardEvent } from 'react';
import { cn } from '@/lib/utils';
import { parseLimitInput, parseNumberInput, type ParseResult } from '@/lib/pendingEdits';
import type { AlarmLimitValue, SafetyClass } from '@/lib/api/types';
import { SAFETY_CLASSES } from '@/lib/api/types';

const viewClass =
  'flex h-8 w-full items-center rounded px-1.5 text-left text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring';
const inputClass =
  'h-8 w-full rounded border border-ring bg-background px-1.5 text-sm outline-none ring-2 ring-ring/40 aria-invalid:border-destructive aria-invalid:ring-destructive/40';

interface TextCellProps {
  value: string | null;
  label: string;
  modified: boolean;
  disabled?: boolean;
  numeric?: boolean;
  onCommit: (next: string | null) => void;
}

/** Click or Enter to edit, Enter/blur to keep, Escape to cancel. Numeric cells reject non-numbers inline. */
export function TextCell({ value, label, modified, disabled, numeric, onCommit }: TextCellProps) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);
  const cancelled = useRef(false);

  const begin = () => {
    cancelled.current = false;
    setDraft(value ?? '');
    setError(null);
    setEditing(true);
  };

  const commit = () => {
    const parsed: ParseResult<string | null> = numeric
      ? parseNumberInput(draft)
      : { ok: true, value: draft.trim() === '' ? null : draft.trim() };
    if (!parsed.ok) {
      setError(parsed.error);
      return false;
    }
    setEditing(false);
    if (parsed.value !== (value === '' ? null : value)) onCommit(parsed.value);
    return true;
  };

  if (!editing) {
    return (
      <button
        type="button"
        className={cn(
          viewClass,
          numeric && 'justify-end tabular-nums',
          modified && 'bg-warning/25 font-medium',
        )}
        disabled={disabled}
        onClick={begin}
        aria-label={`${label}: ${value ?? 'empty'}. Edit`}
        title={modified ? 'Pending edit' : undefined}
      >
        <span className="truncate">
          {value ?? <span className="text-muted-foreground">-</span>}
        </span>
      </button>
    );
  }
  return (
    <div className="relative">
      <input
        autoFocus
        className={cn(inputClass, numeric && 'text-right tabular-nums')}
        value={draft}
        aria-label={label}
        aria-invalid={!!error}
        inputMode={numeric ? 'decimal' : 'text'}
        onChange={(e) => {
          setDraft(e.target.value);
          setError(null);
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit();
          if (e.key === 'Escape') {
            cancelled.current = true;
            setEditing(false);
          }
        }}
        onBlur={() => {
          if (!cancelled.current && !commit()) setEditing(false);
        }}
      />
      {error && (
        <span
          role="alert"
          className="bg-destructive absolute top-full left-0 z-30 rounded px-1.5 py-0.5 text-xs text-white"
        >
          {error}
        </span>
      )}
    </div>
  );
}

export function SafetyCell({
  value,
  label,
  modified,
  onCommit,
}: {
  value: SafetyClass;
  label: string;
  modified: boolean;
  onCommit: (next: SafetyClass) => void;
}) {
  const [editing, setEditing] = useState(false);
  if (!editing) {
    return (
      <button
        type="button"
        className={cn(
          viewClass,
          modified && 'bg-warning/25 font-medium',
          value !== 'NONE' && 'text-destructive font-semibold',
        )}
        onClick={() => setEditing(true)}
        aria-label={`${label}: ${value}. Edit`}
      >
        {value}
      </button>
    );
  }
  return (
    <select
      autoFocus
      className={inputClass}
      aria-label={label}
      value={value}
      onChange={(e) => {
        setEditing(false);
        if (e.target.value !== value) onCommit(e.target.value as SafetyClass);
      }}
      onBlur={() => setEditing(false)}
      onKeyDown={(e) => e.key === 'Escape' && setEditing(false)}
    >
      {SAFETY_CLASSES.map((c) => (
        <option key={c} value={c}>
          {c}
        </option>
      ))}
    </select>
  );
}

export function formatLimit(l: AlarmLimitValue | null): string {
  return l ? `${l.value} / ${l.delayS}s` : '';
}

/** Alarm limit cell: value + delay. Clearing the value removes the limit. */
export function LimitCell({
  value,
  label,
  modified,
  onCommit,
}: {
  value: AlarmLimitValue | null;
  label: string;
  modified: boolean;
  onCommit: (next: AlarmLimitValue | null) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [v, setV] = useState('');
  const [d, setD] = useState('');
  const [error, setError] = useState<string | null>(null);
  const box = useRef<HTMLDivElement>(null);

  const begin = () => {
    setV(value ? String(value.value) : '');
    setD(value ? String(value.delayS) : '');
    setError(null);
    setEditing(true);
  };
  const commit = () => {
    const parsed = parseLimitInput(v, d, value);
    if (!parsed.ok) {
      setError(parsed.error);
      return;
    }
    setEditing(false);
    onCommit(parsed.value);
  };
  const onKey = (e: KeyboardEvent) => {
    if (e.key === 'Enter') commit();
    if (e.key === 'Escape') setEditing(false);
  };

  if (!editing) {
    return (
      <button
        type="button"
        className={cn(viewClass, 'tabular-nums', modified && 'bg-warning/25 font-medium')}
        onClick={begin}
        aria-label={`${label}: ${value ? `${value.value}, delay ${value.delayS} s` : 'not set'}. Edit`}
      >
        {value ? (
          <span className="truncate">
            {value.value} <span className="text-muted-foreground">/ {value.delayS}s</span>
          </span>
        ) : (
          <span className="text-muted-foreground">-</span>
        )}
      </button>
    );
  }
  return (
    <div
      ref={box}
      className="relative flex gap-1"
      onBlur={(e) => {
        if (!box.current?.contains(e.relatedTarget as Node | null)) commit();
      }}
    >
      <input
        autoFocus
        className={cn(inputClass, 'w-1/2 text-right tabular-nums')}
        aria-label={`${label} value`}
        aria-invalid={!!error}
        placeholder="value"
        value={v}
        onChange={(e) => {
          setV(e.target.value);
          setError(null);
        }}
        onKeyDown={onKey}
      />
      <input
        className={cn(inputClass, 'w-1/2 text-right tabular-nums')}
        aria-label={`${label} delay in seconds`}
        aria-invalid={!!error}
        placeholder="delay s"
        value={d}
        onChange={(e) => {
          setD(e.target.value);
          setError(null);
        }}
        onKeyDown={onKey}
      />
      {error && (
        <span
          role="alert"
          className="bg-destructive absolute top-full left-0 z-30 rounded px-1.5 py-0.5 text-xs text-white"
        >
          {error}
        </span>
      )}
    </div>
  );
}
