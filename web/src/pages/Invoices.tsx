import { useEffect, useState, type FormEvent } from 'react';
import { api, money, type Appointment, type Invoice, type InvoiceLine, type InvoiceStatus, type Patient, type PaymentMethod } from '../api';
import { Alert, Badge, Card, Field, button, ghost, input } from '../components/ui';

const STATUSES: InvoiceStatus[] = ['Draft', 'Issued', 'PartiallyPaid', 'Paid', 'Void'];

export function Invoices() {
  const [status, setStatus] = useState<InvoiceStatus | ''>('');
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [reload, setReload] = useState(0);

  useEffect(() => { api.invoices(status || undefined).then(setInvoices).catch((e) => setMessage({ ok: false, text: e.message })); }, [status, reload]);

  const act = async (p: Promise<Invoice>, text: string) => {
    try { const i = await p; setMessage({ ok: true, text: `${i.number}: ${text}` }); setReload((r) => r + 1); }
    catch (e) { setMessage({ ok: false, text: (e as Error).message }); }
  };

  return (
    <div className="grid gap-4 lg:grid-cols-3">
      <div className="space-y-3 lg:col-span-2">
        <div className="flex items-center justify-between">
          <h1 className="text-2xl font-bold">Invoices</h1>
          <select aria-label="Status filter" value={status} onChange={(e) => setStatus(e.target.value as InvoiceStatus | '')} className="rounded-md border px-3 py-2">
            <option value="">All statuses</option>
            {STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </div>
        {message && <Alert kind={message.ok ? 'success' : 'error'}>{message.text}</Alert>}
        {invoices.map((i) => <InvoiceRow key={i.id} invoice={i} act={act} />)}
        {invoices.length === 0 && <p className="text-sm text-slate-500">No invoices.</p>}
      </div>
      <NewInvoice onCreated={(i) => { setMessage({ ok: true, text: `${i.number} created` }); setReload((r) => r + 1); }} />
    </div>
  );
}

function InvoiceRow({ invoice: i, act }: { invoice: Invoice; act: (p: Promise<Invoice>, text: string) => void }) {
  const [amount, setAmount] = useState('');
  const [method, setMethod] = useState<PaymentMethod>('Cash');
  return (
    <article aria-label={i.number} className="rounded-xl bg-white p-4 shadow-sm">
      <div className="flex items-center justify-between">
        <span className="font-semibold">{i.number}</span>
        <Badge status={i.status} />
      </div>
      <ul className="my-2 text-sm text-slate-600">
        {i.lines.map((l, n) => <li key={n}>{l.quantity} × {l.description} @ {money(l.unitPrice)}</li>)}
      </ul>
      <div className="text-sm">Total <strong>{money(i.total)}</strong> · Paid {money(i.amountPaid)} · Balance <strong>{money(i.balance)}</strong>
        {i.dueAt && <span className="text-slate-500"> · due {i.dueAt.slice(0, 10)}</span>}</div>
      <div className="mt-2 flex flex-wrap gap-2">
        {i.status === 'Draft' && <button className={ghost} onClick={() => act(api.issue(i.id), 'issued')}>Issue</button>}
        {(i.status === 'Draft' || i.status === 'Issued') && <button className={ghost} onClick={() => act(api.voidInvoice(i.id), 'voided')}>Void</button>}
        {(i.status === 'Issued' || i.status === 'PartiallyPaid') && (
          <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); act(api.pay(i.id, Number(amount), method), 'payment recorded'); setAmount(''); }}>
            <input aria-label={`Payment for ${i.number}`} type="number" step="0.01" min="0.01" required placeholder={money(i.balance)} value={amount}
              onChange={(e) => setAmount(e.target.value)} className="w-28 rounded border px-2 py-1 text-sm" />
            <select aria-label="Method" value={method} onChange={(e) => setMethod(e.target.value as PaymentMethod)} className="rounded border px-2 py-1 text-sm">
              {['Cash', 'Card', 'BankTransfer', 'Insurance'].map((m) => <option key={m}>{m}</option>)}
            </select>
            <button className={ghost}>Record payment</button>
          </form>
        )}
      </div>
    </article>
  );
}

function NewInvoice({ onCreated }: { onCreated: (i: Invoice) => void }) {
  const [search, setSearch] = useState('');
  const [patients, setPatients] = useState<Patient[]>([]);
  const [patientId, setPatientId] = useState('');
  const [visits, setVisits] = useState<Appointment[]>([]);
  const [appointmentId, setAppointmentId] = useState('');
  const [taxRate, setTaxRate] = useState('0');
  const [lines, setLines] = useState<InvoiceLine[]>([{ description: 'Consultation', quantity: 1, unitPrice: 0 }]);
  const [error, setError] = useState('');

  useEffect(() => {
    const t = setTimeout(() => api.patients(search).then((p) => setPatients(p.items)).catch(() => {}), 250);
    return () => clearTimeout(t);
  }, [search]);
  useEffect(() => {
    if (!patientId) { setVisits([]); return; }
    api.appointments({ patientId }).then((a) => setVisits(a.filter((x) => x.status === 'Completed'))).catch(() => setVisits([]));
  }, [patientId]);

  const setLine = (n: number, patch: Partial<InvoiceLine>) => setLines(lines.map((l, i) => (i === n ? { ...l, ...patch } : l)));
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      onCreated(await api.createInvoice({ patientId, appointmentId: appointmentId || null, taxRate: Number(taxRate) / 100, lines }));
      setLines([{ description: 'Consultation', quantity: 1, unitPrice: 0 }]);
      setAppointmentId('');
    } catch (err) { setError((err as Error).message); }
  };

  return (
    <Card title="New invoice">
      {error && <Alert>{error}</Alert>}
      <form onSubmit={submit} className="space-y-2">
        <input aria-label="Find patient" placeholder="Find patient" value={search} onChange={(e) => setSearch(e.target.value)} className={input} />
        <select aria-label="Invoice patient" required value={patientId} onChange={(e) => setPatientId(e.target.value)} className={input}>
          <option value="">Choose a patient…</option>
          {patients.map((p) => <option key={p.id} value={p.id}>{p.fullName}</option>)}
        </select>
        <select aria-label="Visit" value={appointmentId} onChange={(e) => setAppointmentId(e.target.value)} className={input}>
          <option value="">No linked visit</option>
          {visits.map((v) => <option key={v.id} value={v.id}>{new Date(v.startsAt).toLocaleDateString()} · {v.practitionerName}</option>)}
        </select>
        {lines.map((l, n) => (
          <div key={n} className="flex gap-1">
            <input aria-label={`Line ${n + 1} description`} className={`${input} flex-1`} value={l.description} onChange={(e) => setLine(n, { description: e.target.value })} />
            <input aria-label={`Line ${n + 1} quantity`} type="number" min={1} className="w-14 rounded border px-1" value={l.quantity} onChange={(e) => setLine(n, { quantity: Number(e.target.value) })} />
            <input aria-label={`Line ${n + 1} price`} type="number" min={0} step="0.01" className="w-24 rounded border px-1" value={l.unitPrice} onChange={(e) => setLine(n, { unitPrice: Number(e.target.value) })} />
          </div>
        ))}
        <button type="button" className="text-sm text-teal-700" onClick={() => setLines([...lines, { description: '', quantity: 1, unitPrice: 0 }])}>+ Add line</button>
        <Field label="Tax %"><input aria-label="Tax %" type="number" min={0} max={50} className={input} value={taxRate} onChange={(e) => setTaxRate(e.target.value)} /></Field>
        <button className={`${button} w-full`}>Create draft</button>
      </form>
    </Card>
  );
}
