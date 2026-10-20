import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { fetchProjects } from '@/lib/api/endpoints';
import type { Project } from '@/lib/api/types';

const KEY = 'bosun.project';

interface ProjectContextValue {
  projects: Project[];
  project: Project | null;
  loading: boolean;
  error: unknown;
  select: (id: number) => void;
}

const ProjectContext = createContext<ProjectContextValue | null>(null);

export function ProjectProvider({ children }: { children: ReactNode }) {
  const [selected, setSelected] = useState<number | null>(() => {
    const n = Number(localStorage.getItem(KEY));
    return Number.isInteger(n) && n > 0 ? n : null;
  });
  const query = useQuery({ queryKey: ['projects'], queryFn: fetchProjects, staleTime: 30_000 });

  const value = useMemo<ProjectContextValue>(() => {
    const projects = query.data ?? [];
    const project = projects.find((p) => p.id === selected) ?? projects[0] ?? null;
    return {
      projects,
      project,
      loading: query.isPending,
      error: query.error,
      select: (id) => {
        localStorage.setItem(KEY, String(id));
        setSelected(id);
      },
    };
  }, [query.data, query.isPending, query.error, selected]);

  return <ProjectContext.Provider value={value}>{children}</ProjectContext.Provider>;
}

export function useProject(): ProjectContextValue {
  const ctx = useContext(ProjectContext);
  if (!ctx) throw new Error('useProject must be used inside <ProjectProvider>');
  return ctx;
}
