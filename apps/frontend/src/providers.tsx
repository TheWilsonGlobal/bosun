import type { ReactNode } from 'react';
import { QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ApiError } from '@/lib/api/client';
import { AuthProvider } from '@/features/auth/session';
import { ProjectProvider } from '@/features/project/ProjectContext';
import { PendingProvider } from '@/features/tags/PendingContext';
import { Toaster } from '@/components/ui/sonner';

export function createQueryClient() {
  const queryCache = new QueryCache({
    onError: (error, query) => {
      // If any query gets 401 unauthorized, synchronize session to null so the UI doesn't claim
      // a user is logged in while queries are rejected as unauthorized.
      if (error instanceof ApiError && error.code === 'unauthorized') {
        if (query.queryKey[0] !== 'session') {
          client.setQueryData(['session'], null);
        }
      }
    },
  });

  const client: QueryClient = new QueryClient({
    queryCache,
    defaultOptions: {
      // Errors are typed ApiErrors with their own UI; automatic retries would only delay the offline banner.
      queries: { retry: false, refetchOnWindowFocus: false },
    },
  });

  return client;
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
