import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import type { AlarmLevel, AlarmLimitValue, Tag, UpdateField, Violation } from '@/lib/api/types';
import {
  setFieldEdit,
  setLimitEdit,
  type PendingEdit,
  type PendingEdits,
} from '@/lib/pendingEdits';
import { useProject } from '@/features/project/ProjectContext';

// Pending (not yet submitted) grid edits for the selected project, kept in sessionStorage so a reload does not
// lose them, plus the rule violations of the last validated change set, which the grid highlights.

interface Highlight {
  changeSetId: number;
  byTag: ReadonlyMap<string, Violation[]>;
}

interface PendingContextValue {
  edits: PendingEdits;
  list: PendingEdit[];
  editField: (tag: Tag, field: UpdateField, value: string | null) => void;
  editLimit: (tag: Tag, level: AlarmLevel, value: AlarmLimitValue | null) => void;
  discard: (key: string) => void;
  clear: () => void;
  highlight: Highlight | null;
  setHighlight: (changeSetId: number, violations: readonly Violation[]) => void;
  clearHighlight: () => void;
}

const PendingContext = createContext<PendingContextValue | null>(null);
const storageKey = (projectId: number) => `bosun.pending.${projectId}`;

function load(projectId: number | null): PendingEdits {
  if (projectId === null) return new Map();
  try {
    const raw = sessionStorage.getItem(storageKey(projectId));
    if (!raw) return new Map();
    return new Map((JSON.parse(raw) as PendingEdit[]).map((e) => [e.key, e]));
  } catch {
    return new Map();
  }
}

export function PendingProvider({ children }: { children: ReactNode }) {
  const { project } = useProject();
  const projectId = project?.id ?? null;
  const [state, setState] = useState<{ projectId: number | null; edits: PendingEdits }>(() => ({
    projectId,
    edits: load(projectId),
  }));
  const [highlight, setHighlightState] = useState<Highlight | null>(null);

  // Switching project swaps the edit set (edits are per project).
  if (state.projectId !== projectId) {
    setState({ projectId, edits: load(projectId) });
    setHighlightState(null);
  }
  const edits = state.edits;

  useEffect(() => {
    if (projectId === null || state.projectId !== projectId) return;
    if (edits.size === 0) sessionStorage.removeItem(storageKey(projectId));
    else sessionStorage.setItem(storageKey(projectId), JSON.stringify([...edits.values()]));
  }, [edits, projectId, state.projectId]);

  const update = useCallback((fn: (e: PendingEdits) => PendingEdits) => {
    setState((s) => ({ ...s, edits: fn(s.edits) }));
  }, []);

  const value = useMemo<PendingContextValue>(
    () => ({
      edits,
      list: [...edits.values()],
      editField: (tag, field, v) => update((e) => setFieldEdit(e, tag, field, v)),
      editLimit: (tag, level, v) => update((e) => setLimitEdit(e, tag, level, v)),
      discard: (key) =>
        update((e) => {
          const next = new Map(e);
          next.delete(key);
          return next;
        }),
      clear: () => update(() => new Map()),
      highlight,
      setHighlight: (changeSetId, violations) => {
        const byTag = new Map<string, Violation[]>();
        for (const v of violations) {
          if (!v.tagName) continue;
          byTag.set(v.tagName, [...(byTag.get(v.tagName) ?? []), v]);
        }
        setHighlightState(byTag.size ? { changeSetId, byTag } : null);
      },
      clearHighlight: () => setHighlightState(null),
    }),
    [edits, highlight, update],
  );

  return <PendingContext.Provider value={value}>{children}</PendingContext.Provider>;
}

export function usePending(): PendingContextValue {
  const ctx = useContext(PendingContext);
  if (!ctx) throw new Error('usePending must be used inside <PendingProvider>');
  return ctx;
}
