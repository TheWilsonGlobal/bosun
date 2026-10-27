import { Bot } from 'lucide-react';
import { AssistantPanel } from './AssistantPanel';
import { DeveloperBadge } from '@/components/DeveloperBadge';

export function AssistantPage() {
  return (
    <div className="flex h-full flex-col overflow-hidden bg-background">
      <div className="mx-auto flex h-full w-full max-w-6xl flex-col p-3 sm:p-5">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2 border-b pb-3">
          <div className="flex items-center gap-2.5">
            <div className="bg-primary/10 text-primary flex size-9 items-center justify-center rounded-lg">
              <Bot className="size-5" />
            </div>
            <div>
              <h1 className="text-xl font-bold tracking-tight">Bosun AI Engineering Assistant</h1>
              <p className="text-muted-foreground text-xs">
                Autonomous change set generator with self-repairing ReAct agent & RAG maritime specification retrieval.
              </p>
            </div>
          </div>
          <DeveloperBadge variant="compact" />
        </div>

        <div className="bg-card flex-1 min-h-0 overflow-hidden rounded-xl border shadow-xs flex flex-col">
          <AssistantPanel />
        </div>
      </div>
    </div>
  );
}

export default AssistantPage;
