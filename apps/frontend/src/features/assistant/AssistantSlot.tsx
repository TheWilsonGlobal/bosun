import { useSyncExternalStore, type ReactNode } from 'react';

// Extension point for the Phase 4 Assistant side panel. Nothing is built here on purpose: the future panel calls
// registerAssistantPanel(() => <AssistantPanel />) once (e.g. from main.tsx) and the shell renders it as a
// right-hand column.

type Render = () => ReactNode;
let render: Render | null = null;
const listeners = new Set<() => void>();

export function registerAssistantPanel(next: Render | null) {
  render = next;
  listeners.forEach((l) => l());
}

export function AssistantSlot() {
  const current = useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => render,
    () => null,
  );
  if (!current) return null;
  return (
    <aside
      aria-label="Assistant"
      className="bg-card hidden w-96 shrink-0 flex-col overflow-hidden border-l lg:flex"
    >
      {current()}
    </aside>
  );
}
