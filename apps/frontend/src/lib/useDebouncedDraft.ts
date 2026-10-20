import { useEffect, useRef, useState } from 'react';

/**
 * A text input that shows what the user types immediately but only commits it (e.g. into the URL) after a
 * pause. While no draft is pending it mirrors `value`, so external changes (Clear filters, back button) show up.
 */
export function useDebouncedDraft(value: string, commit: (next: string) => void, delay = 300) {
  const [draft, setDraft] = useState<string | null>(null);
  const commitRef = useRef(commit);
  useEffect(() => {
    commitRef.current = commit;
  });
  useEffect(() => {
    if (draft === null) return;
    const t = setTimeout(() => {
      commitRef.current(draft);
      setDraft(null);
    }, delay);
    return () => clearTimeout(t);
  }, [draft, delay]);
  return [draft ?? value, setDraft] as const;
}
