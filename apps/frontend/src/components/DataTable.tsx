import { useState, useMemo, type ReactNode } from 'react';
import {
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  ChevronsRight,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export interface Column<T> {
  key: string;
  header: ReactNode;
  align?: 'left' | 'center' | 'right';
  width?: string | number;
  className?: string;
  headerClassName?: string;
  render?: (item: T, index: number) => ReactNode;
}

export interface DataTablePaginationProps {
  page: number;
  pageSize: number;
  total: number;
  pageSizeOptions?: number[];
  itemLabel?: string;
  onPageChange: (page: number) => void;
  onPageSizeChange?: (pageSize: number) => void;
}

export interface DataTableProps<T> {
  data: T[];
  columns: Column<T>[];
  keyExtractor?: (item: T, index: number) => string | number;
  rowAriaLabel?: (item: T, index: number) => string;
  caption?: string;
  className?: string;
  tableClassName?: string;
  rowClassName?: string | ((item: T, index: number) => string | undefined);
  striped?: boolean;
  pagination?: DataTablePaginationProps;
  clientPagination?: boolean;
  initialPageSize?: number;
  onRowClick?: (item: T, index: number) => void;
  emptyMessage?: ReactNode;
  headerRight?: ReactNode;
}

export function DataTable<T>({
  data,
  columns,
  keyExtractor,
  rowAriaLabel,
  caption,
  className,
  tableClassName,
  rowClassName,
  striped = true,
  pagination: controlledPagination,
  clientPagination = false,
  initialPageSize = 25,
  onRowClick,
  emptyMessage = 'No records found.',
  headerRight,
}: DataTableProps<T>) {
  // Client-side pagination state when clientPagination is active
  const [clientPage, setClientPage] = useState(1);
  const [clientPageSize, setClientPageSize] = useState(initialPageSize);

  const pagination = controlledPagination ?? (clientPagination
    ? {
        page: clientPage,
        pageSize: clientPageSize,
        total: data.length,
        pageSizeOptions: [10, 25, 50, 100],
        onPageChange: setClientPage,
        onPageSizeChange: (size: number) => {
          setClientPageSize(size);
          setClientPage(1);
        },
      }
    : undefined);

  // Compute displayed rows (if client pagination is active, slice data)
  const displayedData = useMemo(() => {
    if (clientPagination && pagination) {
      const start = (pagination.page - 1) * pagination.pageSize;
      return data.slice(start, start + pagination.pageSize);
    }
    return data;
  }, [data, clientPagination, pagination]);

  const total = pagination?.total ?? data.length;
  const page = pagination?.page ?? 1;
  const pageSize = pagination?.pageSize ?? data.length;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const fromItem = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const toItem = Math.min(total, page * pageSize);
  const itemLabel = pagination?.itemLabel ?? 'records';

  const defaultKeyExtractor = (item: T, index: number): string | number => {
    if (item && typeof item === 'object') {
      if ('id' in item && item.id != null) return String(item.id);
      if ('key' in item && item.key != null) return String(item.key);
    }
    return index;
  };

  const getKey = keyExtractor ?? defaultKeyExtractor;

  return (
    <div className={cn('flex flex-col gap-2.5', className)}>
      {/* Top Header info (if item range or custom header right is desired) */}
      {(total > 0 || headerRight) && (
        <div className="flex items-center justify-between text-xs text-muted-foreground px-1">
          <div>
            {total > 0 && pagination && (
              <span>
                Showing <strong className="text-foreground font-mono">{fromItem}</strong>–
                <strong className="text-foreground font-mono">{toItem}</strong> of{' '}
                <strong className="text-foreground font-mono">{total.toLocaleString()}</strong> {itemLabel}
              </span>
            )}
          </div>
          {headerRight && <div>{headerRight}</div>}
        </div>
      )}

      {/* Main Table Container */}
      <div className="overflow-x-auto rounded-md border bg-card shadow-2xs">
        <table className={cn('w-full text-sm', tableClassName)} aria-label={caption}>
          <thead className="bg-muted/70 text-left text-xs font-semibold text-muted-foreground border-b sticky top-0 z-10 select-none">
            <tr>
              {columns.map((col) => (
                <th
                  key={col.key}
                  scope="col"
                  style={col.width ? { width: col.width } : undefined}
                  className={cn(
                    'px-3 py-2.5 whitespace-nowrap',
                    col.align === 'center' && 'text-center',
                    col.align === 'right' && 'text-right',
                    col.headerClassName,
                  )}
                >
                  {col.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {displayedData.length === 0 ? (
              <tr>
                <td colSpan={columns.length} className="px-4 py-8 text-center text-muted-foreground text-sm">
                  {emptyMessage}
                </td>
              </tr>
            ) : (
              displayedData.map((item, index) => {
                const key = getKey(item, index);
                const isClickable = !!onRowClick;
                const customRowClass = typeof rowClassName === 'function' ? rowClassName(item, index) : rowClassName;

                return (
                  <tr
                    key={key}
                    tabIndex={isClickable ? 0 : undefined}
                    role={isClickable ? 'button' : undefined}
                    aria-label={rowAriaLabel ? rowAriaLabel(item, index) : undefined}
                    onClick={isClickable ? () => onRowClick(item, index) : undefined}
                    onKeyDown={
                      isClickable
                        ? (e) => {
                            if (e.key === 'Enter' || e.key === ' ') {
                              e.preventDefault();
                              onRowClick(item, index);
                            }
                          }
                        : undefined
                    }
                    className={cn(
                      'transition-colors text-xs',
                      // Alternating zebra striping
                      striped && (index % 2 === 1 ? 'bg-muted/20' : 'bg-card'),
                      isClickable && 'hover:bg-accent/50 cursor-pointer focus-visible:outline-none focus-visible:bg-accent/60',
                      !isClickable && 'hover:bg-muted/40',
                      customRowClass,
                    )}
                  >
                    {columns.map((col) => (
                      <td
                        key={col.key}
                        className={cn(
                          'px-3 py-2 align-middle',
                          col.align === 'center' && 'text-center',
                          col.align === 'right' && 'text-right',
                          col.className,
                        )}
                      >
                        {col.render
                          ? col.render(item, index)
                          : String((item as Record<string, unknown>)[col.key] ?? '-')}
                      </td>
                    ))}
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination Footer */}
      {pagination && (
        <nav
          aria-label="Pagination"
          className="flex flex-wrap items-center justify-between gap-2 pt-1 text-xs text-muted-foreground"
        >
          {/* Rows per page selector */}
          <div className="flex items-center gap-1.5">
            <span>Rows per page</span>
            <select
              aria-label="Rows per page"
              className="border-input bg-background h-8 rounded-md border px-2 text-foreground font-medium"
              value={pageSize}
              onChange={(e) => {
                const newSize = Number(e.target.value);
                if (pagination.onPageSizeChange) {
                  pagination.onPageSizeChange(newSize);
                } else {
                  pagination.onPageChange(1);
                }
              }}
            >
              {(pagination.pageSizeOptions ?? [10, 25, 50, 100]).map((opt) => (
                <option key={opt} value={opt}>
                  {opt}
                </option>
              ))}
            </select>
          </div>

          {/* Navigation Controls */}
          <div className="flex items-center gap-1">
            <Button
              variant="outline"
              size="icon"
              className="size-8"
              aria-label="First page"
              disabled={page <= 1}
              onClick={() => pagination.onPageChange(1)}
            >
              <ChevronsLeft className="size-4" />
            </Button>
            <Button
              variant="outline"
              size="icon"
              className="size-8"
              aria-label="Previous page"
              disabled={page <= 1}
              onClick={() => pagination.onPageChange(page - 1)}
            >
              <ChevronLeft className="size-4" />
            </Button>

            <span className="px-2.5 tabular-nums text-foreground font-medium">
              Page {page} of {totalPages}
            </span>

            <Button
              variant="outline"
              size="icon"
              className="size-8"
              aria-label="Next page"
              disabled={page >= totalPages}
              onClick={() => pagination.onPageChange(page + 1)}
            >
              <ChevronRight className="size-4" />
            </Button>
            <Button
              variant="outline"
              size="icon"
              className="size-8"
              aria-label="Last page"
              disabled={page >= totalPages}
              onClick={() => pagination.onPageChange(totalPages)}
            >
              <ChevronsRight className="size-4" />
            </Button>
          </div>
        </nav>
      )}
    </div>
  );
}
export default DataTable;
