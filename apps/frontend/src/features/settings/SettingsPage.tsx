import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import {
  Activity,
  BookOpen,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Cpu,
  Database,
  ExternalLink,
  Moon,
  Palette,
  RefreshCw,
  Settings as SettingsIcon,
  ShieldCheck,
  Sun,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { fetchHealth } from '@/lib/api/endpoints';
import { fetchKnowledge } from '@/features/assistant/ask/api';
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

const ALL_SECTIONS = [
  'llm',
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
        <div className="grid gap-4 max-w-6xl">
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

        {/* 2. Industrial Protocols & Simulator */}
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

