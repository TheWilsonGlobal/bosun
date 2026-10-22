import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { BookOpen, FileSearch, RefreshCw, Trash2 } from 'lucide-react';
import { Status } from '@/components/Status';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { KnowledgeViewer } from '@/features/knowledge/KnowledgeViewer';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { ApiError } from '@/lib/api/client';
import { describeError, type ErrorMessage } from '@/lib/api/errors';
import { useAuth } from '@/features/auth/session';
import { useProject } from '@/features/project/ProjectContext';
import { deleteDocument, extractAgain, fetchDocuments, uploadDocument } from './api';
import { describeDeleteError, describeRerunError, describeUploadError } from './messages';
import { ExtractionStatusBadge } from './parts';
import { UploadZone } from './UploadZone';
import { formatBytes } from './upload';
import { isActive, type DocumentItem } from './types';

export const documentsKey = (projectId: number | undefined) => ['documents', projectId] as const;

export function DocumentsPage({ pollMs = 3000 }: { pollMs?: number }) {
  const { project } = useProject();
  const { openLogin } = useAuth();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [uploadError, setUploadError] = useState<ErrorMessage | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<DocumentItem | null>(null);
  const [activeTab, setActiveTab] = useState<'uploads' | 'knowledge'>('uploads');
  const projectId = project?.id;

  const list = useQuery({
    queryKey: documentsKey(projectId),
    queryFn: () => fetchDocuments(projectId!),
    enabled: projectId !== undefined,
    // Keep the list fresh while something is being read.
    refetchInterval: (q) =>
      q.state.data?.some((d) => d.latestExtraction && isActive(d.latestExtraction.status))
        ? pollMs
        : false,
  });

  const needLogin = (e: unknown) => {
    if (e instanceof ApiError && e.code === 'unauthorized') openLogin();
  };

  const upload = useMutation({
    mutationFn: (file: File) => uploadDocument(projectId!, file),
    onMutate: () => setUploadError(null),
    onSuccess: (res) => {
      void qc.invalidateQueries({ queryKey: ['documents'] });
      toast.success('Uploaded. Reading the document...');
      void navigate(`/extractions/${res.extractionId}`);
    },
    onError: (e) => {
      setUploadError(describeUploadError(e));
      needLogin(e);
    },
  });

  const rerun = useMutation({
    mutationFn: (documentId: number) => extractAgain(documentId),
    onSuccess: (res) => {
      void qc.invalidateQueries({ queryKey: ['documents'] });
      void navigate(`/extractions/${res.extractionId}`);
    },
    onError: (e) => {
      const m = describeRerunError(e);
      toast.error(m.title, { description: m.message });
      needLogin(e);
      void qc.invalidateQueries({ queryKey: ['documents'] });
    },
  });

  const remove = useMutation({
    mutationFn: (documentId: number) => deleteDocument(documentId),
    onSuccess: () => {
      setConfirmDelete(null);
      void qc.invalidateQueries({ queryKey: ['documents'] });
      toast.success('Document deleted.');
    },
    onError: (e) => {
      const m = describeDeleteError(e);
      setConfirmDelete(null);
      toast.error(m.title, { description: m.message });
      needLogin(e);
      if (m.refetch) void qc.invalidateQueries({ queryKey: ['documents'] });
    },
  });

  if (!project) return <Status>Select a project to upload documents.</Status>;

  return (
    <div className="flex h-full flex-col gap-4 overflow-auto p-4">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-3">
        <div className="flex items-center gap-2">
          <FileSearch className="size-5 text-primary" aria-hidden />
          <h1 className="text-lg font-semibold">Documents</h1>
          <span className="text-muted-foreground text-sm">
            {activeTab === 'uploads'
              ? `Customer requirement PDFs of ${project.name}`
              : `System reference standards & knowledge chunks of ${project.name}`}
          </span>
        </div>

        <div className="flex items-center gap-1 bg-muted p-1 rounded-lg">
          <Button
            type="button"
            size="sm"
            variant={activeTab === 'uploads' ? 'default' : 'ghost'}
            className="h-7 text-xs px-3"
            onClick={() => setActiveTab('uploads')}
          >
            Customer Uploads (PDF)
            {list.data && list.data.length > 0 && (
              <Badge variant="secondary" className="ml-1.5 px-1.5 py-0 text-[10px]">
                {list.data.length}
              </Badge>
            )}
          </Button>
          <Button
            type="button"
            size="sm"
            variant={activeTab === 'knowledge' ? 'default' : 'ghost'}
            className="h-7 text-xs px-3 gap-1.5"
            onClick={() => setActiveTab('knowledge')}
          >
            <BookOpen className="size-3.5" />
            Knowledge Base (3 Docs / 15 Chunks)
          </Button>
        </div>
      </div>

      {activeTab === 'uploads' ? (
        <>
          <UploadZone
            busy={upload.isPending}
            error={uploadError}
            onFile={(file) => upload.mutate(file)}
          />

          <section aria-label="Uploaded documents" className="grid gap-2">
            <h2 className="text-sm font-semibold">Uploaded documents</h2>
            {list.isPending ? (
              <p role="status" className="text-muted-foreground text-sm">
                Loading documents...
              </p>
            ) : list.isError ? (
              <p role="alert" className="text-destructive text-sm">
                {describeError(list.error).message}
              </p>
            ) : list.data.length === 0 ? (
              <p className="text-muted-foreground text-sm">
                No documents yet. Upload a PDF above to extract tags from it.
              </p>
            ) : (
              <DocumentTable
                docs={list.data}
                rerunning={rerun.isPending ? rerun.variables : null}
                onRerun={(d) => rerun.mutate(d.id)}
                onDelete={setConfirmDelete}
              />
            )}
          </section>
        </>
      ) : (
        <KnowledgeViewer projectId={projectId!} />
      )}

      <Dialog open={confirmDelete !== null} onOpenChange={(o) => !o && setConfirmDelete(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete document?</DialogTitle>
            <DialogDescription>
              {confirmDelete
                ? `${confirmDelete.displayName} and its extractions will be deleted. Change sets already created from it are not affected. This cannot be undone.`
                : ''}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setConfirmDelete(null)}>
              Cancel
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={remove.isPending}
              onClick={() => confirmDelete && remove.mutate(confirmDelete.id)}
            >
              {remove.isPending ? 'Deleting...' : 'Delete'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

function DocumentTable({
  docs,
  rerunning,
  onRerun,
  onDelete,
}: {
  docs: DocumentItem[];
  rerunning: number | null;
  onRerun: (d: DocumentItem) => void;
  onDelete: (d: DocumentItem) => void;
}) {
  return (
    <div className="overflow-auto rounded-md border">
      <table className="w-full text-left text-sm">
        <caption className="sr-only">Uploaded documents and their latest extraction</caption>
        <thead className="bg-muted text-xs">
          <tr>
            {['Document', 'Pages', 'Size', 'Uploaded', 'Latest extraction', 'Rows', ''].map(
              (h, i) => (
                <th key={i} scope="col" className="px-3 py-2 font-semibold">
                  {h || <span className="sr-only">Actions</span>}
                </th>
              ),
            )}
          </tr>
        </thead>
        <tbody>
          {docs.map((d) => {
            const ex = d.latestExtraction;
            const active = ex ? isActive(ex.status) : false;
            return (
              <tr key={d.id} className="border-t" data-testid={`document-${d.id}`}>
                <th scope="row" className="px-3 py-2 font-medium">
                  {d.displayName}
                </th>
                <td className="px-3 py-2 tabular-nums">{d.pageCount}</td>
                <td className="px-3 py-2 tabular-nums">{formatBytes(d.sizeBytes)}</td>
                <td className="px-3 py-2">
                  {new Date(d.uploadedAt).toLocaleString()}
                  <span className="text-muted-foreground block text-xs">by {d.uploadedBy}</span>
                </td>
                <td className="px-3 py-2">
                  {ex ? (
                    <Link
                      to={`/extractions/${ex.id}`}
                      className="inline-flex items-center gap-1.5 underline"
                      aria-label={`Open extraction ${ex.id} of ${d.displayName}`}
                    >
                      #{ex.id} <ExtractionStatusBadge status={ex.status} />
                    </Link>
                  ) : (
                    <span className="text-muted-foreground">none</span>
                  )}
                </td>
                <td className="px-3 py-2 text-xs tabular-nums">
                  {ex && !active
                    ? `${ex.summary.rows} (${ex.summary.auto} auto, ${ex.summary.review} review, ${ex.summary.mustFix} must fix)`
                    : '-'}
                </td>
                <td className="px-3 py-2 text-right">
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={active || rerunning === d.id}
                    onClick={() => onRerun(d)}
                    aria-label={`Read ${d.displayName} again`}
                  >
                    <RefreshCw aria-hidden /> Read again
                  </Button>{' '}
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={active}
                    onClick={() => onDelete(d)}
                    aria-label={`Delete ${d.displayName}`}
                  >
                    <Trash2 aria-hidden /> Delete
                  </Button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
