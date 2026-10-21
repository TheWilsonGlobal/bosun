import { useRef, useState, type DragEvent } from 'react';
import { FileUp, Loader2 } from 'lucide-react';
import { cn } from '@/lib/utils';
import { validateFile } from './upload';

interface Props {
  disabled?: boolean;
  busy?: boolean;
  /** Called with a file that passed the client-side checks. */
  onFile: (file: File) => void;
  /** A message to show under the zone (server error for the last upload). */
  error?: { title: string; message: string } | null;
}

/** Drag-and-drop target plus a real file input. Only one file at a time; checks .pdf and 20 MB before sending. */
export function UploadZone({ disabled, busy, onFile, error }: Props) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  const [clientError, setClientError] = useState<string | null>(null);

  const take = (files: FileList | null) => {
    if (!files || files.length === 0) return;
    if (files.length > 1) {
      setClientError('Upload one PDF at a time.');
      return;
    }
    const file = files[0];
    const problem = validateFile(file);
    setClientError(problem);
    if (!problem) onFile(file);
    if (inputRef.current) inputRef.current.value = '';
  };

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setOver(false);
    if (!disabled && !busy) take(e.dataTransfer.files);
  };

  return (
    <div className="grid gap-2">
      <div
        data-testid="drop-zone"
        onDragOver={(e) => {
          e.preventDefault();
          if (!disabled) setOver(true);
        }}
        onDragLeave={() => setOver(false)}
        onDrop={onDrop}
        className={cn(
          'has-[input:focus-visible]:ring-ring flex flex-col items-center gap-2 rounded-lg border-2 border-dashed p-6 text-center text-sm has-[input:focus-visible]:ring-2',
          over && 'border-primary bg-primary/5',
          disabled && 'opacity-60',
        )}
      >
        {busy ? (
          <Loader2 className="size-6 animate-spin" aria-hidden />
        ) : (
          <FileUp className="text-muted-foreground size-6" aria-hidden />
        )}
        <p>
          Drop a customer I/O list PDF here, or{' '}
          <label
            htmlFor="document-file"
            className="text-primary cursor-pointer font-medium underline"
          >
            choose a file
          </label>
          .
        </p>
        <p className="text-muted-foreground text-xs">PDF only, at most 20 MB.</p>
        <input
          ref={inputRef}
          id="document-file"
          type="file"
          accept=".pdf,application/pdf"
          className="sr-only"
          disabled={disabled || busy}
          onChange={(e) => take(e.target.files)}
        />
      </div>
      {busy && (
        <p role="status" className="text-sm">
          Uploading...
        </p>
      )}
      {clientError && (
        <p role="alert" className="text-destructive text-sm">
          {clientError}
        </p>
      )}
      {error && !clientError && (
        <p role="alert" className="text-destructive text-sm">
          <span className="font-semibold">{error.title}. </span>
          {error.message}
        </p>
      )}
    </div>
  );
}
