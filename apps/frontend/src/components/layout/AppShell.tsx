import { useState } from 'react';
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
  PanelLeftClose,
  PanelLeftOpen,
  Settings,
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
import { DeveloperBadge } from '@/components/DeveloperBadge';
import { BackendBanner } from './BackendBanner';

const NAV = [
  { to: '/monitoring', label: 'Live', icon: Activity },
  { to: '/tags', label: 'Tags', icon: Table2 },
  { to: '/documents', label: 'Documents', icon: FileSearch },
  { to: '/change-sets', label: 'Change sets', icon: GitPullRequest },
  { to: '/audit', label: 'Audit log', icon: History },
  { to: '/settings', label: 'Settings', icon: Settings },
];

export function AppShell() {
  const { session, openLogin, logout, loading } = useAuth();
  const { projects, project, select } = useProject();
  const { list } = usePending();
  const { theme, setTheme } = useTheme();

  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem('bosun.sidebar.collapsed') === 'true';
    } catch {
      return false;
    }
  });

  const toggleSidebar = () => {
    setCollapsed((prev) => {
      const next = !prev;
      try {
        localStorage.setItem('bosun.sidebar.collapsed', String(next));
      } catch {
        // ignore storage error
      }
      return next;
    });
  };

  return (
    <div className="flex h-dvh flex-col">
      <BackendBanner />
      <div className="flex min-h-0 flex-1">
        <nav
          aria-label="Main"
          className={cn(
            'bg-sidebar flex shrink-0 flex-col border-r transition-[width] duration-200 ease-in-out',
            collapsed ? 'w-14' : 'w-52',
          )}
        >
          <div
            className={cn(
              'flex h-12 items-center border-b font-semibold tracking-wide transition-all',
              collapsed ? 'justify-center px-1' : 'justify-between px-3',
            )}
          >
            {!collapsed ? (
              <>
                <div className="flex min-w-0 items-center gap-2">
                  <Anchor className="text-primary size-5 shrink-0" aria-hidden />
                  <span className="truncate">Bosun</span>
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  className="text-muted-foreground hover:text-foreground size-7 shrink-0 rounded-md"
                  onClick={toggleSidebar}
                  aria-label="Collapse sidebar"
                  title="Collapse sidebar"
                >
                  <PanelLeftClose className="size-4" />
                </Button>
              </>
            ) : (
              <Button
                variant="ghost"
                size="icon"
                className="text-primary hover:bg-accent size-8 shrink-0 rounded-md"
                onClick={toggleSidebar}
                aria-label="Expand sidebar"
                title="Expand sidebar (Bosun)"
              >
                <Anchor className="size-5" aria-hidden />
              </Button>
            )}
          </div>
          <ul className="grid gap-1 p-2">
            {NAV.map(({ to, label, icon: Icon }) => (
              <li key={to}>
                <NavLink
                  to={to}
                  title={collapsed ? label : undefined}
                  className={({ isActive }) =>
                    cn(
                      'hover:bg-accent flex items-center rounded-md text-sm transition-colors',
                      collapsed ? 'justify-center p-2.5' : 'gap-2 px-3 py-2',
                      isActive && 'bg-accent text-accent-foreground font-medium',
                    )
                  }
                >
                  <div className="relative flex shrink-0 items-center justify-center">
                    <Icon className="size-4" aria-hidden />
                    {to === '/tags' && list.length > 0 && collapsed && (
                      <span
                        className="bg-warning text-warning-foreground absolute -top-1.5 -right-2 flex size-3.5 items-center justify-center rounded-full text-[9px] font-bold"
                        aria-label={`${list.length} pending edits`}
                      >
                        {list.length > 9 ? '9+' : list.length}
                      </span>
                    )}
                  </div>
                  {!collapsed && (
                    <>
                      <span className="truncate">{label}</span>
                      {to === '/tags' && list.length > 0 && (
                        <span
                          className="bg-warning text-warning-foreground ml-auto rounded-full px-1.5 text-xs font-medium"
                          aria-label={`${list.length} pending edits`}
                        >
                          {list.length}
                        </span>
                      )}
                    </>
                  )}
                </NavLink>
              </li>
            ))}
          </ul>
          <div className="mt-auto border-t p-2">
            {!collapsed ? (
              <DeveloperBadge variant="sidebar" />
            ) : (
              <div className="flex flex-col items-center gap-2">
                <Button
                  variant="ghost"
                  size="icon"
                  className="text-muted-foreground hover:text-foreground size-8 rounded-md"
                  onClick={toggleSidebar}
                  aria-label="Expand sidebar"
                  title="Expand sidebar"
                >
                  <PanelLeftOpen className="size-4" />
                </Button>
                <a
                  href="https://github.com/TheWilsonGlobal"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="hover:bg-accent text-muted-foreground hover:text-foreground flex items-center justify-center rounded-md p-1 transition-colors"
                  title="Engineered by Wilson Pham (@TheWilsonGlobal)"
                >
                  <div className="border-primary/50 bg-background relative flex size-6 shrink-0 items-center justify-center overflow-hidden rounded-full border">
                    <img
                      src="https://avatars.githubusercontent.com/u/70420596?v=4"
                      alt="Wilson Pham"
                      className="size-full object-cover"
                    />
                    <span className="border-background absolute -right-0.5 -bottom-0.5 size-1.5 rounded-full border bg-emerald-500" />
                  </div>
                </a>
              </div>
            )}
          </div>
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
              <DeveloperBadge variant="compact" />
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
