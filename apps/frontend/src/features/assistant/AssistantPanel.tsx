import { useCallback, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Bot, LogIn, Send } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { useAuth } from '@/features/auth/session';
import { useProject } from '@/features/project/ProjectContext';
import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/errors';
import { startRun } from './api';
import { AskPanel } from './ask/AskPanel';
import { RunHistory, runsKey } from './RunHistory';
import { DEMO_INSTRUCTION, MAX_MESSAGE_LENGTH } from './types';
import type { UseAgentRunOptions } from './useAgentRun';

interface Selected {
  id: number;
  live: boolean;
}

export function AssistantPanel(props: UseAgentRunOptions = {}) {
  const { session, openLogin, loading: sessionLoading } = useAuth();
  const { project } = useProject();
  const qc = useQueryClient();
  const [text, setText] = useState('');
  const [selected, setSelected] = useState<Selected | null>(null);
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<unknown>(null);
  const [mode, setMode] = useState<'change' | 'ask'>('change');

  const projectId = project?.id ?? null;
  const refreshHistory = useCallback(() => {
    if (projectId !== null) void qc.invalidateQueries({ queryKey: runsKey(projectId) });
  }, [qc, projectId]);

  const trimmed = text.trim();
  const canSend = !!session && projectId !== null && trimmed.length > 0 && !starting;
  const inProgressId =
    startError instanceof ApiError && startError.code === 'agent_run_in_progress'
      ? Number(startError.extensions.runId) || null
      : null;

  const send = async () => {
    if (!canSend || projectId === null) return;
    setStarting(true);
    setStartError(null);
    try {
      const { runId } = await startRun(projectId, trimmed);
      setSelected({ id: runId, live: true });
      setText('');
      refreshHistory();
    } catch (e) {
      setStartError(e);
      if (e instanceof ApiError && e.code === 'unauthorized') openLogin();
    } finally {
      setStarting(false);
    }
  };

  const startErrorMessage = startError ? describeError(startError) : null;

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div className="flex h-12 shrink-0 items-center gap-2 border-b px-4 font-semibold">
        <Bot className="text-primary size-5" aria-hidden />
        Assistant
        <Badge variant="outline" className="ml-auto font-normal">
          drafts only
        </Badge>
      </div>

      <div
        role="tablist"
        aria-label="Assistant mode"
        className="flex shrink-0 gap-1 border-b px-3 py-2"
      >
        {(
          [
            ['change', 'Change'],
            ['ask', 'Ask'],
          ] as const
        ).map(([id, label]) => (
          <button
            key={id}
            type="button"
            role="tab"
            id={`assistant-tab-${id}`}
            aria-selected={mode === id}
            aria-controls="assistant-mode-panel"
            className={
              'focus-visible:ring-ring/50 rounded-md px-3 py-1 text-sm outline-none focus-visible:ring-[3px] ' +
              (mode === id ? 'bg-accent text-accent-foreground font-medium' : 'hover:bg-accent/50')
            }
            onClick={() => setMode(id)}
          >
            {label}
          </button>
        ))}
        <span className="text-muted-foreground ml-auto self-center text-xs">
          {mode === 'change' ? 'Draft a change set' : 'Answers from the docs'}
        </span>
      </div>

      <div
        role="tabpanel"
        id="assistant-mode-panel"
        aria-labelledby={`assistant-tab-${mode}`}
        className="min-h-0 flex-1 overflow-auto p-4"
      >
        {mode === 'ask' ? (
          <AskPanel projectId={projectId} />
        ) : (
          <div className="grid gap-5">
            <form
              className="grid gap-2"
              onSubmit={(e) => {
                e.preventDefault();
                void send();
              }}
            >
              <label htmlFor="assistant-input" className="text-sm font-medium">
                Describe a change
              </label>
              <Textarea
                id="assistant-input"
                value={text}
                maxLength={MAX_MESSAGE_LENGTH}
                rows={3}
                disabled={!session || starting}
                placeholder="e.g. raise the H alarm on all lube oil pressure sensors"
                onChange={(e) => setText(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    void send();
                  }
                }}
              />
              <div className="flex flex-wrap items-center gap-2">
                <button
                  type="button"
                  className="bg-secondary text-secondary-foreground hover:bg-secondary/80 focus-visible:ring-ring/50 max-w-full rounded-full px-3 py-1 text-left text-xs outline-none focus-visible:ring-[3px] disabled:opacity-50"
                  disabled={!session || starting}
                  onClick={() => setText(DEMO_INSTRUCTION)}
                  title="Use this example"
                >
                  {DEMO_INSTRUCTION}
                </button>
              </div>
              <div className="flex items-center gap-2">
                <span className="text-muted-foreground text-xs tabular-nums">
                  {text.length} / {MAX_MESSAGE_LENGTH}
                </span>
                {session ? (
                  <Button type="submit" size="sm" className="ml-auto" disabled={!canSend}>
                    <Send aria-hidden /> {starting ? 'Starting...' : 'Send'}
                  </Button>
                ) : (
                  <Button
                    type="button"
                    size="sm"
                    className="ml-auto"
                    onClick={openLogin}
                    disabled={sessionLoading}
                  >
                    <LogIn aria-hidden /> Sign in to use the assistant
                  </Button>
                )}
              </div>
              {startErrorMessage && (
                <div
                  role="alert"
                  className="border-destructive/50 bg-destructive/10 rounded-md border px-3 py-2 text-sm"
                >
                  <p className="font-semibold">
                    {startError instanceof ApiError && startError.code === 'agent_run_in_progress'
                      ? 'A run is already in progress'
                      : startErrorMessage.title}
                  </p>
                  <p>{startErrorMessage.message}</p>
                  {inProgressId !== null && (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="mt-2"
                      onClick={() => {
                        setSelected({ id: inProgressId, live: true });
                        setStartError(null);
                      }}
                    >
                      Watch run #{inProgressId}
                    </Button>
                  )}
                </div>
              )}
            </form>

            {project ? (
              <RunHistory
                projectId={project.id}
                selected={selected}
                onSelect={setSelected}
                onFinished={refreshHistory}
                runViewOptions={props}
              />
            ) : (
              <p className="text-muted-foreground text-sm">
                Select a project to use the assistant.
              </p>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
