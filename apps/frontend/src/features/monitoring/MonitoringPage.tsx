import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { PowerOff, WifiOff } from 'lucide-react';
import { Status } from '@/components/Status';
import { useAuth } from '@/features/auth/session';
import { useProject } from '@/features/project/ProjectContext';
import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/errors';
import { fetchMimic } from './api';
import { AlarmBanner, AlarmList, ConnectionBadges } from './AlarmPanels';
import { describeSimulatorState, simulatorState } from './messages';
import { MimicView } from './MimicView';
import { createMonitoringStore, useMonitoringSelector } from './store';
import { useMonitoring, type UseMonitoringOptions } from './useMonitoring';

function describeMimicError(error: unknown) {
  if (error instanceof ApiError) {
    if (error.code === 'mimic_not_found')
      return {
        title: 'No mimic layout',
        message:
          'The backend has no mimic layout (seed/mimic.json is missing), so there is nothing to draw.',
      };
    if (error.code === 'mimic_invalid')
      return {
        title: 'The mimic layout is invalid',
        message: error.detail || 'seed/mimic.json could not be read. Fix the file and reload.',
      };
  }
  return describeError(error);
}

export function MonitoringPage(options: UseMonitoringOptions = {}) {
  const { project } = useProject();
  const { session } = useAuth();
  const [store] = useState(createMonitoringStore);
  const { transport, error } = useMonitoring(store, {
    ...options,
    authKey: session?.username ?? null,
  });
  const status = useMonitoringSelector(store, (s) => s.status);
  const connections = useMonitoringSelector(store, (s) => s.connections);
  const projectId = project?.id ?? null;

  const mimic = useQuery({
    queryKey: ['mimic', projectId],
    queryFn: () => fetchMimic(projectId!),
    enabled: projectId !== null,
    staleTime: 5 * 60_000,
  });

  const sim = simulatorState(status, connections);
  const simMessage = describeSimulatorState(sim, connections);
  const offline = error instanceof ApiError && error.isOffline;

  if (!project) return <Status>Select a project.</Status>;

  return (
    <div className="h-full overflow-auto">
      <div className="mx-auto grid max-w-6xl gap-4 p-4">
        <header className="flex flex-wrap items-start gap-x-8 gap-y-3">
          <div>
            <h1 className="text-lg font-semibold">Live engine room</h1>
            <p
              className="text-muted-foreground text-xs"
              data-testid="transport"
              data-transport={transport}
            >
              {transport === 'live'
                ? 'Live feed connected.'
                : transport === 'connecting'
                  ? 'Connecting to the live feed...'
                  : 'Live feed unavailable; refreshing over HTTP every few seconds.'}
            </p>
          </div>
          <ConnectionBadges store={store} />
        </header>

        {offline && (
          <div
            role="alert"
            className="border-destructive/50 bg-destructive/10 flex items-center gap-2 rounded-md border px-3 py-2 text-sm"
          >
            <WifiOff className="size-4" aria-hidden />
            The backend is not reachable. Values below are the last ones received.
          </div>
        )}
        {error != null && !offline && (
          <p role="alert" className="text-destructive text-sm">
            {describeError(error).title}: {describeError(error).message}
          </p>
        )}

        {simMessage && (
          <div
            role="alert"
            data-testid="simulator-state"
            data-state={sim}
            className="border-warning bg-warning/20 flex gap-2 rounded-md border px-3 py-2 text-sm"
          >
            <PowerOff className="mt-0.5 size-4 shrink-0" aria-hidden />
            <div>
              <p className="font-semibold">{simMessage.title}</p>
              <p>{simMessage.message}</p>
            </div>
          </div>
        )}

        <AlarmBanner store={store} />

        <div className="bg-card rounded-lg border p-2">
          {mimic.isPending && <Status>Loading the mimic...</Status>}
          {mimic.isError && (
            <Status tone="error" title={describeMimicError(mimic.error).title}>
              {describeMimicError(mimic.error).message}
            </Status>
          )}
          {mimic.data && (
            <>
              <MimicView doc={mimic.data} store={store} />
              {mimic.data.missingTags.length > 0 && (
                <p className="text-muted-foreground px-2 pb-1 text-xs">
                  {mimic.data.missingTags.length} mimic tag
                  {mimic.data.missingTags.length === 1 ? ' is' : 's are'} not in this project and
                  not drawn: {mimic.data.missingTags.join(', ')}.
                </p>
              )}
            </>
          )}
        </div>

        <AlarmList store={store} />
      </div>
    </div>
  );
}
