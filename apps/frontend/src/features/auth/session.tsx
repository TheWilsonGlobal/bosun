import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { fetchSession, login as apiLogin, logout as apiLogout } from '@/lib/api/endpoints';
import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/errors';
import type { Session } from '@/lib/api/types';
import { LoginDialog } from './LoginDialog';

interface AuthContextValue {
  session: Session | null;
  /** True until the first /api/auth/me answer. */
  loading: boolean;
  openLogin: () => void;
  logout: () => void;
  login: (username: string, password: string) => Promise<Session>;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const query = useQuery({
    queryKey: ['session'],
    queryFn: fetchSession,
    staleTime: 60_000,
  });

  const loginMutation = useMutation({
    mutationFn: (v: { username: string; password: string }) => apiLogin(v.username, v.password),
    onSuccess: (session) => {
      qc.setQueryData(['session'], session);
      void qc.invalidateQueries();
      setOpen(false);
      toast.success(`Signed in as ${session.username}`);
    },
  });
  const logoutMutation = useMutation({
    mutationFn: apiLogout,
    onSuccess: () => {
      qc.setQueryData(['session'], null);
      void qc.invalidateQueries();
      toast.message('Signed out');
    },
    onError: (e) => {
      const m = describeError(e);
      toast.error(m.title, { description: m.message });
    },
  });

  const { mutateAsync: loginAsync } = loginMutation;
  const login = useCallback(
    (username: string, password: string) => loginAsync({ username, password }),
    [loginAsync],
  );
  const logout = logoutMutation.mutate;

  const value = useMemo<AuthContextValue>(
    () => ({
      session: query.data ?? null,
      loading: query.isPending,
      openLogin: () => setOpen(true),
      logout,
      login,
    }),
    [query.data, query.isPending, logout, login],
  );

  return (
    <AuthContext.Provider value={value}>
      {children}
      <LoginDialog open={open} onOpenChange={setOpen} onLogin={login} />
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>');
  return ctx;
}

/** Surfaces an error as a toast; a 401 additionally opens the sign-in dialog. */
export function useReportError() {
  const { openLogin } = useAuth();
  return useCallback(
    (error: unknown) => {
      const m = describeError(error);
      toast.error(m.title, { description: m.message });
      if (error instanceof ApiError && error.code === 'unauthorized') openLogin();
    },
    [openLogin],
  );
}
