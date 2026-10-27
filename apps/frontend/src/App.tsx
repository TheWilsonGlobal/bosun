import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { AppShell } from '@/components/layout/AppShell';
import { Status } from '@/components/Status';

// Route-level code splitting: each page is its own chunk, loaded on first visit.
const TagsPage = lazy(() =>
  import('@/features/tags/TagsPage').then((m) => ({ default: m.TagsPage })),
);
const ChangeSetsPage = lazy(() =>
  import('@/features/changesets/ChangeSetsPage').then((m) => ({ default: m.ChangeSetsPage })),
);
const ChangeSetDetailPage = lazy(() =>
  import('@/features/changesets/ChangeSetDetailPage').then((m) => ({
    default: m.ChangeSetDetailPage,
  })),
);
const AuditPage = lazy(() =>
  import('@/features/audit/AuditPage').then((m) => ({ default: m.AuditPage })),
);
const DocumentsPage = lazy(() =>
  import('@/features/extraction/DocumentsPage').then((m) => ({ default: m.DocumentsPage })),
);
const ExtractionPage = lazy(() =>
  import('@/features/extraction/ExtractionPage').then((m) => ({ default: m.ExtractionPage })),
);

const MonitoringPage = lazy(() =>
  import('@/features/monitoring/MonitoringPage').then((m) => ({ default: m.MonitoringPage })),
);
const SettingsPage = lazy(() =>
  import('@/features/settings/SettingsPage').then((m) => ({ default: m.SettingsPage })),
);

export function App() {
  return (
    <Suspense fallback={<Status>Loading...</Status>}>
      <Routes>
        <Route element={<AppShell />}>
          <Route index element={<Navigate to="/tags" replace />} />
          <Route path="/tags" element={<TagsPage />} />
          <Route path="/change-sets" element={<ChangeSetsPage />} />
          <Route path="/change-sets/:id" element={<ChangeSetDetailPage />} />
          <Route path="/documents" element={<DocumentsPage />} />
          <Route path="/extractions/:id" element={<ExtractionPage />} />
          <Route path="/monitoring" element={<MonitoringPage />} />
          <Route path="/audit" element={<AuditPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route
            path="*"
            element={<Status title="Page not found">There is nothing at this address.</Status>}
          />
        </Route>
      </Routes>
    </Suspense>
  );
}
