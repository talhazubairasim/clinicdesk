import { useCallback, useEffect, useState } from 'react';
import { api, time, today, type Appointment, type Patient, type Practitioner, type Slot } from '../api';
import { CLINICAL, FRONT_DESK, useAuth } from '../auth';
import { Alert, Badge, Card, button, ghost, input } from '../components/ui';

/** Local-midnight bounds of a yyyy-mm-dd date, as ISO strings. */
const dayRange = (date: string) => {
  const from = new Date(`${date}T00:00:00`);
  const to = new Date(from.getTime() + 86_400_000);
  return { from: from.toISOString(), to: to.toISOString() };
};

export function Calendar() {
  const { can } = useAuth();
  const [date, setDate] = useState(today());
  const [practitioners, setPractitioners] = useState<Practitioner[]>([]);
  const [practitionerId, setPractitionerId] = useState('');
  const [appointments, setAppointments] = useState<Appointment[]>([]);
  const [slots, setSlots] = useState<Slot[]>([]);
  const [patients, setPatients] = useState<Patient[]>([]);
  const [patientSearch, setPatientSearch] = useState('');
  const [patientId, setPatientId] = useState('');
  const [reason, setReason] = useState('');
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  useEffect(() => {
    api.practitioners().then((p) => { setPractitioners(p); if (p[0]) setPractitionerId((id) => id || p[0].id); })
      .catch((e) => setMessage({ ok: false, text: e.message }));
  }, []);

  const load = useCallback(() => {
    if (!practitionerId) return;
    api.appointments({ ...dayRange(date), practitionerId }).then(setAppointments).catch((e) => setMessage({ ok: false, text: e.message }));
    api.slots(practitionerId, date).then(setSlots).catch(() => setSlots([]));
  }, [date, practitionerId]);
  useEffect(load, [load]);

  useEffect(() => {
    if (!can(...FRONT_DESK)) return;
    const t = setTimeout(() => api.patients(patientSearch).then((p) => setPatients(p.items)).catch(() => {}), 250);
    return () => clearTimeout(t);
  }, [patientSearch, can]);

  const act = async (p: Promise<unknown>, text: string) => {
    try { await p; setMessage({ ok: true, text }); load(); } catch (e) { setMessage({ ok: false, text: (e as Error).message }); }
  };

  const book = (slot: Slot) => {
    if (!patientId) return setMessage({ ok: false, text: 'Choose a patient first.' });
    act(api.book({ patientId, practitionerId, startsAt: slot.start, reason }), `Booked ${time(slot.start)}`);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="mr-auto text-2xl font-bold">Calendar</h1>
        <input aria-label="Date" type="date" value={date} onChange={(e) => setDate(e.target.value)} className="rounded-md border px-3 py-2" />
        <select aria-label="Practitioner" value={practitionerId} onChange={(e) => setPractitionerId(e.target.value)} className="rounded-md border px-3 py-2">
          {practitioners.map((p) => <option key={p.id} value={p.id}>{p.name} · {p.specialty}</option>)}
        </select>
      </div>
      {message && <Alert kind={message.ok ? 'success' : 'error'}>{message.text}</Alert>}
      {practitioners.length === 0 && <p className="text-slate-500">Add a practitioner in Settings to start booking.</p>}

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <Card title="Appointments">
            <ul className="divide-y">
              {appointments.map((a) => (
                <li key={a.id} className="flex flex-wrap items-center gap-2 py-2 text-sm">
                  <span className="w-28 font-medium">{time(a.startsAt)}–{time(a.endsAt)}</span>
                  <span className="flex-1">{a.patientName}{a.reason && <span className="text-slate-500"> · {a.reason}</span>}</span>
                  <Badge status={a.status} />
                  {a.status === 'Scheduled' && can(...FRONT_DESK) && <>
                    <button className={ghost} onClick={() => act(api.transition(a.id, 'check-in'), `${a.patientName} checked in`)}>Check in</button>
                    <button className={ghost} onClick={() => act(api.transition(a.id, 'no-show'), 'Marked as no-show')}>No-show</button>
                    <button className={ghost} onClick={() => act(api.cancel(a.id, 'Cancelled by front desk'), 'Appointment cancelled')}>Cancel</button>
                  </>}
                  {a.status === 'CheckedIn' && can(...CLINICAL) &&
                    <button className={ghost} onClick={() => act(api.transition(a.id, 'complete'), 'Visit completed')}>Complete</button>}
                </li>
              ))}
              {appointments.length === 0 && <li className="py-2 text-sm text-slate-500">No appointments on this day.</li>}
            </ul>
          </Card>
        </div>
        {can(...FRONT_DESK) && (
          <Card title="Book">
            <input aria-label="Find patient" placeholder="Find patient" value={patientSearch} onChange={(e) => setPatientSearch(e.target.value)} className={`${input} mb-2`} />
            <select aria-label="Patient" value={patientId} onChange={(e) => setPatientId(e.target.value)} className={`${input} mb-2`}>
              <option value="">Choose a patient…</option>
              {patients.map((p) => <option key={p.id} value={p.id}>{p.fullName} · {p.phone}</option>)}
            </select>
            <input aria-label="Reason" placeholder="Reason (optional)" value={reason} onChange={(e) => setReason(e.target.value)} className={`${input} mb-3`} />
            <div className="grid grid-cols-3 gap-2" aria-label="Free slots">
              {slots.map((s) => <button key={s.start} className={button} onClick={() => book(s)}>{time(s.start)}</button>)}
            </div>
            {slots.length === 0 && <p className="text-sm text-slate-500">No free slots.</p>}
          </Card>
        )}
      </div>
    </div>
  );
}
