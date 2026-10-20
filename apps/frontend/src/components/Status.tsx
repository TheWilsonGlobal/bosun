import { cn } from '@/lib/utils';

export function Status({
  children,
  title,
  tone,
}: {
  children: React.ReactNode;
  title?: string;
  tone?: 'error';
}) {
  return (
    <div
      role={tone === 'error' ? 'alert' : 'status'}
      className={cn(
        'text-muted-foreground flex h-full flex-col items-center justify-center gap-2 p-8 text-center text-sm',
        tone === 'error' && 'text-destructive',
      )}
    >
      {title && <p className="text-base font-semibold">{title}</p>}
      <p>{children}</p>
    </div>
  );
}
