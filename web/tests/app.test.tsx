import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError, request, session } from '../src/api';
import { desk, doctor, mockApi, owner, renderAt } from './helpers';

const usage = { practitioners: 1, maxPractitioners: 1, appointmentsThisMonth: 3, maxAppointmentsPerMonth: 50 };
const appt = (over: Record<string, unknown> = {}) => ({
  id: 'a1', patientId: 'p1', patientName: 'Hamza Iqbal', practitionerId: 'd1', practitionerName: 'Dr. Raza',
  startsAt: '2030-01-08T09:00:00Z', endsAt: '2030-01-08T09:30:00Z', status: 'Scheduled', reason: 'Checkup', cancellationReason: null, ...over,
});
const patient = { id: 'p1', firstName: 'Hamza', lastName: 'Iqbal', fullName: 'Hamza Iqbal', dateOfBirth: '1988-04-12', phone: '+92 300', email: 'h@x.dev', notes: null };
const doc = { id: 'd1', name: 'Dr. Raza', specialty: 'GP', slotMinutes: 30, hours: [] };
const paged = (items: unknown[]) => ({ items, page: 1, pageSize: 20, total: items.length });
const invoice = (over: Record<string, unknown> = {}) => ({
  id: 'i1', number: 'INV-2030-0001', patientId: 'p1', appointmentId: null, taxRate: 0, status: 'Issued', createdAt: '', issuedAt: '', dueAt: '2030-01-20T00:00:00Z',
  lines: [{ description: 'Consultation', quantity: 1, unitPrice: 3000 }], payments: [], subtotal: 3000, tax: 0, total: 3000, amountPaid: 0, balance: 3000, ...over,
});

describe('authentication', () => {
  it('redirects to login, signs in and shows the dashboard', async () => {
    const user = userEvent.setup();
    mockApi({
      'POST /api/auth/login': (init?: RequestInit) => JSON.parse(String(init!.body)).password === 'Demo#2025'
        ? { body: owner } : { status: 401, body: { title: 'Invalid clinic, email or password.' } },
      'GET /api/dashboard': { today: '2030-01-07', todayByStatus: { Scheduled: 2, Completed: 1 }, upcoming: [appt()], patients: 4, collectedThisMonth: 4950, outstanding: 1200, overdueInvoices: 1, usage },
    });
    renderAt('/');
    expect(await screen.findByRole('heading', { name: 'Sign in to your clinic' })).toBeInTheDocument();
    await user.type(screen.getByLabelText('Clinic address'), 'demo');
    await user.type(screen.getByLabelText('Email'), 'demo@clinicdesk.dev');
    await user.type(screen.getByLabelText('Password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid clinic');
    await user.clear(screen.getByLabelText('Password'));
    await user.type(screen.getByLabelText('Password'), 'Demo#2025');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('4,950.00')).toBeInTheDocument();
    expect(screen.getByText('1 overdue')).toBeInTheDocument();
    expect(screen.getByText('2 Scheduled · 1 Completed')).toBeInTheDocument();
    expect(screen.getByRole('meter', { name: 'Practitioners' })).toHaveAttribute('aria-valuenow', '1');
    await user.click(screen.getByText('Sign out'));
    expect(session.get()).toBeNull();
  });

  it('signs up a clinic with a slug derived from its name', async () => {
    const user = userEvent.setup();
    const calls = mockApi({ 'POST /api/clinics': { status: 201, body: owner }, 'GET /api/dashboard': { status: 500, body: { title: 'Boom' } } });
    renderAt('/signup');
    await user.type(screen.getByLabelText('Clinic name'), 'Sunrise Family Clinic!');
    expect(screen.getByLabelText('Clinic address')).toHaveValue('sunrise-family-clinic');
    await user.type(screen.getByLabelText('Your name'), 'Olivia');
    await user.type(screen.getByLabelText('Email'), 'o@x.dev');
    await user.type(screen.getByLabelText('Password'), 'Passw0rd!');
    await user.click(screen.getByRole('button', { name: 'Create clinic' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Boom');
    expect(calls[0].body).toMatchObject({ slug: 'sunrise-family-clinic', ownerName: 'Olivia' });
  });

  it('hides admin and billing pages from practitioners', async () => {
    mockApi({ 'GET /api/dashboard': { status: 403 } });
    renderAt('/settings', doctor);
    expect(await screen.findByRole('alert')).toHaveTextContent('Your role cannot do that.');
    expect(screen.queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Invoices' })).not.toBeInTheDocument();
  });
});

describe('calendar', () => {
  it('books a free slot and runs the visit workflow', async () => {
    const user = userEvent.setup();
    let list = [appt()];
    const calls = mockApi({
      'GET /api/practitioners': [doc],
      'GET /api/appointments': () => ({ body: list }),
      'GET /api/appointments/slots': [{ start: '2030-01-08T10:00:00Z', end: '2030-01-08T10:30:00Z' }],
      'GET /api/patients': paged([patient]),
      'POST /api/appointments': () => { list = [...list, appt({ id: 'a2', startsAt: '2030-01-08T10:00:00Z' })]; return { status: 201, body: list[1] }; },
      'POST /api/appointments/a1/check-in': () => { list = [appt({ status: 'CheckedIn' })]; return { body: list[0] }; },
      'POST /api/appointments/a1/cancel': { status: 409, body: { detail: 'A Completed appointment can\'t be cancelled.' } },
    });
    renderAt('/calendar', owner);
    expect(await screen.findByText('Hamza Iqbal')).toBeInTheDocument();

    const slots = await screen.findByLabelText('Free slots');
    await user.click(within(slots).getAllByRole('button')[0]);
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose a patient first.');
    await screen.findByRole('option', { name: /Hamza Iqbal · / });
    await user.selectOptions(screen.getByLabelText('Patient'), 'p1');
    await user.type(screen.getByLabelText('Reason'), 'Fever');
    await user.click(within(slots).getAllByRole('button')[0]);
    expect(await screen.findByRole('status')).toHaveTextContent('Booked');
    expect(calls.find((c) => c.key === 'POST /api/appointments')!.body).toMatchObject({ patientId: 'p1', practitionerId: 'd1', reason: 'Fever' });

    await user.click(screen.getAllByText('Cancel')[0]);
    expect(await screen.findByRole('alert')).toHaveTextContent("can't be cancelled");
    await user.click(screen.getAllByText('Check in')[0]);
    expect(await screen.findByText('Complete')).toBeInTheDocument();
  });

  it('lets receptionists book but not complete visits', async () => {
    mockApi({
      'GET /api/practitioners': [doc], 'GET /api/appointments': [appt({ status: 'CheckedIn' })],
      'GET /api/appointments/slots': [], 'GET /api/patients': paged([]),
    });
    renderAt('/calendar', desk);
    expect(await screen.findByText('Hamza Iqbal')).toBeInTheDocument();
    expect(screen.getByText('No free slots.')).toBeInTheDocument();
    expect(screen.queryByText('Complete')).not.toBeInTheDocument();
  });
});

describe('patients', () => {
  it('searches, pages and registers patients', async () => {
    const user = userEvent.setup();
    const calls = mockApi({
      'GET /api/patients': (_i: unknown, url: URL) => ({ body: { ...paged(url.searchParams.get('search') === 'zz' ? [] : [patient]), total: 25 } }),
      'POST /api/patients': (init?: RequestInit) => JSON.parse(String(init!.body)).phone === 'bad'
        ? { status: 400, body: { errors: { Phone: ['Enter a valid phone number.'] } } }
        : { status: 201, body: { ...patient, id: 'p2', fullName: 'Sara Khan' } },
    });
    renderAt('/patients', desk);
    expect(await screen.findByText('Hamza Iqbal')).toBeInTheDocument();
    expect(screen.getByText('Page 1 of 2')).toBeInTheDocument();
    await user.click(screen.getByText('Next ›'));
    expect(await screen.findByText('Page 2 of 2')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Search patients'), 'zz');
    expect(await screen.findByText('No patients found.')).toBeInTheDocument();

    await user.type(screen.getByLabelText('First name'), 'Sara');
    await user.type(screen.getByLabelText('Last name'), 'Khan');
    await user.type(screen.getByLabelText('Date of birth'), '1990-02-03');
    await user.type(screen.getByLabelText('Phone'), 'bad');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('valid phone');
    await user.clear(screen.getByLabelText('Phone'));
    await user.type(screen.getByLabelText('Phone'), '+92 300 1234567');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Sara Khan registered');
    expect(calls.filter((c) => c.key === 'POST /api/patients')[1].body).toMatchObject({ email: null, dateOfBirth: '1990-02-03' });
  });
});

describe('invoices', () => {
  it('creates a draft for a completed visit, issues it and records payments', async () => {
    const user = userEvent.setup();
    let invoices = [invoice({ status: 'Draft' })];
    const calls = mockApi({
      'GET /api/invoices': () => ({ body: invoices }),
      'GET /api/patients': paged([patient]),
      'GET /api/appointments': [appt({ status: 'Completed' }), appt({ id: 'a9', status: 'Cancelled' })],
      'POST /api/invoices': { status: 201, body: invoice({ id: 'i2', number: 'INV-2030-0002', status: 'Draft' }) },
      'POST /api/invoices/i1/issue': () => { invoices = [invoice()]; return { body: invoices[0] }; },
      'POST /api/invoices/i1/payments': () => { invoices = [invoice({ status: 'Paid', amountPaid: 3000, balance: 0 })]; return { body: invoices[0] }; },
    });
    renderAt('/invoices', desk);
    await user.click(await screen.findByText('Issue'));
    expect(await screen.findByRole('status')).toHaveTextContent('INV-2030-0001: issued');
    await user.type(await screen.findByLabelText('Payment for INV-2030-0001'), '3000');
    await user.selectOptions(screen.getByLabelText('Method'), 'Card');
    await user.click(screen.getByText('Record payment'));
    expect(await screen.findByText('payment recorded', { exact: false })).toBeInTheDocument();
    expect(calls.find((c) => c.key === 'POST /api/invoices/i1/payments')!.body).toEqual({ amount: 3000, method: 'Card' });

    await within(screen.getByLabelText('Invoice patient')).findByRole('option', { name: 'Hamza Iqbal' });
    await user.selectOptions(screen.getByLabelText('Invoice patient'), 'p1');
    await waitFor(() => expect(within(screen.getByLabelText('Visit')).getAllByRole('option')).toHaveLength(2));
    await user.selectOptions(screen.getByLabelText('Visit'), 'a1');
    await user.clear(screen.getByLabelText('Line 1 price'));
    await user.type(screen.getByLabelText('Line 1 price'), '2500');
    await user.click(screen.getByText('+ Add line'));
    await user.type(screen.getByLabelText('Line 2 description'), 'ECG');
    await user.clear(screen.getByLabelText('Tax %'));
    await user.type(screen.getByLabelText('Tax %'), '10');
    await user.click(screen.getByRole('button', { name: 'Create draft' }));
    expect(await screen.findByText('INV-2030-0002 created')).toBeInTheDocument();
    expect(calls.find((c) => c.key === 'POST /api/invoices')!.body).toMatchObject({
      patientId: 'p1', appointmentId: 'a1', taxRate: 0.1, lines: [{ description: 'Consultation', unitPrice: 2500 }, { description: 'ECG' }],
    });
  });

  it('filters by status and shows errors', async () => {
    const user = userEvent.setup();
    mockApi({
      'GET /api/invoices': (_i: unknown, url: URL) => ({ body: url.searchParams.get('status') === 'Void' ? [] : [invoice()] }),
      'GET /api/patients': paged([]),
      'POST /api/invoices/i1/void': { status: 409, body: { detail: 'Invoices with payments can\'t be voided.' } },
    });
    renderAt('/invoices', owner);
    await user.click(await screen.findByRole('button', { name: 'Void' }));
    expect(await screen.findByRole('alert')).toHaveTextContent("can't be voided");
    await user.selectOptions(screen.getByLabelText('Status filter'), 'Void');
    expect(await screen.findByText('No invoices.')).toBeInTheDocument();
  });
});

describe('settings', () => {
  it('manages plan, team and practitioners', async () => {
    const user = userEvent.setup();
    const clinic = { id: 't1', name: 'Sunrise', slug: 'sunrise', timeZone: 'UTC', plan: 'Free', status: 'Active', currentPeriodEnd: '2030-02-07T00:00:00Z', monthlyPrice: 0, usage, charges: [] };
    const calls = mockApi({
      'GET /api/clinic': clinic,
      'GET /api/staff': [{ id: 'u1', email: 'o@x.dev', fullName: 'Olivia Owner', role: 'Owner', active: true }, { id: 'u2', email: 'd@x.dev', fullName: 'Dana Desk', role: 'Receptionist', active: true }],
      'GET /api/practitioners': [doc],
      'PUT /api/clinic/plan': { ...clinic, plan: 'Pro' },
      'POST /api/staff': { status: 201, body: {} },
      'DELETE /api/staff/u2': { status: 204 },
      'POST /api/practitioners': { status: 402, body: { detail: 'The Free plan includes 1 practitioner(s). Upgrade to add more.' } },
    });
    renderAt('/settings', owner);
    expect(await screen.findByText('Current plan')).toBeInTheDocument();
    await user.click(screen.getByText('Switch to Pro'));
    expect(await screen.findByRole('status')).toHaveTextContent('Switched to Pro');

    await user.type(screen.getByLabelText('Staff name'), 'Rafi');
    await user.type(screen.getByLabelText('Staff email'), 'r@x.dev');
    await user.selectOptions(screen.getByLabelText('Role'), 'Practitioner');
    await user.type(screen.getByLabelText('Temporary password'), 'Passw0rd!');
    await user.click(screen.getByRole('button', { name: 'Add team member' }));
    await waitFor(() => expect(calls.find((c) => c.key === 'POST /api/staff')?.body).toMatchObject({ role: 'Practitioner', email: 'r@x.dev' }));
    expect(screen.getAllByText('Remove')).toHaveLength(1);
    await user.click(screen.getByText('Remove'));
    expect(await screen.findByText('Dana Desk deactivated')).toBeInTheDocument();

    await user.type(screen.getByLabelText('Practitioner name'), 'Dr. New');
    await user.type(screen.getByLabelText('Specialty'), 'Skin');
    await user.click(screen.getByLabelText('Sat'));
    await user.click(screen.getByRole('button', { name: 'Add practitioner' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Upgrade to add more');
    const sent = calls.find((c) => c.key === 'POST /api/practitioners')!.body as { hours: { day: number; start: string }[] };
    expect(sent.hours.map((h) => h.day)).toEqual([1, 2, 3, 4, 5, 6]);
    expect(sent.hours[0].start).toBe('09:00:00');
  });
});

describe('api client', () => {
  it('maps error bodies and empty responses', async () => {
    mockApi({ 'GET /a': { status: 401 }, 'GET /b': { status: 409, body: { detail: 'Nope' } }, 'GET /c': { status: 204 }, 'GET /d': { status: 200 } });
    await expect(request('/a')).rejects.toMatchObject({ status: 401, message: 'Please sign in.' });
    await expect(request('/b')).rejects.toEqual(new ApiError(409, 'Nope'));
    await expect(request('/c')).resolves.toBeUndefined();
    await expect(request('/d')).resolves.toBeUndefined();
    localStorage.setItem('clinicdesk.session', '{bad');
    expect(session.get()).toBeNull();
  });
});
