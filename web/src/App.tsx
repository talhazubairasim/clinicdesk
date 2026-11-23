import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import type { ReactNode } from 'react';
import type { Role } from './api';
import { ADMIN, AuthProvider, FRONT_DESK, useAuth } from './auth';
import { Layout } from './components/Layout';
import { Login, Signup } from './pages/Auth';
import { Dashboard } from './pages/Dashboard';
import { Calendar } from './pages/Calendar';
import { Patients } from './pages/Patients';
import { Invoices } from './pages/Invoices';
import { Settings } from './pages/Settings';

function Guard({ roles, children }: { roles?: Role[]; children: ReactNode }) {
  const { user, can } = useAuth();
  if (!user) return <Navigate to="/login" replace />;
  if (roles && !can(...roles)) return <Navigate to="/" replace />;
  return <>{children}</>;
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route path="/signup" element={<Signup />} />
      <Route element={<Guard><Layout /></Guard>}>
        <Route index element={<Dashboard />} />
        <Route path="calendar" element={<Calendar />} />
        <Route path="patients" element={<Patients />} />
        <Route path="invoices" element={<Guard roles={FRONT_DESK}><Invoices /></Guard>} />
        <Route path="settings" element={<Guard roles={ADMIN}><Settings /></Guard>} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}><AppRoutes /></BrowserRouter>
    </AuthProvider>
  );
}
