import { FormEvent, useEffect, useState } from 'react';
import { api } from '../api';

/** SCR-01. Domain PCs never see this (Windows auth); phones and non-domain PCs sign in with their AD account. */
export default function Login({ message, onSignedIn }: { message?: string; onSignedIn: () => void }) {
  const [formLogin, setFormLogin] = useState<boolean | null>(null);
  const [user, setUser] = useState('');
  const [pass, setPass] = useState('');
  const [error, setError] = useState(message ?? '');
  const [busy, setBusy] = useState(false);

  useEffect(() => { api.get<{ formLogin: boolean }>('/auth/options').then((o) => setFormLogin(o.formLogin)).catch(() => setFormLogin(false)); }, []);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      await api.post('/auth/login', { userName: user, password: pass });
      onSignedIn();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="center-page">
      <div className="card login">
        <div className="logo"><div className="mark">NMS</div><div><b>CBH-NMS</b><small className="muted">PDCL Network Monitoring</small></div></div>
        {formLogin ? (
          <form onSubmit={submit} className="grid" style={{ gap: 12 }}>
            <label className="field">User name<input autoFocus value={user} onChange={(e) => setUser(e.target.value)} placeholder="PDCL\username" autoComplete="username" /></label>
            <label className="field">Password<input type="password" value={pass} onChange={(e) => setPass(e.target.value)} autoComplete="current-password" /></label>
            {error && <div className="err">{error}</div>}
            <button className="btn primary" disabled={busy || !user || !pass}>{busy ? 'Signing in…' : 'Sign in'}</button>
            <div className="small muted">Use your Active Directory account. 5 failed attempts lock sign-in for 15 minutes.</div>
          </form>
        ) : formLogin === false ? (
          <div className="grid" style={{ gap: 12 }}>
            <div>{error || 'Windows sign-in did not succeed.'}</div>
            <div className="small muted">Open CBH-NMS from a domain-joined PC with Edge or Chrome, or ask the administrator to enable LDAPS sign-in.</div>
            <button className="btn primary" onClick={onSignedIn}>Try again</button>
          </div>
        ) : null}
      </div>
    </div>
  );
}
