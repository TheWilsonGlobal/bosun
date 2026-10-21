import type { Extraction, PatchResult } from './types';

/**
 * Merge a PATCH answer into the loaded extraction. Duplicate checks span rows, so the `alsoChanged` rows are
 * replaced too, and the summary counts come from the server (never recomputed here).
 */
export function applyPatchResult(ext: Extraction, res: PatchResult): Extraction {
  const changed = new Map([res.row, ...res.alsoChanged].map((r) => [r.id, r]));
  return {
    ...ext,
    rows: ext.rows.map((r) => changed.get(r.id) ?? r),
    summary: res.summary,
  };
}
