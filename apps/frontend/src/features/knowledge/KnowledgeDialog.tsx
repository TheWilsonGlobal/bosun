import { useState } from 'react';
import { BookOpen, Database } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';
import { KnowledgeViewer } from './KnowledgeViewer';
import type { KnowledgeStatus } from '@/features/assistant/ask/askLogic';

export function KnowledgeDialog({
  projectId,
  status,
  trigger,
}: {
  projectId: number;
  status?: KnowledgeStatus;
  trigger?: React.ReactNode;
}) {
  const [open, setOpen] = useState(false);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        {trigger ?? (
          <Button type="button" size="sm" variant="outline" className="h-7 gap-1.5 text-xs">
            <BookOpen className="size-3.5 text-primary" aria-hidden />
            Inspect Knowledge Chunks
          </Button>
        )}
      </DialogTrigger>
      <DialogContent className="max-w-3xl max-h-[85vh] flex flex-col">
        <DialogHeader>
          <div className="flex items-center gap-2">
            <Database className="size-5 text-primary" />
            <DialogTitle>Reference Knowledge Base (RAG)</DialogTitle>
          </div>
          <DialogDescription>
            3 standard Markdown reference documents and {status?.chunks ?? 15} indexed chunks
            providing grounded domain context to the Assistant Ask agent.
          </DialogDescription>
        </DialogHeader>

        <div className="flex-1 overflow-auto pr-1">
          <KnowledgeViewer projectId={projectId} initialStatus={status} compact />
        </div>
      </DialogContent>
    </Dialog>
  );
}
