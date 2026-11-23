import { useEffect, useState } from 'react';
import { api, money, time, type Dashboard as Model } from '../api';
import { Alert, Badge, Card } from '../components/ui';

const Stat = ({ label, value, hint }: { label: string; value: string | number; hint?: string }) => (
  <div className="rounded-xl bg-white p-4 shadow-sm">
    <div className="text-xs uppercase tracking-wide text-slate-500">{label}</div>
    <div className="mt-1 text-2xl font-bold">{value}</div>
    {hint && <div className="text-xs text-slate-500">{hint}</div>}
  </div>
);

export function Dashboard() {
  const [data, setData] = useState<Model | null>(null);
  const [error, setError] = useState('');
  useEffect(() => { api.dashboard().then(setData).catch((e) => setError(e.message)); }, []);
  if (error) return <Alert>{error}</Alert>;
  if (!data) return <p>Loading…</p>;
  const todayTotal = Object.values(data.todayByStatus).reduce((a, b) => a + b, 0);
  const { usage } = data;
  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Today · {data.today}</h1>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Appointments today" value={todayTotal} hint={Object.entries(data.todayByStatus).map(([k, v]) => `${v} ${k}`).join(' · ') || 'None booked'} />
        <Stat label="Patients" value={data.patients} />
        <Stat label="Collected this month" value={money(data.collectedThisMonth)} />
        <Stat label="Outstanding" value={money(data.outstanding)} hint={data.overdueInvoices ? `${data.overdueInvoices} overdue` : 'Nothing overdue'} />
      </div>
      <div className="grid gap-4 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <Card title="Next appointments">
            <ul className="divide-y">
              {data.upcoming.map((a) => (
                <li key={a.id} className="flex items-center justify-between py-2 text-sm">
                  <span><strong>{new Date(a.startsAt).toLocaleDateString()} {time(a.startsAt)}</strong> · {a.patientName}</span>
                  <span className="text-slate-500">{a.practitionerName}</span>
                  <Badge status={a.status} />
                </li>
              ))}
              {data.upcoming.length === 0 && <li className="py-2 text-sm text-slate-500">Nothing scheduled.</li>}
            </ul>
          </Card>
        </div>
        <Card title="Plan usage">
          <Meter label="Practitioners" used={usage.practitioners} max={usage.maxPractitioners} />
          <Meter label="Appointments this month" used={usage.appointmentsThisMonth} max={usage.maxAppointmentsPerMonth} />
        </Card>
      </div>
    </div>
  );
}

export function Meter({ label, used, max }: { label: string; used: number; max: number }) {
  const unlimited = max >= 2_000_000_000;
  const pct = unlimited ? 5 : Math.min(100, Math.round((used / Math.max(max, 1)) * 100));
  return (
    <div className="mb-3 text-sm">
      <div className="flex justify-between"><span>{label}</span><span>{used} / {unlimited ? '∞' : max}</span></div>
      <div className="mt-1 h-2 rounded bg-slate-100" role="meter" aria-label={label} aria-valuenow={used} aria-valuemin={0} aria-valuemax={max}>
        <div className={`h-2 rounded ${pct >= 90 ? 'bg-red-500' : 'bg-teal-500'}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}
