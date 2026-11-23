import { useEffect, useState, type FormEvent } from 'react';
import { api, money, type ClinicInfo, type Plan, type Practitioner, type Role, type Staff } from '../api';
import { Alert, Card, Field, button, ghost, input } from '../components/ui';
import { Meter } from './Dashboard';

const PLANS: { plan: Plan; price: number; features: string }[] = [
  { plan: 'Free', price: 0, features: '1 practitioner · 50 appointments / month' },
  { plan: 'Pro', price: 49, features: '5 practitioners · 1,000 appointments / month' },
  { plan: 'Enterprise', price: 199, features: 'Unlimited practitioners and appointments' },
];
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

export function Settings() {
  const [clinic, setClinic] = useState<ClinicInfo | null>(null);
  const [staff, setStaff] = useState<Staff[]>([]);
  const [practitioners, setPractitioners] = useState<Practitioner[]>([]);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  const load = () => {
    api.clinic().then(setClinic).catch((e) => setMessage({ ok: false, text: e.message }));
    api.staff().then(setStaff).catch(() => {});
    api.practitioners().then(setPractitioners).catch(() => {});
  };
  useEffect(load, []);
  const run = async (p: Promise<unknown>, text: string) => {
    try { await p; setMessage({ ok: true, text }); load(); } catch (e) { setMessage({ ok: false, text: (e as Error).message }); }
  };

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold">Settings</h1>
      {message && <Alert kind={message.ok ? 'success' : 'error'}>{message.text}</Alert>}
      {clinic && (
        <Card title={`Subscription · ${clinic.status}`}>
          <div className="mb-4 grid gap-3 md:grid-cols-3">
            {PLANS.map((p) => (
              <div key={p.plan} aria-label={`${p.plan} plan`} className={`rounded-lg border p-3 ${clinic.plan === p.plan ? 'border-teal-600 ring-2 ring-teal-100' : ''}`}>
                <div className="font-semibold">{p.plan}</div>
                <div className="text-xl font-bold">${p.price}<span className="text-sm font-normal">/mo</span></div>
                <div className="mb-2 text-xs text-slate-500">{p.features}</div>
                {clinic.plan === p.plan ? <span className="text-sm text-teal-700">Current plan</span>
                  : <button className={ghost} onClick={() => run(api.changePlan(p.plan), `Switched to ${p.plan}`)}>Switch to {p.plan}</button>}
              </div>
            ))}
          </div>
          <Meter label="Practitioners" used={clinic.usage.practitioners} max={clinic.usage.maxPractitioners} />
          <Meter label="Appointments this month" used={clinic.usage.appointmentsThisMonth} max={clinic.usage.maxAppointmentsPerMonth} />
          <p className="text-sm text-slate-500">Renews {clinic.currentPeriodEnd.slice(0, 10)}</p>
          {clinic.charges.length > 0 && (
            <ul className="mt-2 text-sm">
              {clinic.charges.map((c) => <li key={c.id}>{c.periodStart.slice(0, 10)} · {c.description} · {c.plan} · ${money(c.amount)}</li>)}
            </ul>
          )}
        </Card>
      )}
      <div className="grid gap-4 lg:grid-cols-2">
        <Card title="Team">
          <ul className="mb-3 divide-y text-sm">
            {staff.map((s) => (
              <li key={s.id} className="flex items-center justify-between py-2">
                <span>{s.fullName} <span className="text-slate-500">· {s.email}</span></span>
                <span className="flex items-center gap-2">{s.role}{!s.active && <em className="text-slate-400">inactive</em>}
                  {s.active && s.role !== 'Owner' && <button className="text-red-600" onClick={() => run(api.removeStaff(s.id), `${s.fullName} deactivated`)}>Remove</button>}</span>
              </li>
            ))}
          </ul>
          <AddStaff onAdd={(s) => run(api.addStaff(s), `${s.fullName} added`)} />
        </Card>
        <Card title="Practitioners">
          <ul className="mb-3 divide-y text-sm">
            {practitioners.map((p) => <li key={p.id} className="py-2">{p.name} · {p.specialty} · {p.slotMinutes}-minute slots · {p.hours.length} working windows</li>)}
          </ul>
          <AddPractitioner onAdd={(p) => run(api.addPractitioner(p), `${p.name} added`)} />
        </Card>
      </div>
    </div>
  );
}

function AddStaff({ onAdd }: { onAdd: (s: { email: string; fullName: string; role: Role; password: string }) => void }) {
  const [s, setS] = useState({ email: '', fullName: '', role: 'Receptionist' as Role, password: '' });
  const submit = (e: FormEvent) => { e.preventDefault(); onAdd(s); setS({ ...s, email: '', fullName: '', password: '' }); };
  return (
    <form onSubmit={submit} className="grid grid-cols-2 gap-2">
      <input aria-label="Staff name" required placeholder="Full name" className={input} value={s.fullName} onChange={(e) => setS({ ...s, fullName: e.target.value })} />
      <input aria-label="Staff email" required type="email" placeholder="Email" className={input} value={s.email} onChange={(e) => setS({ ...s, email: e.target.value })} />
      <select aria-label="Role" className={input} value={s.role} onChange={(e) => setS({ ...s, role: e.target.value as Role })}>
        {['Admin', 'Practitioner', 'Receptionist'].map((r) => <option key={r}>{r}</option>)}
      </select>
      <input aria-label="Temporary password" required type="password" placeholder="Temporary password" className={input} value={s.password} onChange={(e) => setS({ ...s, password: e.target.value })} />
      <button className={`${button} col-span-2`}>Add team member</button>
    </form>
  );
}

function AddPractitioner({ onAdd }: { onAdd: (p: Omit<Practitioner, 'id'>) => void }) {
  const [name, setName] = useState('');
  const [specialty, setSpecialty] = useState('');
  const [slotMinutes, setSlot] = useState(30);
  const [days, setDays] = useState([1, 2, 3, 4, 5]);
  const [start, setStart] = useState('09:00');
  const [end, setEnd] = useState('17:00');
  const submit = (e: FormEvent) => {
    e.preventDefault();
    onAdd({ name, specialty, slotMinutes, hours: days.map((day) => ({ day, start: `${start}:00`, end: `${end}:00` })) });
    setName('');
    setSpecialty('');
  };
  return (
    <form onSubmit={submit} className="space-y-2">
      <div className="grid grid-cols-2 gap-2">
        <input aria-label="Practitioner name" required placeholder="Name" className={input} value={name} onChange={(e) => setName(e.target.value)} />
        <input aria-label="Specialty" required placeholder="Specialty" className={input} value={specialty} onChange={(e) => setSpecialty(e.target.value)} />
      </div>
      <div className="flex flex-wrap gap-2 text-xs">
        {DAYS.map((d, i) => (
          <label key={d} className="flex items-center gap-1">
            <input type="checkbox" checked={days.includes(i)} onChange={() => setDays(days.includes(i) ? days.filter((x) => x !== i) : [...days, i].sort())} />{d.slice(0, 3)}
          </label>
        ))}
      </div>
      <div className="grid grid-cols-3 gap-2">
        <Field label="From"><input aria-label="From" type="time" className={input} value={start} onChange={(e) => setStart(e.target.value)} /></Field>
        <Field label="To"><input aria-label="To" type="time" className={input} value={end} onChange={(e) => setEnd(e.target.value)} /></Field>
        <Field label="Slot (min)"><input aria-label="Slot minutes" type="number" min={5} max={240} className={input} value={slotMinutes} onChange={(e) => setSlot(Number(e.target.value))} /></Field>
      </div>
      <button className={`${button} w-full`}>Add practitioner</button>
    </form>
  );
}
