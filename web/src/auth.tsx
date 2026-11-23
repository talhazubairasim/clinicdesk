import { createContext, useContext, useState, type ReactNode } from 'react';
import { session, type Role, type Session } from './api';

interface AuthState {
  user: Session | null;
  can: (...roles: Role[]) => boolean;
  signIn: (s: Session) => void;
  signOut: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

export const ADMIN: Role[] = ['Owner', 'Admin'];
export const FRONT_DESK: Role[] = ['Owner', 'Admin', 'Receptionist'];
export const CLINICAL: Role[] = ['Owner', 'Admin', 'Practitioner'];

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<Session | null>(() => session.get());
  const value: AuthState = {
    user,
    can: (...roles) => !!user && roles.includes(user.role),
    signIn: (s) => { session.set(s); setUser(s); },
    signOut: () => { session.set(null); setUser(null); },
  };
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
