import {
  useSyncExternalStore,
  useState,
  useEffect,
  useRef,
  useCallback,
  type ReactNode,
} from 'react';
import { GripVertical } from 'lucide-react';
import { cn } from '@/lib/utils';

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

const DEFAULT_WIDTH = 384; // w-96 = 24rem = 384px
const MIN_WIDTH = 280;
const MAX_WIDTH = 800;
const STORAGE_KEY = 'bosun.assistant.width';

export function AssistantSlot() {
  const current = useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => render,
    () => null,
  );

  const [width, setWidth] = useState(() => {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved) {
        const parsed = parseInt(saved, 10);
        if (!isNaN(parsed) && parsed >= MIN_WIDTH && parsed <= MAX_WIDTH) {
          return parsed;
        }
      }
    } catch {
      // ignore storage error
    }
    return DEFAULT_WIDTH;
  });

  const [isResizing, setIsResizing] = useState(false);
  const startXRef = useRef(0);
  const startWidthRef = useRef(width);

  const startResizing = useCallback((clientX: number) => {
    setIsResizing(true);
    startXRef.current = clientX;
    startWidthRef.current = width;
  }, [width]);

  useEffect(() => {
    if (!isResizing) return;

    const handleMouseMove = (e: MouseEvent) => {
      // Dragging to the left (decreasing clientX) increases panel width
      const delta = startXRef.current - e.clientX;
      const nextWidth = Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, startWidthRef.current + delta));
      setWidth(nextWidth);
    };

    const handleMouseUp = () => {
      setIsResizing(false);
      setWidth((finalWidth) => {
        try {
          localStorage.setItem(STORAGE_KEY, String(finalWidth));
        } catch {
          // ignore storage error
        }
        return finalWidth;
      });
    };

    const handleTouchMove = (e: TouchEvent) => {
      if (e.touches.length > 0) {
        const delta = startXRef.current - e.touches[0].clientX;
        const nextWidth = Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, startWidthRef.current + delta));
        setWidth(nextWidth);
      }
    };

    const handleTouchEnd = () => {
      setIsResizing(false);
      setWidth((finalWidth) => {
        try {
          localStorage.setItem(STORAGE_KEY, String(finalWidth));
        } catch {
          // ignore storage error
        }
        return finalWidth;
      });
    };

    document.body.style.userSelect = 'none';
    document.body.style.cursor = 'col-resize';

    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('mouseup', handleMouseUp);
    window.addEventListener('touchmove', handleTouchMove);
    window.addEventListener('touchend', handleTouchEnd);

    return () => {
      document.body.style.userSelect = '';
      document.body.style.cursor = '';
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
      window.removeEventListener('touchmove', handleTouchMove);
      window.removeEventListener('touchend', handleTouchEnd);
    };
  }, [isResizing]);

  const handleResetWidth = () => {
    setWidth(DEFAULT_WIDTH);
    try {
      localStorage.setItem(STORAGE_KEY, String(DEFAULT_WIDTH));
    } catch {
      // ignore storage error
    }
  };

  if (!current) return null;

  return (
    <aside
      aria-label="Assistant"
      style={{ width: `${width}px` }}
      className={cn(
        'bg-card relative hidden shrink-0 flex-col overflow-hidden border-l lg:flex',
        isResizing ? 'select-none' : 'transition-[width] duration-75'
      )}
    >
      {/* Draggable resize handle on the left edge */}
      <div
        role="separator"
        aria-orientation="vertical"
        aria-label="Resize Assistant panel"
        tabIndex={0}
        onKeyDown={(e) => {
          if (e.key === 'ArrowLeft') {
            setWidth((w) => {
              const next = Math.min(MAX_WIDTH, w + 20);
              try {
                localStorage.setItem(STORAGE_KEY, String(next));
              } catch {
                // ignore
              }
              return next;
            });
          } else if (e.key === 'ArrowRight') {
            setWidth((w) => {
              const next = Math.max(MIN_WIDTH, w - 20);
              try {
                localStorage.setItem(STORAGE_KEY, String(next));
              } catch {
                // ignore
              }
              return next;
            });
          }
        }}
        onMouseDown={(e) => {
          e.preventDefault();
          startResizing(e.clientX);
        }}
        onTouchStart={(e) => {
          if (e.touches.length > 0) {
            startResizing(e.touches[0].clientX);
          }
        }}
        onDoubleClick={handleResetWidth}
        title="Drag to resize Assistant panel (Double-click to reset to default)"
        className="group absolute top-0 bottom-0 -left-1.5 z-20 flex w-3 cursor-col-resize items-center justify-center outline-none select-none"
      >
        <div
          className={cn(
            'h-full w-[2px] transition-colors',
            isResizing ? 'bg-primary' : 'bg-transparent group-hover:bg-primary/70'
          )}
        />
        <div
          className={cn(
            'bg-border/90 text-muted-foreground absolute top-1/2 -translate-y-1/2 rounded-full border p-0.5 shadow-xs transition-opacity opacity-0 group-hover:opacity-100',
            isResizing && 'bg-primary text-primary-foreground border-primary opacity-100'
          )}
        >
          <GripVertical className="size-3" />
        </div>
      </div>

      {current()}
    </aside>
  );
}
