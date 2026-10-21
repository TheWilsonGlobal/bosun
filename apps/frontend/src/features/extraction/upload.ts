export const MAX_UPLOAD_BYTES = 20 * 1024 * 1024;

export const formatBytes = (n: number) =>
  n >= 1024 * 1024
    ? `${(n / (1024 * 1024)).toFixed(1)} MB`
    : `${Math.max(1, Math.round(n / 1024))} KB`;

/** Client-side checks before any bytes are sent. The server re-checks everything (magic bytes, page count). */
export function validateFile(file: { name: string; size: number }): string | null {
  if (!/\.pdf$/i.test(file.name))
    return `"${file.name}" is not a PDF. Only .pdf files can be read.`;
  if (file.size === 0) return `"${file.name}" is empty.`;
  if (file.size > MAX_UPLOAD_BYTES)
    return `"${file.name}" is ${formatBytes(file.size)}; the limit is 20 MB.`;
  return null;
}
