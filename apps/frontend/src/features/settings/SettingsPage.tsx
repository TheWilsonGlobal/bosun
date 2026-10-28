import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import {
  Activity,
  AlertCircle,
  BookOpen,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Clock,
  Cpu,
  Database,
  ExternalLink,
  Moon,
  Palette,
  RefreshCw,
  Settings as SettingsIcon,
  ShieldCheck,
  Sun,
  Terminal,
  Wrench,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { fetchHealth } from '@/lib/api/endpoints';
import { fetchKnowledge } from '@/features/assistant/ask/api';
import { fetchRun, fetchRuns } from '@/features/assistant/api';
import { previewArgs, toolCallOf, toolResultOf } from '@/features/assistant/stepPayloads';
import { useProject } from '@/features/project/ProjectContext';
import { useAuth } from '@/features/auth/session';
import { useTheme } from '@/lib/theme';
import { cn } from '@/lib/utils';
import { DeveloperBadge } from '@/components/DeveloperBadge';

interface StatProps {
  label: string;
  value: React.ReactNode;
  tone?: 'good' | 'warn' | 'bad' | 'neutral';
  subtext?: string;
  mono?: boolean;
}

function Stat({ label, value, tone = 'neutral', subtext, mono = false }: StatProps) {
  const toneClasses = {
    good: 'text-emerald-500 dark:text-emerald-400 font-medium',
    warn: 'text-amber-500 dark:text-amber-400 font-medium',
    bad: 'text-rose-500 dark:text-rose-400 font-medium',
    neutral: 'text-foreground',
  };

  return (
    <div className="flex flex-col gap-1 rounded-md border bg-muted/30 p-2.5 transition-colors">
      <span className="text-muted-foreground text-xs font-medium">{label}</span>
      <span className={cn('text-sm break-all', mono && 'font-mono text-xs', toneClasses[tone])}>
        {value}
      </span>
      {subtext && <span className="text-muted-foreground text-[11px]">{subtext}</span>}
    </div>
  );
}

interface SectionProps {
  id: string;
  title: string;
  icon: React.ComponentType<{ className?: string }>;
  badge?: string;
  badgeTone?: 'good' | 'warn' | 'bad' | 'neutral';
  configCount?: number;
  isOpen: boolean;
  onToggle: () => void;
  children: React.ReactNode;
}

function SectionCard({
  title,
  icon: Icon,
  badge,
  badgeTone = 'neutral',
  configCount,
  isOpen,
  onToggle,
  children,
}: SectionProps) {
  const badgeColors = {
    good: 'border-emerald-500/30 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400',
    warn: 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400',
    bad: 'border-rose-500/30 bg-rose-500/10 text-rose-600 dark:text-rose-400',
    neutral: 'border-border bg-muted/50 text-muted-foreground',
  };

  return (
    <div className="bg-card rounded-lg border shadow-xs overflow-hidden transition-all">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={isOpen}
        className="hover:bg-accent/50 flex w-full items-center justify-between p-3.5 text-left transition-colors cursor-pointer select-none"
      >
        <div className="flex items-center gap-2.5 min-w-0">
          <div className="bg-primary/10 text-primary flex size-8 items-center justify-center rounded-md shrink-0">
            <Icon className="size-4" />
          </div>
          <span className="font-semibold text-sm truncate">{title}</span>
          {configCount !== undefined && (
            <span className="text-muted-foreground hidden sm:inline text-xs">
              ({configCount} params)
            </span>
          )}
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {badge && (
            <span className={cn('rounded-full border px-2 py-0.5 text-xs font-medium', badgeColors[badgeTone])}>
              {badge}
            </span>
          )}
          {isOpen ? (
            <ChevronUp className="text-muted-foreground size-4" />
          ) : (
            <ChevronDown className="text-muted-foreground size-4" />
          )}
        </div>
      </button>

      {isOpen && <div className="border-t p-4 pt-3.5 space-y-3">{children}</div>}
    </div>
  );
}

interface AgentToolDef {
  name: string;
  category: 'read' | 'write' | 'verify';
  description: string;
  parameters: string;
  safety: string;
}

const AGENT_TOOLS: AgentToolDef[] = [
  {
    name: 'search_tags',
    category: 'read',
    description: 'Searches vessel sensors and actuators by keyword across TagName, Description, and System.',
    parameters: 'terms: string[], system?: string, limit?: number (max 50)',
    safety: 'Read-only · Output wrapped in delimiter to prevent prompt injection',
  },
  {
    name: 'get_tags',
    category: 'read',
    description: 'Fetches detailed engineering metadata and active alarm setpoints for exact tag names.',
    parameters: 'tag_names: string[]',
    safety: 'Read-only · Structured primary key lookup with second-order injection scanning',
  },
  {
    name: 'list_alarm_groups',
    category: 'read',
    description: 'Lists all configured marine alarm groups, member tag counts, and system descriptions.',
    parameters: '(none)',
    safety: 'Read-only · Project-scoped reference data inspection',
  },
  {
    name: 'list_controllers',
    category: 'read',
    description: 'Enumerates distributed automation controllers (DPUs), I/O modules, and channel bindings.',
    parameters: '(none)',
    safety: 'Read-only · Marine hardware topology & channel allocation inspection',
  },
  {
    name: 'search_docs',
    category: 'read',
    description: 'Performs semantic vector search across technical documentation, DNV-GL rules, and manuals.',
    parameters: 'query: string, limit?: number (default 5)',
    safety: 'Read-only · Gemini Embedding 2 cosine similarity (threshold ≥ 0.65)',
  },
  {
    name: 'propose_create_tag',
    category: 'write',
    description: 'Proposes creating a new sensor or actuator tag staged in the active draft change set.',
    parameters: 'tag_name, signal_type, system, description?, unit?, range_low?, range_high?, safety_class?',
    safety: 'Write-proposal · Appends op to draft only; zero direct database mutation',
  },
  {
    name: 'propose_update_tag',
    category: 'write',
    description: 'Proposes modifying tag engineering attributes (unit, ranges, description) in draft.',
    parameters: 'tag_name, field: "description" | "unit" | "range_low" | "range_high" | "alarm_group", new_value',
    safety: 'Write-proposal · Requires human Maker-Checker approval; SIL/ESD flagged',
  },
  {
    name: 'propose_set_alarm_limit',
    category: 'write',
    description: 'Proposes creating or updating alarm thresholds (LL, L, H, HH) with deadband and delay.',
    parameters: 'tag_name, level: "H" | "HH" | "L" | "LL", value, deadband?, delay_seconds?',
    safety: 'Write-proposal · Boundary-checked against sensor physical range; maker-checker gated',
  },
  {
    name: 'propose_assign_group',
    category: 'write',
    description: 'Proposes assigning an automation tag to an operational marine alarm group.',
    parameters: 'tag_name, alarm_group',
    safety: 'Write-proposal · Validates alarm group existence within project',
  },
  {
    name: 'validate_draft',
    category: 'verify',
    description: 'Executes comprehensive maritime engineering and safety validation on the current draft.',
    parameters: '(none)',
    safety: 'Deterministic verification · Checks SIL rules, deadbands, ranges, and safety conflicts',
  },
  {
    name: 'show_draft_summary',
    category: 'verify',
    description: 'Produces a structured summary of all operations staged in the current draft change set.',
    parameters: '(none)',
    safety: 'Inspection only · Previews proposed diffs and changed tags before review submission',
  },
];

const ALL_SECTIONS = [
  'llm',
  'tools',
  'protocols',
  'database',
  'knowledge',
  'safety',
  'preferences',
] as const;

type SectionId = (typeof ALL_SECTIONS)[number];

export function SettingsPage() {
  const { project } = useProject();
  const { session } = useAuth();
  const { theme, setTheme } = useTheme();

  const [openSections, setOpenSections] = useState<Set<SectionId>>(
    new Set<SectionId>(ALL_SECTIONS),
  );

  const toggleSection = (id: SectionId) => {
    setOpenSections((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const expandAll = () => setOpenSections(new Set<SectionId>(ALL_SECTIONS));
  const collapseAll = () => setOpenSections(new Set<SectionId>());

  // Queries for live system diagnostics
  const healthQ = useQuery({
    queryKey: ['health'],
    queryFn: () => fetchHealth(),
    refetchInterval: 10000,
  });

  const knowledgeQ = useQuery({
    queryKey: ['knowledge', project?.id],
    queryFn: () => (project ? fetchKnowledge(project.id) : null),
    enabled: !!project,
  });

  const [toolsTab, setToolsTab] = useState<'catalogue' | 'history'>('catalogue');
  const [catalogueFilter, setCatalogueFilter] = useState<'all' | 'read' | 'write' | 'verify'>('all');
  const [selectedRunId, setSelectedRunId] = useState<number | null>(null);
  const [expandedCallSeq, setExpandedCallSeq] = useState<Set<number>>(new Set());

  const toggleExpandedCall = (seq: number) => {
    setExpandedCallSeq((prev) => {
      const next = new Set(prev);
      if (next.has(seq)) next.delete(seq);
      else next.add(seq);
      return next;
    });
  };

  const runsQ = useQuery({
    queryKey: ['agent-runs', project?.id],
    queryFn: () => (project ? fetchRuns(project.id, 1, 10) : null),
    enabled: !!project,
    staleTime: 30_000,
  });

  const latestRunId = runsQ.data?.items?.[0]?.id ?? null;
  const activeRunId = selectedRunId ?? latestRunId;

  const activeRunDetailQ = useQuery({
    queryKey: ['agent-run-detail', activeRunId],
    queryFn: () => (activeRunId ? fetchRun(activeRunId) : null),
    enabled: !!activeRunId,
    staleTime: 30_000,
  });

  const totalRecordedToolCalls =
    runsQ.data?.items?.reduce((acc, r) => acc + (r.toolCalls || 0), 0) ?? 0;

  const activeRunSteps = activeRunDetailQ.data?.steps ?? [];
  const activeRunToolCalls = useMemo(() => {
    const list: Array<{
      seq: number;
      createdAt: string;
      call: ReturnType<typeof toolCallOf>;
      result: ReturnType<typeof toolResultOf> | null;
      latencyMs: number;
      tokens: number;
    }> = [];

    for (let i = 0; i < activeRunSteps.length; i++) {
      const s = activeRunSteps[i];
      if (s.kind === 'tool_call') {
        const call = toolCallOf(s.payload);
        const next = activeRunSteps[i + 1];
        const result = next && next.kind === 'tool_result' ? toolResultOf(next.payload) : null;
        list.push({
          seq: s.seq,
          createdAt: s.createdAt,
          call,
          result,
          latencyMs: s.latencyMs,
          tokens: s.tokens,
        });
      }
    }
    return list;
  }, [activeRunSteps]);

  const filteredTools = useMemo(() => {
    if (catalogueFilter === 'all') return AGENT_TOOLS;
    return AGENT_TOOLS.filter((t) => t.category === catalogueFilter);
  }, [catalogueFilter]);

  const isOnline = healthQ.isSuccess && healthQ.data.databaseOk;
  const [assistantWidth, setAssistantWidth] = useState(() => {
    try {
      return localStorage.getItem('bosun.assistant.width') || '384';
    } catch {
      return '384';
    }
  });

  const isSidebarCollapsed = (() => {
    try {
      return localStorage.getItem('bosun.sidebar.collapsed') === 'true';
    } catch {
      return false;
    }
  })();

  const resetAssistantWidth = () => {
    try {
      localStorage.setItem('bosun.assistant.width', '384');
      setAssistantWidth('384');
    } catch {
      // ignore
    }
  };

  return (
    <div className="flex h-full flex-col overflow-hidden bg-background">
      {/* Top Header & Diagnostics Banner */}
      <header className="shrink-0 border-b bg-card/40 px-4 py-3 sm:px-6 lg:px-8 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-2">
          <SettingsIcon className="text-primary size-5" />
          <h1 className="text-lg sm:text-xl font-bold tracking-tight">Settings & System Telemetry</h1>
        </div>

        <div className="flex items-center gap-2 flex-wrap">
          <div
            className={cn(
              'flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs font-medium',
              isOnline
                ? 'border-emerald-500/30 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400'
                : 'border-rose-500/30 bg-rose-500/10 text-rose-600 dark:text-rose-400',
            )}
          >
            <span
              className={cn(
                'size-2 rounded-full',
                isOnline ? 'bg-emerald-500 animate-pulse' : 'bg-rose-500',
              )}
            />
            {isOnline
              ? `Backend Online · v${healthQ.data?.version ?? '0.1.0'}`
              : 'Backend Checking / Offline'}
          </div>

          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              void healthQ.refetch();
              void knowledgeQ.refetch();
            }}
            disabled={healthQ.isFetching}
            className="gap-1.5"
          >
            <RefreshCw className={cn('size-3.5', healthQ.isFetching && 'animate-spin')} />
            Refresh
          </Button>

          <Button variant="ghost" size="sm" onClick={expandAll}>
            Expand All
          </Button>
          <Button variant="ghost" size="sm" onClick={collapseAll}>
            Collapse All
          </Button>
        </div>
      </header>

      {/* Grid of Settings Cards (Scrollable Body) */}
      <div className="flex-1 min-h-0 overflow-y-auto p-4 sm:p-6 lg:p-8">
        <div className="grid gap-4 w-full">
        {/* 1. LLM & AI Engine Card */}
        <SectionCard
          id="llm"
          title="LLM & AI Engine Configuration"
          icon={Cpu}
          configCount={8}
          badge={isOnline ? 'Gemini 3.5 Active' : 'Checking'}
          badgeTone={isOnline ? 'good' : 'warn'}
          isOpen={openSections.has('llm')}
          onToggle={() => toggleSection('llm')}
        >
          <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-4">
            <Stat label="LLM Provider" value="Google Gemini API" tone="good" />
            <Stat label="Primary Model" value="gemini-3.5-flash-lite" mono />
            <Stat label="Embedding Model" value="gemini-embedding-2" mono />
            <Stat label="Vector Dimension" value="768 dims" />
            <Stat label="Temperature" value="0.0 (Deterministic)" subtext="Optimized for industrial safety" />
            <Stat label="Max Tool Invocations" value="10 calls / run" />
            <Stat label="Max Token Budget" value="100,000 tokens" />
            <Stat
              label="Endpoint Gateway"
              value="generativelanguage.googleapis.com"
              subtext="Cloud Inference"
              mono
            />
          </div>

          <div className="bg-muted/40 rounded-md border p-3 text-xs flex flex-col sm:flex-row sm:items-center justify-between gap-2">
            <span className="text-muted-foreground">
              Autonomous Agent Mode: <strong className="text-foreground">ReAct Cycle with Self-Repair (Max 3 rounds)</strong>
            </span>
            <span className="text-muted-foreground font-mono">
              Prompt Version: <span className="text-primary font-medium">planner.system@1c3b647a</span>
            </span>
          </div>
        </SectionCard>

        {/* 2. AI Agent Tool Registry & Execution Telemetry */}
        <SectionCard
          id="tools"
          title="AI Agent Tool Registry & Execution Telemetry"
          icon={Wrench}
          configCount={11}
          badge={`${AGENT_TOOLS.length} Tools Available`}
          badgeTone="good"
          isOpen={openSections.has('tools')}
          onToggle={() => toggleSection('tools')}
        >
          {/* Top Key Performance Indicators */}
          <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-4">
            <Stat
              label="Registered Tool Declarations"
              value={`${AGENT_TOOLS.length} Tools`}
              tone="good"
              subtext="5 Read · 4 Write/Propose · 2 Verify"
            />
            <Stat
              label="Execution Safety Model"
              value="Draft-Targeted Only"
              tone="good"
              subtext="Zero direct DB mutation; append-only"
            />
            <Stat
              label="Cycle Tool Call Budget"
              value="10 Invocations"
              subtext="Hard safety ceiling per ReAct run"
            />
            <Stat
              label="Recorded Tool Calls"
              value={`${totalRecordedToolCalls} Invocations`}
              tone={totalRecordedToolCalls > 0 ? 'good' : 'neutral'}
              subtext={runsQ.data ? `Across ${runsQ.data.items.length} agent runs` : 'No runs recorded'}
            />
          </div>

          {/* Sub-Tabs: Tool Catalogue vs Recent Invocations */}
          <div className="flex items-center justify-between border-b pb-2 pt-1 flex-wrap gap-2">
            <div className="flex items-center gap-1.5">
              <Button
                variant={toolsTab === 'catalogue' ? 'default' : 'ghost'}
                size="sm"
                onClick={() => setToolsTab('catalogue')}
                className="h-7 text-xs gap-1.5"
              >
                <Terminal className="size-3.5" /> Tool Catalogue ({AGENT_TOOLS.length})
              </Button>
              <Button
                variant={toolsTab === 'history' ? 'default' : 'ghost'}
                size="sm"
                onClick={() => setToolsTab('history')}
                className="h-7 text-xs gap-1.5"
              >
                <Clock className="size-3.5" /> Recent Invocations ({totalRecordedToolCalls})
              </Button>
            </div>

            {toolsTab === 'catalogue' && (
              <div className="flex items-center gap-1">
                {(['all', 'read', 'write', 'verify'] as const).map((cat) => (
                  <button
                    key={cat}
                    type="button"
                    onClick={() => setCatalogueFilter(cat)}
                    className={cn(
                      'rounded px-2 py-0.5 text-[11px] font-medium transition-colors capitalize cursor-pointer',
                      catalogueFilter === cat
                        ? 'bg-primary text-primary-foreground'
                        : 'text-muted-foreground hover:bg-muted',
                    )}
                  >
                    {cat === 'all'
                      ? 'All (11)'
                      : cat === 'read'
                        ? 'Read (5)'
                        : cat === 'write'
                          ? 'Write (4)'
                          : 'Verify (2)'}
                  </button>
                ))}
              </div>
            )}
          </div>

          {/* Tab Content: Tool Catalogue */}
          {toolsTab === 'catalogue' && (
            <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
              {filteredTools.map((tool) => {
                const categoryBadges = {
                  read: 'bg-sky-500/10 text-sky-600 dark:text-sky-400 border-sky-500/30',
                  write: 'bg-amber-500/10 text-amber-600 dark:text-amber-400 border-amber-500/30',
                  verify: 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border-emerald-500/30',
                };
                const categoryLabels = {
                  read: 'Read / Query',
                  write: 'Write Proposal',
                  verify: 'Safety Verification',
                };

                return (
                  <div
                    key={tool.name}
                    className="flex flex-col justify-between rounded-md border bg-muted/20 p-3 hover:bg-muted/40 transition-colors"
                  >
                    <div className="space-y-1.5">
                      <div className="flex items-center justify-between gap-1.5 flex-wrap">
                        <span className="font-mono text-xs font-bold text-foreground bg-background px-1.5 py-0.5 rounded border">
                          {tool.name}
                        </span>
                        <span
                          className={cn(
                            'rounded-full border px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider',
                            categoryBadges[tool.category],
                          )}
                        >
                          {categoryLabels[tool.category]}
                        </span>
                      </div>
                      <p className="text-xs text-muted-foreground leading-relaxed">
                        {tool.description}
                      </p>
                    </div>

                    <div className="mt-2.5 pt-2 border-t space-y-1.5 text-[11px]">
                      <div>
                        <span className="text-muted-foreground font-medium">Parameters: </span>
                        <code className="font-mono text-[11px] text-foreground/90 break-all">
                          {tool.parameters}
                        </code>
                      </div>
                      <div className="text-[11px] text-muted-foreground/80 flex items-center gap-1">
                        <ShieldCheck className="size-3 text-emerald-500 shrink-0" />
                        <span className="truncate">{tool.safety}</span>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}

          {/* Tab Content: Execution History */}
          {toolsTab === 'history' && (
            <div className="space-y-3">
              {runsQ.data && runsQ.data.items.length > 0 ? (
                <>
                  {/* Run Selector */}
                  <div className="flex items-center gap-2 flex-wrap text-xs">
                    <span className="text-muted-foreground">Select Agent Run:</span>
                    <select
                      className="border-input bg-background h-7 rounded border px-2 text-xs font-mono"
                      value={activeRunId ?? ''}
                      onChange={(e) => setSelectedRunId(Number(e.target.value))}
                    >
                      {runsQ.data.items.map((r) => (
                        <option key={r.id} value={r.id}>
                          Run #{r.id} · {r.status} · {r.toolCalls} calls ({(r.message || '').slice(0, 35)}…)
                        </option>
                      ))}
                    </select>

                    {activeRunDetailQ.data && (
                      <span className="text-muted-foreground ml-auto">
                        Total Latency:{' '}
                        <strong className="text-foreground">
                          {activeRunDetailQ.data.elapsedMs ?? 0} ms
                        </strong>{' '}
                        · Tokens:{' '}
                        <strong className="text-foreground font-mono">
                          {(activeRunDetailQ.data.tokens?.total ?? 0).toLocaleString()}
                        </strong>
                      </span>
                    )}
                  </div>

                  {/* List of Tool Calls for Selected Run */}
                  {activeRunToolCalls.length > 0 ? (
                    <div className="space-y-2">
                      {activeRunToolCalls.map((tc) => {
                        const isExpanded = expandedCallSeq.has(tc.seq);
                        const isOk = tc.result ? tc.result.ok : true;

                        return (
                          <div
                            key={tc.seq}
                            className="rounded-md border bg-muted/20 p-2.5 text-xs transition-colors hover:bg-muted/30"
                          >
                            <div className="flex items-center justify-between gap-2 flex-wrap">
                              <div className="flex items-center gap-2">
                                <span className="font-mono text-[11px] font-bold bg-background px-1.5 py-0.5 rounded border">
                                  #{tc.seq} {tc.call.name}
                                </span>
                                {tc.result ? (
                                  <span
                                    className={cn(
                                      'flex items-center gap-1 font-medium text-[11px]',
                                      isOk
                                        ? 'text-emerald-600 dark:text-emerald-400'
                                        : 'text-rose-600 dark:text-rose-400',
                                    )}
                                  >
                                    {isOk ? (
                                      <CheckCircle2 className="size-3" />
                                    ) : (
                                      <AlertCircle className="size-3" />
                                    )}
                                    {isOk ? 'Success' : 'Failed'}
                                  </span>
                                ) : (
                                  <span className="text-muted-foreground text-[11px]">
                                    Executed
                                  </span>
                                )}
                              </div>

                              <div className="flex items-center gap-2">
                                {tc.latencyMs > 0 && (
                                  <span className="text-muted-foreground font-mono text-[11px]">
                                    {tc.latencyMs} ms
                                  </span>
                                )}
                                <Button
                                  variant="ghost"
                                  size="sm"
                                  onClick={() => toggleExpandedCall(tc.seq)}
                                  className="h-6 px-1.5 text-[11px] text-muted-foreground hover:text-foreground"
                                >
                                  {isExpanded ? 'Hide Payload' : 'View Payload'}
                                </Button>
                              </div>
                            </div>

                            <div className="mt-1.5 text-muted-foreground font-mono text-[11px] truncate">
                              Args: {previewArgs(tc.call.args, 120)}
                            </div>

                            {isExpanded && (
                              <div className="mt-2.5 grid gap-2 sm:grid-cols-2 pt-2 border-t">
                                <div>
                                  <span className="font-semibold text-[11px] text-foreground">
                                    Arguments:
                                  </span>
                                  <pre className="mt-1 p-2 rounded bg-background border font-mono text-[11px] overflow-x-auto max-h-40">
                                    {JSON.stringify(tc.call.args, null, 2)}
                                  </pre>
                                </div>
                                <div>
                                  <span className="font-semibold text-[11px] text-foreground">
                                    Return Payload:
                                  </span>
                                  <pre className="mt-1 p-2 rounded bg-background border font-mono text-[11px] overflow-x-auto max-h-40">
                                    {tc.result
                                      ? JSON.stringify(
                                          tc.result.response ?? tc.result.errorMessage,
                                          null,
                                          2,
                                        )
                                      : '(No return payload recorded)'}
                                  </pre>
                                </div>
                              </div>
                            )}
                          </div>
                        );
                      })}
                    </div>
                  ) : (
                    <div className="rounded-md border border-dashed p-6 text-center text-xs text-muted-foreground">
                      Run #{activeRunId} executed 0 tool calls (e.g. clarification prompt or direct
                      response).
                    </div>
                  )}
                </>
              ) : (
                <div className="rounded-md border border-dashed p-8 text-center text-xs text-muted-foreground flex flex-col items-center justify-center gap-2">
                  <Wrench className="size-6 text-muted-foreground/60" />
                  <p className="font-medium text-foreground text-sm">No Recorded Tool Calls Yet</p>
                  <p className="max-w-md">
                    Use the Assistant panel to propose a marine automation modification (e.g. &ldquo;Raise
                    ME lube oil temperature alarm to 95°C&rdquo;) to observe tool call telemetry live.
                  </p>
                </div>
              )}
            </div>
          )}

          {/* Bottom Security / ReAct Footer */}
          <div className="bg-muted/40 rounded-md border p-3 text-xs flex flex-col sm:flex-row sm:items-center justify-between gap-2">
            <span className="text-muted-foreground">
              ReAct Tool Calling Protocol:{' '}
              <strong className="text-foreground">
                Gemini Function Calling API with Strict JSON Schemas
              </strong>
            </span>
            <span className="text-muted-foreground">
              Safety Guardrails:{' '}
              <strong className="text-emerald-600 dark:text-emerald-400">
                Zero Direct Database Access · Draft Appends Only
              </strong>
            </span>
          </div>
        </SectionCard>

        {/* 3. Industrial Protocols & Simulator */}
        <SectionCard
          id="protocols"
          title="Industrial Marine Protocols & Simulator"
          icon={Activity}
          configCount={7}
          badge="Modbus & NMEA Live"
          badgeTone="good"
          isOpen={openSections.has('protocols')}
          onToggle={() => toggleSection('protocols')}
        >
          <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-4">
            <Stat label="Modbus TCP Server" value="127.0.0.1:5483" tone="good" mono />
            <Stat label="NMEA 0183 Server" value="127.0.0.1:5484" tone="good" mono />
            <Stat label="Simulator Feed" value="100 Hz Internal" subtext="High-fidelity PLC math model" />
            <Stat label="Web Dispatch Throttle" value="2 Hz (500 ms)" subtext="SignalR TelemetryHub batching" />
            <Stat label="NMEA Sentences" value="$GPRMC, $HEHDT" mono />
            <Stat label="Checksum Policy" value="XOR 8-bit (*HH)" tone="good" subtext="Corrupted packets dropped" />
            <Stat label="Main Engine Twin" value="MAN B&W 6S60ME-C8" subtext="8 cylinders, 2-stroke diesel" />
            <Stat label="Target Vessel" value="MV Ha Long (IMO 9876543)" tone="good" />
          </div>

          <div className="bg-muted/40 rounded-md border p-3 text-xs text-muted-foreground flex items-center justify-between">
            <span>
              Real-time monitoring engine connected via WebSocket to <code className="text-foreground font-mono">/hubs/telemetry</code>.
            </span>
            <Link to="/monitoring" className="text-primary hover:underline flex items-center gap-1 font-medium">
              View Engine Room Mimic <ExternalLink className="size-3" />
            </Link>
          </div>
        </SectionCard>

        {/* 3. Database & Storage Architecture */}
        <SectionCard
          id="database"
          title="Database & Storage Architecture"
          icon={Database}
          configCount={6}
          badge={healthQ.data?.databaseProvider ? `${healthQ.data.databaseProvider.toUpperCase()} · WAL` : 'SQLite'}
          badgeTone="good"
          isOpen={openSections.has('database')}
          onToggle={() => toggleSection('database')}
        >
          <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
            <Stat
              label="Database Provider"
              value={healthQ.data?.databaseProvider ?? 'sqlite'}
              tone="good"
              mono
            />
            <Stat
              label="Journal Mode"
              value="PRAGMA journal_mode=WAL"
              subtext="Write-Ahead Logging enabled"
              mono
            />
            <Stat label="Busy Timeout" value="5,000 ms" subtext="Prevents database locked race" />
            <Stat label="Total Vessel Tags" value={project ? `${project.tagCount.toLocaleString()} tags` : '5,000 tags'} tone="good" />
            <Stat label="Distributed Controllers" value="3 DPUs (ME01, GEN01, AUX01)" />
            <Stat label="Full-Text Search" value="SQLite FTS5 (Trigram on tag_fts)" mono />
          </div>

          <div className="bg-muted/40 rounded-md border p-3 text-xs text-muted-foreground">
            Connection string: <code className="text-foreground font-mono">Data Source=data/db/bosun.db</code> (Local persistent, Render container ephemeral).
          </div>
        </SectionCard>

        {/* 4. Knowledge Base & RAG Engine */}
        <SectionCard
          id="knowledge"
          title="Knowledge Base & RAG Vector Engine"
          icon={BookOpen}
          configCount={5}
          badge={knowledgeQ.data?.indexed ? '15 Chunks Indexed' : 'Indexed'}
          badgeTone="good"
          isOpen={openSections.has('knowledge')}
          onToggle={() => toggleSection('knowledge')}
        >
          <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
            <Stat
              label="RAG Index Status"
              value={
                knowledgeQ.data?.indexed ? (
                  <span className="flex items-center gap-1 text-emerald-500 font-medium">
                    <CheckCircle2 className="size-3.5" /> Indexed & Ready
                  </span>
                ) : (
                  'Ready'
                )
              }
            />
            <Stat
              label="Indexed Documents"
              value={knowledgeQ.data ? `${knowledgeQ.data.documents} maritime specs` : '3 reference docs'}
            />
            <Stat
              label="Total Semantic Chunks"
              value={knowledgeQ.data ? `${knowledgeQ.data.chunks} chunks` : '15 chunks'}
              tone="good"
            />
            <Stat label="Chunking Strategy" value="Markdown Heading / Section Split" />
            <Stat label="Distance Metric" value="Cosine Similarity" subtext="Threshold ≥ 0.65" />
            <Stat label="Citation Verification" value="Strict Page & Heading Attributed" tone="good" />
          </div>

          <div className="bg-muted/40 rounded-md border p-3 text-xs flex items-center justify-between">
            <span className="text-muted-foreground">
              Reference documents include MAN B&W Alarm Matrix, DNV-GL Class Rules, and Commissioning Trial Reports.
            </span>
            <Link to="/documents" className="text-primary hover:underline flex items-center gap-1 font-medium">
              Open Knowledge Base Viewer <ExternalLink className="size-3" />
            </Link>
          </div>
        </SectionCard>

        {/* 5. Safety Governance & Maker-Checker */}
        <SectionCard
          id="safety"
          title="Safety Governance & Maker-Checker Policy"
          icon={ShieldCheck}
          configCount={5}
          badge="Two-Person Rule Enforced"
          badgeTone="good"
          isOpen={openSections.has('safety')}
          onToggle={() => toggleSection('safety')}
        >
          <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
            <Stat
              label="Four-Eyes Governance"
              value="Maker-Checker-Deployer"
              tone="good"
              subtext="AI proposals require human approval"
            />
            <Stat
              label="Safety Critical Classes"
              value="SIL1, SIL2, ESD"
              tone="warn"
              subtext="Emergency Shutdown Protection"
            />
            <Stat
              label="Second Approval Policy"
              value="Mandatory for Safety Tags"
              tone="good"
              subtext="Enforced on setpoint modifications"
            />
            <Stat
              label="Concurrency Control"
              value="Optimistic (RowVersion / xmin)"
              subtext="Prevents race conditions on apply"
            />
            <Stat
              label="Audit Log Mode"
              value="Append-Only & Immutable"
              tone="good"
              subtext="Zero updates or deletes permitted"
            />
            <Stat
              label="Current Session"
              value={session ? `${session.username} (${session.roles.join(', ')})` : 'Anonymous / Operator'}
            />
          </div>

          <div className="bg-muted/40 rounded-md border p-3 text-xs flex items-center justify-between">
            <span className="text-muted-foreground">
              All applied change sets generate tamper-evident audit records with model provenance and UTC timestamps.
            </span>
            <Link to="/audit" className="text-primary hover:underline flex items-center gap-1 font-medium">
              View Audit Log <ExternalLink className="size-3" />
            </Link>
          </div>
        </SectionCard>

        {/* 6. Workspace & Client Preferences */}
        <SectionCard
          id="preferences"
          title="Workspace & UI Customization"
          icon={Palette}
          configCount={4}
          badge="Client Preferences"
          badgeTone="neutral"
          isOpen={openSections.has('preferences')}
          onToggle={() => toggleSection('preferences')}
        >
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <div className="flex flex-col justify-between rounded-md border bg-muted/30 p-3">
              <div>
                <span className="font-medium text-sm">Theme Appearance</span>
                <p className="text-muted-foreground text-xs mt-0.5">Toggle between Dark and Light mode.</p>
              </div>
              <div className="mt-3 flex items-center gap-2">
                <Button
                  variant={theme === 'dark' ? 'default' : 'outline'}
                  size="sm"
                  onClick={() => setTheme('dark')}
                  className="gap-1.5"
                >
                  <Moon className="size-3.5" /> Dark
                </Button>
                <Button
                  variant={theme === 'light' ? 'default' : 'outline'}
                  size="sm"
                  onClick={() => setTheme('light')}
                  className="gap-1.5"
                >
                  <Sun className="size-3.5" /> Light
                </Button>
              </div>
            </div>

            <div className="flex flex-col justify-between rounded-md border bg-muted/30 p-3">
              <div>
                <span className="font-medium text-sm">Side Navigation Menu</span>
                <p className="text-muted-foreground text-xs mt-0.5">
                  Current state: <strong className="text-foreground">{isSidebarCollapsed ? 'Collapsed (w-14)' : 'Expanded (w-52)'}</strong>
                </p>
              </div>
              <p className="text-muted-foreground text-xs mt-3">
                Use the toggle button in the sidebar header to collapse/expand anytime.
              </p>
            </div>

            <div className="flex flex-col justify-between rounded-md border bg-muted/30 p-3">
              <div>
                <span className="font-medium text-sm">Assistant Panel Width</span>
                <p className="text-muted-foreground text-xs mt-0.5">
                  Current width: <strong className="text-foreground">{assistantWidth}px</strong> (resizable 280–800px).
                </p>
              </div>
              <Button
                variant="outline"
                size="sm"
                onClick={resetAssistantWidth}
                className="mt-3 w-fit"
              >
                Reset to Default (384px)
              </Button>
            </div>
          </div>

          <div className="border-t pt-3 flex items-center justify-between">
            <span className="text-muted-foreground text-xs">
              Bosun Maritime Automation Platform · Version {healthQ.data?.version ?? '0.1.0'}
            </span>
            <DeveloperBadge variant="compact" />
          </div>
        </SectionCard>
      </div>
    </div>
  </div>
  );
}

export default SettingsPage;

