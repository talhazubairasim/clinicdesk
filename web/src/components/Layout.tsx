import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { ADMIN, FRONT_DESK, useAuth } from '../auth';

const link = ({ isActive }: { isActive: boolean }) =>
  `block rounded-md px-3 py-2 text-sm ${isActive ? 'bg-teal-600 text-white' : 'text-slate-700 hover:bg-slate-100'}`;

export function Layout() {
  const { user, can, signOut } = useAuth();
  const navigate = useNavigate();
  return (
    <div className="flex min-h-screen">
      <aside className="w-56 shrink-0 border-r bg-white p-4">
        <div className="mb-6">
          <div className="text-lg font-bold text-teal-700">ClinicDesk</div>
          <div className="text-xs text-slate-500">{user?.clinic}</div>
        </div>
        <nav className="space-y-1">
          <NavLink to="/" end className={link}>Dashboard</NavLink>
          <NavLink to="/calendar" className={link}>Calendar</NavLink>
          <NavLink to="/patients" className={link}>Patients</NavLink>
          {can(...FRONT_DESK) && <NavLink to="/invoices" className={link}>Invoices</NavLink>}
          {can(...ADMIN) && <NavLink to="/settings" className={link}>Settings</NavLink>}
        </nav>
        <div className="mt-8 border-t pt-4 text-sm">
          <div className="font-medium">{user?.fullName}</div>
          <div className="text-xs text-slate-500">{user?.role}</div>
          <button className="mt-2 text-teal-700" onClick={() => { signOut(); navigate('/login'); }}>Sign out</button>
        </div>
      </aside>
      <main className="flex-1 p-6"><Outlet /></main>
    </div>
  );
}
