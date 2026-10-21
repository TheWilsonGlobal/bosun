import { get, post } from '@/lib/api/client';
import type {
  Alarm,
  AlarmFilter,
  AlarmList,
  MimicDoc,
  MonitoringStatus,
  Navigation,
  ValuesBatch,
} from './types';

export const fetchStatus = (signal?: AbortSignal) =>
  get<MonitoringStatus>('/api/monitoring/status', signal);
export const fetchValues = (signal?: AbortSignal) =>
  get<ValuesBatch>('/api/monitoring/values', signal);
export const fetchNavigation = (signal?: AbortSignal) =>
  get<Navigation>('/api/monitoring/navigation', signal);
export const fetchAlarms = (
  state: AlarmFilter | 'all' = 'all',
  limit = 200,
  signal?: AbortSignal,
) => get<AlarmList>(`/api/monitoring/alarms?state=${state}&limit=${limit}`, signal);
export const ackAlarm = (id: number) => post<Alarm>(`/api/monitoring/alarms/${id}/ack`);
export const fetchMimic = (projectId: number, signal?: AbortSignal) =>
  get<MimicDoc>(`/api/projects/${projectId}/mimic`, signal);
