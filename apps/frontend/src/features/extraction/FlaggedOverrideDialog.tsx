import { useState } from 'react';
import { ShieldAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import type { ExtractedRow } from './types';

/**
 * Shown when the server refuses flagged_rows_need_override. The acknowledgement is never pre-checked, and the
 * component is remounted for every refusal (keyed by the caller), so a previous tick cannot carry over.
 * Row text is document content and is rendered as plain text only.
 */
export function FlaggedOverrideDialog({
  rows,
  busy,
  onConfirm,
  onCancel,
}: {
  rows: ExtractedRow[];
  busy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const [reviewed, setReviewed] = useState(false);
  return (
    <Dialog open onOpenChange={(open) => !open && !busy && onCancel()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <ShieldAlert className="text-destructive size-5" aria-hidden /> Accept flagged rows?
          </DialogTitle>
          <DialogDescription>
            These rows contain instruction-like text from the document (suspected injection). They
            are accepted only if you confirm that you checked them.
          </DialogDescription>
        </DialogHeader>
        <ul aria-label="Flagged rows" className="max-h-60 list-disc overflow-auto pl-5 text-sm">
          {rows.map((r) => (
            <li key={r.id}>
              Row {r.seq}
              {r.fields.tagName?.value ? ` (${String(r.fields.tagName.value)})` : ''}
              {r.snippet ? `: ${r.snippet}` : ''}
            </li>
          ))}
        </ul>
        <div className="flex items-center gap-2">
          <Checkbox
            id="override-reviewed"
            checked={reviewed}
            onCheckedChange={(c) => setReviewed(c === true)}
          />
          <label htmlFor="override-reviewed" className="text-sm">
            I reviewed these rows; accept them anyway
          </label>
        </div>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
          <Button type="button" onClick={onConfirm} disabled={!reviewed || busy}>
            {busy ? 'Accepting...' : 'Accept flagged rows'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
