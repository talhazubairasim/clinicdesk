import type { ReactNode } from 'react';
import type { AppointmentStatus, InvoiceStatus } from '../api';

export const Alert = ({ kind = 'error', children }: { kind?: 'error' | 'success'; children: ReactNode }) => (
  <div role={kind === 'error' ? 'alert' : 'status'} className={`mb-3 rounded-md p-3 text-sm ${kind === 'error' ? 'bg-red-50 text-red-800' : 'bg-emerald-50 text-emerald-800'}`}>{children}</div>
);

export const Card = ({ title, children, action }: { title?: string; children: ReactNode; action?: ReactNode }) => (
  <section className="rounded-xl bg-white p-4 shadow-sm">
    {(title || action) && <div className="mb-3 flex items-center justify-between"><h2 className="font-semibold">{title}</h2>{action}</div>}
    {children}
  </section>
);

const tone: Record<string, string> = {
  Scheduled: 'bg-sky-100 text-sky-800', CheckedIn: 'bg-amber-100 text-amber-800', Completed: 'bg-emerald-100 text-emerald-800',
  Cancelled: 'bg-slate-200 text-slate-700', NoShow: 'bg-red-100 text-red-800',
  Draft: 'bg-slate-200 text-slate-700', Issued: 'bg-sky-100 text-sky-800', PartiallyPaid: 'bg-amber-100 text-amber-800',
  Paid: 'bg-emerald-100 text-emerald-800', Void: 'bg-red-100 text-red-800',
};
export const Badge = ({ status }: { status: AppointmentStatus | InvoiceStatus }) =>
  <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${tone[status]}`}>{status.replace(/([a-z])([A-Z])/g, '$1 $2')}</span>;

export const Field = ({ label, children }: { label: string; children: ReactNode }) => (
  <label className="block text-sm">{label}<div className="mt-1">{children}</div></label>
);

export const input = 'w-full rounded-md border border-slate-300 px-3 py-2';
export const button = 'rounded-md bg-teal-600 px-3 py-2 text-sm font-medium text-white hover:bg-teal-700 disabled:opacity-50';
export const ghost = 'rounded-md border border-slate-300 px-3 py-1.5 text-sm hover:bg-slate-50';
