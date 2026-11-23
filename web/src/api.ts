export type Role = 'Owner' | 'Admin' | 'Practitioner' | 'Receptionist';
export type Plan = 'Free' | 'Pro' | 'Enterprise';
export type AppointmentStatus = 'Scheduled' | 'CheckedIn' | 'Completed' | 'Cancelled' | 'NoShow';
export type InvoiceStatus = 'Draft' | 'Issued' | 'PartiallyPaid' | 'Paid' | 'Void';
export type PaymentMethod = 'Cash' | 'Card' | 'BankTransfer' | 'Insurance';

export interface Session { token: string; userId: string; fullName: string; email: string; role: Role; tenantId: string; clinic: string; clinicSlug: string }
export interface Usage { practitioners: number; maxPractitioners: number; appointmentsThisMonth: number; maxAppointmentsPerMonth: number }
export interface Charge { id: string; plan: Plan; amount: number; periodStart: string; periodEnd: string; description: string }
export interface ClinicInfo { id: string; name: string; slug: string; timeZone: string; plan: Plan; status: string; currentPeriodEnd: string; monthlyPrice: number; usage: Usage; charges: Charge[] }
export interface WorkingHours { day: number | string; start: string; end: string }
export interface Practitioner { id: string; name: string; specialty: string; slotMinutes: number; hours: WorkingHours[] }
export interface Patient { id: string; firstName: string; lastName: string; fullName: string; dateOfBirth: string; phone: string; email: string | null; notes: string | null }
export interface Paged<T> { items: T[]; page: number; pageSize: number; total: number }
export interface Appointment { id: string; patientId: string; patientName: string; practitionerId: string; practitionerName: string; startsAt: string; endsAt: string; status: AppointmentStatus; reason: string; cancellationReason: string | null }
export interface Slot { start: string; end: string }
export interface InvoiceLine { description: string; quantity: number; unitPrice: number; amount?: number }
export interface Payment { id: string; amount: number; method: PaymentMethod; paidAt: string; reference: string | null }
export interface Invoice { id: string; number: string; patientId: string; appointmentId: string | null; taxRate: number; status: InvoiceStatus; createdAt: string; issuedAt: string | null; dueAt: string | null; lines: InvoiceLine[]; payments: Payment[]; subtotal: number; tax: number; total: number; amountPaid: number; balance: number }
export interface Staff { id: string; email: string; fullName: string; role: Role; active: boolean }
export interface Dashboard { today: string; todayByStatus: Record<string, number>; upcoming: Appointment[]; patients: number; collectedThisMonth: number; outstanding: number; overdueInvoices: number; usage: Usage }
export interface PatientInput { firstName: string; lastName: string; dateOfBirth: string; phone: string; email: string | null; notes: string | null }
export interface SignupInput { clinicName: string; slug: string; timeZone: string; ownerName: string; email: string; password: string }

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

const KEY = 'clinicdesk.session';
export const session = {
  get(): Session | null {
    try { return JSON.parse(localStorage.getItem(KEY) ?? 'null'); } catch { return null; }
  },
  set(s: Session | null) { if (s) localStorage.setItem(KEY, JSON.stringify(s)); else localStorage.removeItem(KEY); },
};

async function errorMessage(res: Response): Promise<string> {
  try {
    const body = await res.json();
    if (body.errors) return Object.values(body.errors as Record<string, string[]>).flat().join(' ');
    return body.detail ?? body.title ?? res.statusText;
  } catch {
    return res.status === 401 ? 'Please sign in.' : res.status === 403 ? 'Your role cannot do that.' : res.statusText || 'Request failed';
  }
}

export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = session.get()?.token;
  const res = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}), ...init.headers },
  });
  if (!res.ok) throw new ApiError(res.status, await errorMessage(res));
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const post = <T>(path: string, body: unknown = {}) => request<T>(path, { method: 'POST', body: JSON.stringify(body) });
const q = (params: Record<string, string | undefined>) =>
  new URLSearchParams(Object.entries(params).filter(([, v]) => v) as [string, string][]).toString();

export const api = {
  signup: (input: SignupInput) => post<Session>('/api/clinics', input),
  login: (clinic: string, email: string, password: string) => post<Session>('/api/auth/login', { clinic, email, password }),
  clinic: () => request<ClinicInfo>('/api/clinic'),
  changePlan: (plan: Plan) => request<ClinicInfo>('/api/clinic/plan', { method: 'PUT', body: JSON.stringify({ plan }) }),
  dashboard: () => request<Dashboard>('/api/dashboard'),
  staff: () => request<Staff[]>('/api/staff'),
  addStaff: (s: { email: string; fullName: string; role: Role; password: string }) => post<Staff>('/api/staff', s),
  removeStaff: (id: string) => request<void>(`/api/staff/${id}`, { method: 'DELETE' }),
  practitioners: () => request<Practitioner[]>('/api/practitioners'),
  addPractitioner: (p: Omit<Practitioner, 'id'>) => post<Practitioner>('/api/practitioners', p),
  patients: (search = '', page = 1) => request<Paged<Patient>>(`/api/patients?${q({ search, page: String(page) })}`),
  addPatient: (p: PatientInput) => post<Patient>('/api/patients', p),
  appointments: (params: { from?: string; to?: string; practitionerId?: string; patientId?: string }) =>
    request<Appointment[]>(`/api/appointments?${q(params)}`),
  slots: (practitionerId: string, date: string) => request<Slot[]>(`/api/appointments/slots?${q({ practitionerId, date })}`),
  book: (b: { patientId: string; practitionerId: string; startsAt: string; reason: string }) => post<Appointment>('/api/appointments', b),
  transition: (id: string, action: 'check-in' | 'complete' | 'no-show') => post<Appointment>(`/api/appointments/${id}/${action}`),
  cancel: (id: string, reason: string) => post<Appointment>(`/api/appointments/${id}/cancel`, { reason }),
  invoices: (status?: InvoiceStatus) => request<Invoice[]>(`/api/invoices?${q({ status })}`),
  createInvoice: (i: { patientId: string; appointmentId: string | null; taxRate: number; lines: InvoiceLine[] }) => post<Invoice>('/api/invoices', i),
  issue: (id: string) => post<Invoice>(`/api/invoices/${id}/issue`, { dueInDays: 14 }),
  pay: (id: string, amount: number, method: PaymentMethod) => post<Invoice>(`/api/invoices/${id}/payments`, { amount, method }),
  voidInvoice: (id: string) => post<Invoice>(`/api/invoices/${id}/void`),
};

export const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
export const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
export const today = () => new Date().toISOString().slice(0, 10);
