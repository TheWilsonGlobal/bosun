import { NavLink, Outlet } from 'react-router-dom';
import {
  Activity,
  Anchor,
  FileSearch,
  GitPullRequest,
  History,
  LogIn,
  LogOut,
  Moon,
  Sun,
  Table2,
  User,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { useAuth } from '@/features/auth/session';
import { useProject } from '@/features/project/ProjectContext';
import { AssistantSlot } from '@/features/assistant/AssistantSlot';
import { usePending } from '@/features/tags/PendingContext';
import { useTheme } from '@/lib/theme';
import { cn } from '@/lib/utils';
import { BackendBanner } from './BackendBanner';

const NAV = [
  { to: '/monitoring', label: 'Live', icon: Activity },
  { to: '/tags', label: 'Tags', icon: Table2 },
  { to: '/documents', label: 'Documents', icon: FileSearch },
  { to: '/change-sets', label: 'Change sets', icon: GitPullRequest },
  { to: '/audit', label: 'Audit log', icon: History },
];

export function AppShell() {
  const { session, openLogin, logout, loading } = useAuth();
  const { projects, project, select } = useProject();
  const { list } = usePending();
  const { theme, setTheme } = useTheme();

  return (
    <div className="flex h-dvh flex-col">
      <BackendBanner />
      <div className="flex min-h-0 flex-1">
        <nav aria-label="Main" className="bg-sidebar flex w-52 shrink-0 flex-col border-r">
          <div className="flex h-12 items-center gap-2 border-b px-4 font-semibold tracking-wide">
            <Anchor className="text-primary size-5" aria-hidden />
            Bosun
          </div>
          <ul className="grid gap-0.5 p-2">
            {NAV.map(({ to, label, icon: Icon }) => (
              <li key={to}>
                <NavLink
                  to={to}
                  className={({ isActive }) =>
                    cn(
                      'hover:bg-accent flex items-center gap-2 rounded-md px-3 py-2 text-sm',
                      isActive && 'bg-accent text-accent-foreground font-medium',
                    )
                  }
                >
                  <Icon className="size-4" aria-hidden />
                  {label}
                  {to === '/tags' && list.length > 0 && (
                    <span
                      className="bg-warning text-warning-foreground ml-auto rounded-full px-1.5 text-xs font-medium"
                      aria-label={`${list.length} pending edits`}
                    >
                      {list.length}
                    </span>
                  )}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>

        <div className="flex min-w-0 flex-1 flex-col">
          <header className="flex h-12 shrink-0 items-center gap-3 border-b px-4">
            <label htmlFor="project-select" className="text-muted-foreground text-sm">
              Project
            </label>
            <Select
              value={project ? String(project.id) : undefined}
              onValueChange={(v) => select(Number(v))}
              disabled={projects.length === 0}
            >
              <SelectTrigger id="project-select" size="sm" className="w-72">
                <SelectValue placeholder="No project" />
              </SelectTrigger>
              <SelectContent>
                {projects.map((p) => (
                  <SelectItem key={p.id} value={String(p.id)}>
                    {p.name} ({p.tagCount.toLocaleString()} tags)
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {project && (
              <span className="text-muted-foreground hidden text-sm md:inline">
                {project.vesselName}
                {project.imoNumber ? ` - IMO ${project.imoNumber}` : ''}
              </span>
            )}
            <div className="ml-auto flex items-center gap-2">
              <Button
                variant="ghost"
                size="icon"
                aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
                onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
              >
                {theme === 'dark' ? <Sun /> : <Moon />}
              </Button>
              {session ? (
                <>
                  <span className="flex items-center gap-1.5 text-sm" data-testid="current-user">
                    <User className="size-4" aria-hidden />
                    {session.username}
                    <span className="text-muted-foreground text-xs">
                      ({session.roles.join(', ')})
                    </span>
                  </span>
                  <Button variant="outline" size="sm" onClick={() => logout()}>
                    <LogOut aria-hidden /> Sign out
                  </Button>
                </>
              ) : (
                <Button size="sm" onClick={openLogin} disabled={loading}>
                  <LogIn aria-hidden /> Sign in
                </Button>
              )}
            </div>
          </header>
          <div className="flex min-h-0 flex-1">
            <main className="min-w-0 flex-1 overflow-hidden">
              <Outlet />
            </main>
            <AssistantSlot />
          </div>
        </div>
      </div>
    </div>
  );
}
