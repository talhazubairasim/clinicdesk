import { useEffect, useState, type FormEvent } from 'react';
import { api, type Patient, type PatientInput } from '../api';
import { Alert, Card, Field, button, input } from '../components/ui';

const blank: PatientInput = { firstName: '', lastName: '', dateOfBirth: '', phone: '', email: '', notes: '' };

export function Patients() {
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<{ items: Patient[]; total: number; pageSize: number }>({ items: [], total: 0, pageSize: 20 });
  const [form, setForm] = useState<PatientInput>(blank);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    const t = setTimeout(() => api.patients(search, page).then(setData).catch((e) => setMessage({ ok: false, text: e.message })), 250);
    return () => clearTimeout(t);
  }, [search, page, reload]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    try {
      const p = await api.addPatient({ ...form, email: form.email || null, notes: form.notes || null });
      setMessage({ ok: true, text: `${p.fullName} registered` });
      setForm(blank);
      setReload((r) => r + 1);
    } catch (err) { setMessage({ ok: false, text: (err as Error).message }); }
  };
  const set = (k: keyof PatientInput) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value });
  const pages = Math.max(1, Math.ceil(data.total / data.pageSize));

  return (
    <div className="grid gap-4 lg:grid-cols-3">
      <div className="space-y-3 lg:col-span-2">
        <h1 className="text-2xl font-bold">Patients</h1>
        <input aria-label="Search patients" placeholder="Search by name, phone or email" value={search}
          onChange={(e) => { setSearch(e.target.value); setPage(1); }} className={input} />
        <Card>
          <table className="w-full text-sm">
            <thead><tr className="text-left text-slate-500"><th className="py-1">Name</th><th>Born</th><th>Phone</th><th>Email</th></tr></thead>
            <tbody>
              {data.items.map((p) => (
                <tr key={p.id} className="border-t"><td className="py-2 font-medium">{p.fullName}</td><td>{p.dateOfBirth}</td><td>{p.phone}</td><td>{p.email ?? '—'}</td></tr>
              ))}
            </tbody>
          </table>
          {data.items.length === 0 && <p className="py-2 text-sm text-slate-500">No patients found.</p>}
          <div className="mt-3 flex items-center justify-between text-sm">
            <span>{data.total} patient{data.total === 1 ? '' : 's'}</span>
            <span className="space-x-2">
              <button disabled={page <= 1} onClick={() => setPage(page - 1)} className="disabled:opacity-40">‹ Prev</button>
              <span>Page {page} of {pages}</span>
              <button disabled={page >= pages} onClick={() => setPage(page + 1)} className="disabled:opacity-40">Next ›</button>
            </span>
          </div>
        </Card>
      </div>
      <Card title="Register patient">
        {message && <Alert kind={message.ok ? 'success' : 'error'}>{message.text}</Alert>}
        <form onSubmit={submit} className="space-y-2">
          <Field label="First name"><input required className={input} value={form.firstName} onChange={set('firstName')} /></Field>
          <Field label="Last name"><input required className={input} value={form.lastName} onChange={set('lastName')} /></Field>
          <Field label="Date of birth"><input required type="date" className={input} value={form.dateOfBirth} onChange={set('dateOfBirth')} /></Field>
          <Field label="Phone"><input required className={input} value={form.phone} onChange={set('phone')} /></Field>
          <Field label="Email"><input type="email" className={input} value={form.email ?? ''} onChange={set('email')} /></Field>
          <button className={`${button} w-full`}>Register</button>
        </form>
      </Card>
    </div>
  );
}
