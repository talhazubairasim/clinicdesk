import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, type SignupInput } from '../api';
import { useAuth } from '../auth';
import { Alert, Field, button, input } from '../components/ui';

function Shell({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-teal-50 to-sky-50 p-4">
      <div className="w-full max-w-md rounded-2xl bg-white p-8 shadow-lg">
        <div className="mb-1 text-sm font-semibold text-teal-700">ClinicDesk</div>
        <h1 className="mb-4 text-2xl font-bold">{title}</h1>
        {children}
      </div>
    </div>
  );
}

export function Login() {
  const { signIn } = useAuth();
  const navigate = useNavigate();
  const [form, setForm] = useState({ clinic: '', email: '', password: '' });
  const [error, setError] = useState('');
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try { signIn(await api.login(form.clinic, form.email, form.password)); navigate('/'); }
    catch (err) { setError((err as Error).message); }
  };
  return (
    <Shell title="Sign in to your clinic">
      {error && <Alert>{error}</Alert>}
      <form onSubmit={submit} className="space-y-3">
        <Field label="Clinic address"><input required className={input} value={form.clinic} onChange={(e) => setForm({ ...form, clinic: e.target.value })} placeholder="demo" /></Field>
        <Field label="Email"><input required type="email" className={input} value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} /></Field>
        <Field label="Password"><input required type="password" className={input} value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} /></Field>
        <button className={`${button} w-full`}>Sign in</button>
      </form>
      <p className="mt-4 text-center text-sm">New clinic? <Link to="/signup" className="text-teal-700">Start free</Link></p>
    </Shell>
  );
}

const slugify = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 40);

export function Signup() {
  const { signIn } = useAuth();
  const navigate = useNavigate();
  const [form, setForm] = useState<SignupInput>({
    clinicName: '', slug: '', ownerName: '', email: '', password: '',
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
  });
  const [error, setError] = useState('');
  const set = (k: keyof SignupInput) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm((f) => ({ ...f, [k]: e.target.value, ...(k === 'clinicName' ? { slug: slugify(e.target.value) } : {}) }));
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try { signIn(await api.signup(form)); navigate('/'); } catch (err) { setError((err as Error).message); }
  };
  return (
    <Shell title="Create your clinic">
      {error && <Alert>{error}</Alert>}
      <form onSubmit={submit} className="space-y-3">
        <Field label="Clinic name"><input required className={input} value={form.clinicName} onChange={set('clinicName')} /></Field>
        <Field label="Clinic address"><input required className={input} value={form.slug} onChange={set('slug')} /></Field>
        <Field label="Time zone"><input required className={input} value={form.timeZone} onChange={set('timeZone')} /></Field>
        <Field label="Your name"><input required className={input} value={form.ownerName} onChange={set('ownerName')} /></Field>
        <Field label="Email"><input required type="email" className={input} value={form.email} onChange={set('email')} /></Field>
        <Field label="Password"><input required type="password" className={input} value={form.password} onChange={set('password')} /></Field>
        <button className={`${button} w-full`}>Create clinic</button>
      </form>
      <p className="mt-4 text-center text-sm"><Link to="/login" className="text-teal-700">I already have an account</Link></p>
    </Shell>
  );
}
