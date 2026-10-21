import { NUMERIC_FIELDS, type PatchBody } from './types';

const PLAIN_NUMBER = /^[+-]?(\d+\.?\d*|\.\d+)$/;

/**
 * Map what the user typed in a cell to the PATCH body. A plain decimal in a numeric column goes out as a number;
 * anything else goes out as typed, so the server (which owns the normalisation) can accept it or reject it with
 * invalid_field. An empty cell clears the value (null) - the server refuses that for the tag name.
 */
export function buildFieldPatch(field: string, text: string): PatchBody {
  const t = text.trim();
  if (t === '') return { fields: { [field]: null } };
  if (NUMERIC_FIELDS.has(field) && PLAIN_NUMBER.test(t)) return { fields: { [field]: Number(t) } };
  return { fields: { [field]: t } };
}

export const buildExcludePatch = (excluded: boolean): PatchBody => ({ excluded });
