import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from '@/features/auth/session';
import { ProjectProvider } from '@/features/project/ProjectContext';
import { PendingProvider } from '@/features/tags/PendingContext';
import { Toaster } from '@/components/ui/sonner';

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      // Errors are typed ApiErrors with their own UI; automatic retries would only delay the offline banner.
      queries: { retry: false, refetchOnWindowFocus: false },
    },
  });
}

export function AppProviders({ client, children }: { client: QueryClient; children: ReactNode }) {
  return (
    <QueryClientProvider client={client}>
      <AuthProvider>
        <ProjectProvider>
          <PendingProvider>
            {children}
            <Toaster position="bottom-right" richColors />
          </PendingProvider>
        </ProjectProvider>
      </AuthProvider>
    </QueryClientProvider>
  );
}
