import { useCallback, useEffect, useMemo, useState } from 'react';
import { NavLink, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { api, ApiError, Me, Summary, Tile } from './api';
import { Loading, MeContext, useMe } from './components/ui';
import { useLive, useLiveState } from './live';
import Login from './pages/Login';
import Dashboard from './pages/Dashboard';
import BranchDetail from './pages/BranchDetail';
import Alerts from './pages/Alerts';
import Incidents from './pages/Incidents';
import Reports from './pages/Reports';
import Maintenance from './pages/Maintenance';
import Inventory from './pages/Inventory';
import Admin from './pages/Admin';
import Endpoints from './pages/Endpoints';
import EndpointDetail from './pages/EndpointDetail';

type AuthState = { kind: 'loading' } | { kind: 'in'; me: Me } | { kind: 'out'; message?: string };

export default function App() {
  const [auth, setAuth] = useState<AuthState>({ kind: 'loading' });

  const loadMe = useCallback(async () => {
    try {
      setAuth({ kind: 'in', me: await api.get<Me>('/me') });
      return;
    } catch (e) {
      if (!(e instanceof ApiError) || (e.status !== 401 && e.status !== 403)) {
        setAuth({ kind: 'out', message: (e as Error).message });
        return;
      }
    }
    // SCR-01: on a domain PC Windows auth signs in silently; otherwise fall back to the form.
    try {
      await api.get('/auth/windows');
      setAuth({ kind: 'in', me: await api.get<Me>('/me') });
    } catch (e) {
      setAuth({ kind: 'out', message: e instanceof ApiError && e.status === 403 ? e.message : undefined });
    }
  }, []);

  useEffect(() => { loadMe(); }, [loadMe]);

  if (auth.kind === 'loading') return <div className="center-page"><Loading /></div>;
  if (auth.kind === 'out') return <Login message={auth.message} onSignedIn={loadMe} />;
  return (
    <MeContext.Provider value={auth.me}>
      <Shell onSignOut={async () => { await api.post('/auth/logout'); setAuth({ kind: 'out' }); }} />
    </MeContext.Provider>
  );
}

const TITLES: [string, string][] = [
  ['/branches/', 'Branch Detail'], ['/endpoints/', 'User PC'], ['/endpoints', 'User PCs'], ['/alerts', 'Alert Console'], ['/incidents', 'Incidents'], ['/reports', 'Reports'],
  ['/maintenance', 'Maintenance Windows'], ['/inventory', 'Branches & Devices'], ['/admin', 'Administration'], ['/', 'Dashboard'],
];

function Shell({ onSignOut }: { onSignOut: () => void }) {
  const me = useMe();
  const loc = useLocation();
  const nav = useNavigate();
  const live = useLiveState();
  const [summary, setSummary] = useState<Summary | null>(null);
  const [menuOpen, setMenuOpen] = useState(false);
  const can = (p: string) => me.permissions.includes(p);

  const loadSummary = useCallback(() => { api.get<Summary>('/dashboard/summary').then(setSummary).catch(() => {}); }, []);
  useEffect(() => { loadSummary(); const h = setInterval(loadSummary, 30000); return () => clearInterval(h); }, [loadSummary]);
  useLive(useCallback((e: string) => { if (e !== 'incidentUpdated') loadSummary(); }, [loadSummary]));
  useEffect(() => setMenuOpen(false), [loc.pathname]);

  const title = TITLES.find(([p]) => loc.pathname.startsWith(p))?.[1] ?? 'CBH-NMS';
  const initials = me.displayName.replace(/^.*\\/, '').split(/\s+/).map((w) => w[0]).join('').slice(0, 2).toUpperCase();

  return (
    <div className="app">
      <aside className={menuOpen ? 'open' : ''}>
        <div className="logo"><div className="mark">NMS</div><div><b>CBH-NMS</b><small>PDCL Network Monitoring</small></div></div>
        <nav>
          <div className="sec">Monitoring</div>
          <NavLink to="/" end>▦ Dashboard</NavLink>
          <NavLink to="/alerts">⚠ Alerts {summary && summary.activeAlerts > 0 && <span className="badge">{summary.activeAlerts}</span>}</NavLink>
          <NavLink to="/incidents">☰ Incidents {summary && summary.unackedIncidents > 0 && <span className="badge">{summary.unackedIncidents}</span>}</NavLink>
          <NavLink to="/maintenance">⏸ Maintenance</NavLink>
          <NavLink to="/endpoints">🖥 User PCs</NavLink>
          {can('reports') && <><div className="sec">Analysis</div><NavLink to="/reports">▤ Reports</NavLink></>}
          {(can('inventory.edit') || can('admin') || can('audit')) && <div className="sec">Administration</div>}
          {can('inventory.edit') && <NavLink to="/inventory">⚙ Branches &amp; Devices</NavLink>}
          {(can('admin') || can('audit')) && <NavLink to="/admin">🔒 Admin</NavLink>}
        </nav>
        <div className="side-foot">
          {me.displayName}<br />{me.role.replace(/_/g, ' ')}<br />
          <button onClick={onSignOut}>Sign out</button>
        </div>
      </aside>

      <div style={{ minWidth: 0 }}>
        <header className="top">
          <button className="menu-btn" onClick={() => setMenuOpen((x) => !x)} aria-label="Menu">☰</button>
          <div><h1>{title}</h1><div className="crumb">{loc.pathname === '/' ? 'All branches in your scope · live' : 'CBH-NMS'}</div></div>
          <GlobalSearch />
          {summary && summary.unackedCritical > 0 && (
            <a className="critbadge" onClick={() => nav('/alerts?severity=CRITICAL')}>{summary.unackedCritical} Critical</a>
          )}
          <div className={`live ${live ? '' : 'off'}`} title="Real-time updates (SignalR)"><i /> <span>{live ? 'Live' : 'Reconnecting'}</span></div>
          <div className="user"><div className="avatar">{initials}</div><span>{me.role.replace(/_/g, ' ')}</span></div>
        </header>
        <main>
          <Routes>
            <Route path="/" element={<Dashboard summary={summary} />} />
            <Route path="/branches/:id" element={<BranchDetail />} />
            <Route path="/alerts" element={<Alerts />} />
            <Route path="/incidents" element={<Incidents />} />
            <Route path="/incidents/:id" element={<Incidents />} />
            <Route path="/maintenance" element={<Maintenance />} />
            <Route path="/reports" element={<Reports />} />
            <Route path="/inventory" element={<Inventory />} />
            <Route path="/admin" element={<Admin />} />
            <Route path="/endpoints" element={<Endpoints />} />
            <Route path="/endpoints/:id" element={<EndpointDetail />} />
            <Route path="*" element={<div className="empty">Page not found.</div>} />
          </Routes>
        </main>
      </div>
    </div>
  );
}

/** Branch code / name / IP search across the user's scope (SRS §8 layout). */
function GlobalSearch() {
  const [q, setQ] = useState('');
  const [tiles, setTiles] = useState<Tile[] | null>(null);
  const [devices, setDevices] = useState<{ deviceId: number; branchId: number; hostName: string; ipAddress: string }[]>([]);
  const nav = useNavigate();

  const ensureData = () => {
    if (tiles) return;
    api.get<{ items: Tile[] }>('/dashboard/branches').then((r) => setTiles(r.items)).catch(() => setTiles([]));
    api.get<{ items: typeof devices }>('/devices').then((r) => setDevices(r.items)).catch(() => {});
  };
  const results = useMemo(() => {
    const s = q.trim().toLowerCase();
    if (s.length < 2 || !tiles) return [];
    const b = tiles.filter((t) => t.code.toLowerCase().includes(s) || t.name.toLowerCase().includes(s))
      .map((t) => ({ key: 'b' + t.branchId, label: `${t.code} · ${t.name}`, go: `/branches/${t.branchId}` }));
    const d = devices.filter((x) => x.ipAddress.includes(s) || x.hostName.toLowerCase().includes(s))
      .map((x) => ({ key: 'd' + x.deviceId, label: `${x.hostName} · ${x.ipAddress}`, go: `/branches/${x.branchId}?tab=dev` }));
    return [...b, ...d].slice(0, 12);
  }, [q, tiles, devices]);

  return (
    <div className="search">
      <input value={q} placeholder="⌕ Search branch code, name or IP…" onFocus={ensureData} onChange={(e) => setQ(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Enter' && results[0]) { nav(results[0].go); setQ(''); } }} />
      {results.length > 0 && (
        <div className="results">
          {results.map((r) => <div key={r.key} onMouseDown={() => { nav(r.go); setQ(''); }}>{r.label}</div>)}
        </div>
      )}
    </div>
  );
}
