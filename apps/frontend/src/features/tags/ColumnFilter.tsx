import { Filter } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { cn } from '@/lib/utils';

interface Props {
  label: string;
  options: readonly string[];
  selected: string[];
  onChange: (next: string[]) => void;
  /** Adds a "(none)" choice that maps to the literal value `none` the backend understands. */
  allowNone?: boolean;
}

export function ColumnFilter({ label, options, selected, onChange, allowNone }: Props) {
  const all = allowNone ? ['none', ...options] : [...options];
  const toggle = (value: string) =>
    onChange(selected.includes(value) ? selected.filter((s) => s !== value) : [...selected, value]);
  const active = selected.length > 0;

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          className={cn('size-6', active && 'text-primary')}
          aria-label={`Filter ${label}${active ? ` (${selected.length} selected)` : ''}`}
        >
          <Filter className={cn('size-3.5', active && 'fill-current')} aria-hidden />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-56 p-2">
        <div className="mb-1 flex items-center justify-between px-1 text-xs font-medium">
          {label}
          <Button
            variant="link"
            size="sm"
            className="h-auto p-0 text-xs"
            disabled={!active}
            onClick={() => onChange([])}
          >
            Clear
          </Button>
        </div>
        <ul className="max-h-64 overflow-auto">
          {all.length === 0 && (
            <li className="text-muted-foreground px-1 py-2 text-xs">No values</li>
          )}
          {all.map((value) => {
            const id = `filter-${label}-${value}`;
            return (
              <li key={value} className="hover:bg-accent flex items-center gap-2 rounded px-1 py-1">
                <Checkbox
                  id={id}
                  checked={selected.includes(value)}
                  onCheckedChange={() => toggle(value)}
                />
                <label htmlFor={id} className="flex-1 cursor-pointer text-sm">
                  {value === 'none' ? '(none)' : value}
                </label>
              </li>
            );
          })}
        </ul>
      </PopoverContent>
    </Popover>
  );
}
