import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, qs, Region } from '../api';
import { fmtTime, Loading, Modal, SevTag, useCan, useLoad, useToast } from '../components/ui';

const ROLES = ['SUPER_ADMIN', 'HEAD_OF_IT', 'IT_MANAGER', 'NET_ADMIN', 'BRANCH_ENGINEER', 'MANAGEMENT'];
const SEVS = ['CRITICAL', 'HIGH', 'WARNING', 'INFO'];

/** SCR-09 (Super Admin; Head of IT sees the audit log). */
export default function Admin() {
  const can = useCan();
  const [params, setParams] = useSearchParams();
  const tabs = [
    ...(can('admin') ? [['users', 'Users & scope'], ['rules', 'Alert rules'], ['notify', 'Notifications'], ['escalation', 'Escalation'],
      ['integrations', 'Integrations & SMS']] : []),
    ['audit', 'Audit log'],
  ];
  const tab = params.get('tab') ?? tabs[0][0];
  return (
    <>
      <div className="tabs">{tabs.map(([k, l]) => <a key={k} className={tab === k ? 'on' : ''} onClick={() => setParams({ tab: k })}>{l}</a>)}</div>
      {tab === 'users' && <Users />}
      {tab === 'rules' && <Rules />}
      {tab === 'notify' && <NotificationRules />}
      {tab === 'escalation' && <Escalation />}
      {tab === 'integrations' && <Integrations />}
      {tab === 'audit' && <Audit />}
    </>
  );
}

function useAct() {
  const toast = useToast();
  return async (fn: () => Promise<unknown>, msg: string, after?: () => void) => {
    try { await fn(); toast(msg); after?.(); return true; } catch (e) { toast((e as Error).message, true); return false; }
  };
}

interface User { userId: number; userName: string; displayName: string; email?: string; mobile?: string; role: string; isActive: boolean; lastLoginAt?: string; scopeBranchIds: number[]; scopeRegionIds: number[] }

function Users() {
  const users = useLoad(() => api.get<User[]>('/admin/users'), []);
  const regions = useLoad(() => api.get<Region[]>('/regions'), []);
  const branches = useLoad(() => api.get<{ items: { branchId: number; code: string }[] }>('/branches'), []);
  const [edit, setEdit] = useState<User | 'new' | null>(null);
  return (
    <div className="card"><h3>Users <span className="sub">AD group → role mapping is in appsettings (Auth:GroupRoleMap) · <button className="btn primary sm" onClick={() => setEdit('new')}>+ User</button></span></h3>
      {!users.data ? <Loading error={users.error} /> : <div className="tablewrap"><table>
        <thead><tr><th>Name</th><th>Account</th><th>Role</th><th>Scope</th><th>E-mail</th><th>Mobile</th><th>Last login</th><th>Active</th></tr></thead>
        <tbody>{users.data.map((u) => (
          <tr key={u.userId} className="click" onClick={() => setEdit(u)}>
            <td><b>{u.displayName}</b></td><td>{u.userName}</td><td>{u.role.replace(/_/g, ' ')}</td>
            <td className="small">{[...u.scopeRegionIds.map((r) => regions.data?.find((x) => x.regionId === r)?.name),
              ...u.scopeBranchIds.map((b) => branches.data?.items.find((x) => x.branchId === b)?.code)].join(', ') || 'all'}</td>
            <td>{u.email ?? '—'}</td><td>{u.mobile ?? '—'}</td><td>{fmtTime(u.lastLoginAt, true)}</td><td>{u.isActive ? '✓' : '—'}</td>
          </tr>))}</tbody>
      </table></div>}
      {edit && regions.data && branches.data && <UserEditor user={edit === 'new' ? null : edit} regions={regions.data} branches={branches.data.items}
        onClose={() => setEdit(null)} onSaved={users.reload} />}
    </div>
  );
}

function UserEditor({ user, regions, branches, onClose, onSaved }: { user: User | null; regions: Region[]; branches: { branchId: number; code: string }[]; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({
    userName: user?.userName ?? 'PDCL\\', displayName: user?.displayName ?? '', email: user?.email ?? '', mobile: user?.mobile ?? '',
    role: user?.role ?? 'BRANCH_ENGINEER', isActive: user?.isActive ?? true, scopeBranchIds: user?.scopeBranchIds ?? [], scopeRegionIds: user?.scopeRegionIds ?? [],
  });
  const act = useAct();
  const scoped = f.role === 'IT_MANAGER' || f.role === 'BRANCH_ENGINEER';
  const toggle = (k: 'scopeBranchIds' | 'scopeRegionIds', id: number) =>
    setF((x) => ({ ...x, [k]: x[k].includes(id) ? x[k].filter((v) => v !== id) : [...x[k], id] }));
  return (
    <Modal title={user ? `Edit ${user.displayName}` : 'New user'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" onClick={() =>
        act(() => user ? api.put(`/admin/users/${user.userId}`, f) : api.post('/admin/users', f), 'User saved', () => { onSaved(); onClose(); })}>Save</button></>}>
      <div className="form">
        <label className="field">AD account<input value={f.userName} onChange={(e) => setF({ ...f, userName: e.target.value })} /></label>
        <label className="field">Display name<input value={f.displayName} onChange={(e) => setF({ ...f, displayName: e.target.value })} /></label>
        <label className="field">E-mail<input value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} /></label>
        <label className="field">Mobile (SMS)<input value={f.mobile} onChange={(e) => setF({ ...f, mobile: e.target.value })} placeholder="8801XXXXXXXXX" /></label>
        <label className="field">Role<select value={f.role} onChange={(e) => setF({ ...f, role: e.target.value })}>{ROLES.map((r) => <option key={r} value={r}>{r.replace(/_/g, ' ')}</option>)}</select></label>
        <label className="chip" style={{ alignSelf: 'end' }}><input type="checkbox" checked={f.isActive} onChange={(e) => setF({ ...f, isActive: e.target.checked })} /> Active</label>
        {scoped && <div className="full">
          <div className="small muted">Scope (required for this role): regions</div>
          <div className="row">{regions.map((r) => <label key={r.regionId} className={`chip ${f.scopeRegionIds.includes(r.regionId) ? 'on' : ''}`}>
            <input type="checkbox" hidden checked={f.scopeRegionIds.includes(r.regionId)} onChange={() => toggle('scopeRegionIds', r.regionId)} />{r.name}</label>)}</div>
          <div className="small muted" style={{ marginTop: 8 }}>…or individual branches</div>
          <div className="row">{branches.map((b) => <label key={b.branchId} className={`chip ${f.scopeBranchIds.includes(b.branchId) ? 'on' : ''}`}>
            <input type="checkbox" hidden checked={f.scopeBranchIds.includes(b.branchId)} onChange={() => toggle('scopeBranchIds', b.branchId)} />{b.code}</label>)}</div>
        </div>}
      </div>
    </Modal>
  );
}

interface Rule { alertRuleId: number; name: string; entityType: string; metricKey: string; operator: string; threshold: number; forSeconds: number; severity: string; scopeBranchId?: number; isEnabled: boolean }
const emptyRule = { name: '', entityType: 'DEVICE', metricKey: 'cpu_pct', operator: '>', threshold: 90, forSeconds: 600, severity: 'WARNING', scopeBranchId: null as number | null, isEnabled: true };

function Rules() {
  const rules = useLoad(() => api.get<Rule[]>('/admin/alert-rules'), []);
  const [edit, setEdit] = useState<typeof emptyRule & { alertRuleId?: number } | null>(null);
  const act = useAct();
  return (
    <div className="card"><h3>Alert rules <span className="sub">changes apply within 15 s, no redeploy (FR-AL-01) · <button className="btn primary sm" onClick={() => setEdit({ ...emptyRule })}>+ Rule</button></span></h3>
      {!rules.data ? <Loading error={rules.error} /> : <div className="tablewrap"><table>
        <thead><tr><th>Rule</th><th>Entity</th><th>Condition</th><th>For</th><th>Severity</th><th>Enabled</th></tr></thead>
        <tbody>{rules.data.map((r) => (
          <tr key={r.alertRuleId} className="click" onClick={() => setEdit({ ...r, scopeBranchId: r.scopeBranchId ?? null })}>
            <td><b>{r.name}</b></td><td>{r.entityType}</td><td><code>{r.metricKey} {r.operator} {r.threshold}</code></td>
            <td>{r.forSeconds ? `${r.forSeconds / 60} min` : 'immediately'}</td><td><SevTag s={r.severity} /></td><td>{r.isEnabled ? '✓' : '—'}</td>
          </tr>))}</tbody>
      </table></div>}
      <p className="small muted">Branch Offline / Unknown alerts come from the status engine (§9.1) and are always on. Common metric keys: up, latency_ms, loss_pct, jitter_ms, cpu_pct, mem_pct, ups_on_battery, ups_battery_pct, response_ms, disk_used_pct.</p>
      {edit && (
        <Modal title={edit.alertRuleId ? `Edit rule` : 'New rule'} onClose={() => setEdit(null)}
          footer={<><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" onClick={() =>
            act(() => edit.alertRuleId ? api.put(`/admin/alert-rules/${edit.alertRuleId}`, edit) : api.post('/admin/alert-rules', edit), 'Rule saved', () => { rules.reload(); setEdit(null); })}>Save</button></>}>
          <div className="form">
            <label className="field full">Name<input value={edit.name} onChange={(e) => setEdit({ ...edit, name: e.target.value })} /></label>
            <label className="field">Entity<select value={edit.entityType} onChange={(e) => setEdit({ ...edit, entityType: e.target.value })}>
              {['BRANCH', 'DEVICE', 'ISPLINK', 'APP'].map((x) => <option key={x}>{x}</option>)}</select></label>
            <label className="field">Metric key<input value={edit.metricKey} onChange={(e) => setEdit({ ...edit, metricKey: e.target.value })} /></label>
            <label className="field">Operator<select value={edit.operator} onChange={(e) => setEdit({ ...edit, operator: e.target.value })}>
              {['>', '>=', '<', '<=', '='].map((x) => <option key={x}>{x}</option>)}</select></label>
            <label className="field">Threshold<input type="number" value={edit.threshold} onChange={(e) => setEdit({ ...edit, threshold: +e.target.value })} /></label>
            <label className="field">For (seconds)<input type="number" value={edit.forSeconds} onChange={(e) => setEdit({ ...edit, forSeconds: +e.target.value })} /></label>
            <label className="field">Severity<select value={edit.severity} onChange={(e) => setEdit({ ...edit, severity: e.target.value })}>{SEVS.map((x) => <option key={x}>{x}</option>)}</select></label>
            <label className="chip"><input type="checkbox" checked={edit.isEnabled} onChange={(e) => setEdit({ ...edit, isEnabled: e.target.checked })} /> Enabled</label>
          </div>
        </Modal>
      )}
    </div>
  );
}

function NotificationRules() {
  const rules = useLoad(() => api.get<{ notificationRuleId: number; branchId?: number; minSeverity: string; timeWindow: string; userId: number; channels: string }[]>('/admin/notification-rules'), []);
  const users = useLoad(() => api.get<User[]>('/admin/users'), []);
  const branches = useLoad(() => api.get<{ items: { branchId: number; code: string }[] }>('/branches'), []);
  const [f, setF] = useState({ branchId: 0, minSeverity: 'HIGH', timeWindow: 'ANY', userId: 0, channels: 'SMS+EMAIL' });
  const act = useAct();
  return (
    <div className="card"><h3>Extra recipients (FR-AL-07) <span className="sub">by default, alerts go to the branch's engineers + NOC (Net Admins)</span></h3>
      {!rules.data ? <Loading error={rules.error} /> : <table><thead><tr><th>User</th><th>Branch</th><th>Min severity</th><th>When</th><th>Channels</th><th></th></tr></thead>
        <tbody>{rules.data.map((r) => <tr key={r.notificationRuleId}>
          <td>{users.data?.find((u) => u.userId === r.userId)?.displayName}</td><td>{r.branchId ? branches.data?.items.find((b) => b.branchId === r.branchId)?.code : 'All'}</td>
          <td><SevTag s={r.minSeverity} /></td><td>{r.timeWindow}</td><td>{r.channels}</td>
          <td><button className="btn sm danger" onClick={() => act(() => api.del(`/admin/notification-rules/${r.notificationRuleId}`), 'Removed', rules.reload)}>Remove</button></td></tr>)}</tbody></table>}
      <div className="row" style={{ marginTop: 10 }}>
        <select value={f.userId} onChange={(e) => setF({ ...f, userId: +e.target.value })}><option value={0}>User…</option>{(users.data ?? []).map((u) => <option key={u.userId} value={u.userId}>{u.displayName}</option>)}</select>
        <select value={f.branchId} onChange={(e) => setF({ ...f, branchId: +e.target.value })}><option value={0}>All branches</option>{(branches.data?.items ?? []).map((b) => <option key={b.branchId} value={b.branchId}>{b.code}</option>)}</select>
        <select value={f.minSeverity} onChange={(e) => setF({ ...f, minSeverity: e.target.value })}>{SEVS.map((s) => <option key={s}>{s}</option>)}</select>
        <select value={f.timeWindow} onChange={(e) => setF({ ...f, timeWindow: e.target.value })}><option value="ANY">Any time</option><option value="OFFICE">Office hours</option><option value="NIGHT">Night (22–07)</option></select>
        <select value={f.channels} onChange={(e) => setF({ ...f, channels: e.target.value })}><option>SMS+EMAIL</option><option>EMAIL</option><option>SMS</option></select>
        <button className="btn primary sm" disabled={!f.userId} onClick={() => act(() => api.post('/admin/notification-rules', { ...f, branchId: f.branchId || null }), 'Rule added', rules.reload)}>Add</button>
      </div>
    </div>
  );
}

function Escalation() {
  const list = useLoad(() => api.get<{ escalationPolicyId: number; severity: string; level: number; afterMinutes: number; targetRole: string; channels: string }[]>('/admin/escalation-policies'), []);
  const act = useAct();
  return (
    <div className="card"><h3>Escalation (§9.2, FR-IN-05) <span className="sub">level 1 = first notification; levels 2–3 fire if the incident is not acknowledged</span></h3>
      {!list.data ? <Loading error={list.error} /> : <table><thead><tr><th>Severity</th><th>Level</th><th>After (min)</th><th>Notify role</th><th>Channels</th></tr></thead>
        <tbody>{list.data.map((p) => <tr key={p.escalationPolicyId}><td><SevTag s={p.severity} /></td><td>L{p.level}</td>
          <td><input type="number" defaultValue={p.afterMinutes} style={{ width: 80 }} disabled={p.level === 1}
            onBlur={(e) => +e.target.value !== p.afterMinutes && act(() => api.put(`/admin/escalation-policies/${p.escalationPolicyId}`, { ...p, afterMinutes: +e.target.value }), 'Saved', list.reload)} /></td>
          <td><select defaultValue={p.targetRole} onChange={(e) => act(() => api.put(`/admin/escalation-policies/${p.escalationPolicyId}`, { ...p, targetRole: e.target.value }), 'Saved', list.reload)}>
            {ROLES.map((r) => <option key={r} value={r}>{r.replace(/_/g, ' ')}</option>)}</select></td>
          <td><select defaultValue={p.channels} onChange={(e) => act(() => api.put(`/admin/escalation-policies/${p.escalationPolicyId}`, { ...p, channels: e.target.value }), 'Saved', list.reload)}>
            <option>SMS+EMAIL</option><option>SMS</option><option>EMAIL</option></select></td></tr>)}</tbody></table>}
    </div>
  );
}

function Integrations() {
  const status = useLoad(() => api.get<Record<string, boolean>>('/admin/integrations'), []);
  const sms = useLoad(() => api.get<{ budgetBdt: number; dailyLimit: number; currentMonthCostBdt: number; months: { month: string; sent: number; skipped: number; failed: number; costBdt: number }[] }>('/admin/sms-usage'), []);
  const log = useLoad(() => api.get<{ notificationId: number; at: string; channel: string; recipient: string; subject?: string; status: string; attempts: number; error?: string; costBdt: number }[]>('/admin/notifications'), [], 30000);
  const [to, setTo] = useState('');
  const act = useAct();
  const pct = sms.data ? Math.min(100, (100 * sms.data.currentMonthCostBdt) / Math.max(1, sms.data.budgetBdt)) : 0;
  return (
    <div className="grid">
      <div className="grid g2">
        <div className="card"><h3>Integrations</h3>
          {!status.data ? <Loading error={status.error} /> : <table><tbody>
            {[['email', 'E-mail (SMTP)'], ['sms', 'SMS gateway'], ['taskManagement', 'Task Management (INT-01)'], ['ldap', 'LDAPS form login'], ['breakGlass', 'Break-glass account']].map(([k, l]) =>
              <tr key={k}><td>{l}</td><td>{status.data![k] ? <span className="pill s-ONLINE">● configured</span> : <span className="pill s-UNKNOWN">not configured</span>}</td></tr>)}
          </tbody></table>}
          <div className="row" style={{ marginTop: 10 }}>
            <input placeholder="e-mail or 8801XXXXXXXXX" value={to} onChange={(e) => setTo(e.target.value)} />
            <button className="btn sm" disabled={!to.includes('@')} onClick={() => act(() => api.post('/admin/integrations/test/email', { to }), 'Test e-mail sent')}>Test e-mail</button>
            <button className="btn sm" disabled={!to || to.includes('@')} onClick={() => act(() => api.post('/admin/integrations/test/sms', { to }), 'Test SMS sent')}>Test SMS</button>
          </div>
          <p className="small muted">Connection settings live in appsettings.Production.json on the server (Email, Sms, TaskManagement, Auth sections).</p>
        </div>
        <div className="card"><h3>SMS spend <span className="sub">limit ৳{sms.data?.budgetBdt ?? '—'} / month · {sms.data?.dailyLimit} per day</span></h3>
          {!sms.data ? <Loading error={sms.error} /> : <>
            <div className="bar" style={{ width: '100%', height: 10 }}><i style={{ width: `${pct}%`, background: pct >= 80 ? 'var(--crit)' : 'var(--ok)' }} /></div>
            <div className="small muted" style={{ marginTop: 6 }}>৳{sms.data.currentMonthCostBdt.toFixed(2)} used ({pct.toFixed(0)}%) · Head of IT is e-mailed at 80%</div>
            <table style={{ marginTop: 10 }}><thead><tr><th>Month</th><th className="num">Sent</th><th className="num">Skipped</th><th className="num">Failed</th><th className="num">Cost ৳</th></tr></thead>
              <tbody>{sms.data.months.map((m) => <tr key={m.month}><td>{m.month}</td><td className="num">{m.sent}</td><td className="num">{m.skipped}</td><td className="num">{m.failed}</td><td className="num">{m.costBdt.toFixed(2)}</td></tr>)}</tbody></table>
          </>}
        </div>
      </div>
      <div className="card"><h3>Recent notifications</h3>
        {!log.data ? <Loading error={log.error} /> : log.data.length === 0 ? <div className="empty">Nothing sent yet.</div> : <div className="tablewrap"><table>
          <thead><tr><th>Time</th><th>Channel</th><th>To</th><th>Subject</th><th>Status</th><th>Attempts</th><th>Error</th></tr></thead>
          <tbody>{log.data.map((n) => <tr key={n.notificationId}><td className="nowrap">{fmtTime(n.at, true)}</td><td>{n.channel}</td><td>{n.recipient}</td>
            <td>{n.subject ?? '—'}</td><td>{n.status}</td><td>{n.attempts}</td><td className="small err">{n.error}</td></tr>)}</tbody></table></div>}
      </div>
    </div>
  );
}

function Audit() {
  const [user, setUser] = useState('');
  const [action, setAction] = useState('');
  const [page, setPage] = useState(1);
  const log = useLoad(() => api.get<{ items: { auditId: number; at: string; userName: string; clientIp?: string; action: string; entityType?: string; entityId?: string; oldValue?: string; newValue?: string }[]; total: number; pageSize: number }>(
    `/admin/audit${qs({ user, action, page, pageSize: 100 })}`), [user, action, page]);
  return (
    <div className="card"><h3>Audit log <span className="sub">append-only (FR-US-04)</span></h3>
      <div className="filters">
        <input placeholder="User contains…" value={user} onChange={(e) => { setUser(e.target.value); setPage(1); }} />
        <input placeholder="Action (e.g. LOGIN, ALERT_ACK)" value={action} onChange={(e) => { setAction(e.target.value.toUpperCase()); setPage(1); }} />
        {log.data && <span className="right small muted">{log.data.total} entries ·
          <button className="btn sm" disabled={page === 1} onClick={() => setPage(page - 1)}>‹</button> page {page}
          <button className="btn sm" disabled={page * log.data.pageSize >= log.data.total} onClick={() => setPage(page + 1)}>›</button></span>}
      </div>
      {!log.data ? <Loading error={log.error} /> : <div className="tablewrap"><table>
        <thead><tr><th>Time</th><th>User</th><th>From</th><th>Action</th><th>Entity</th><th>Before</th><th>After</th></tr></thead>
        <tbody>{log.data.items.map((a) => <tr key={a.auditId}><td className="nowrap">{fmtTime(a.at, true)}</td><td>{a.userName}</td><td>{a.clientIp ?? '—'}</td>
          <td><code>{a.action}</code></td><td>{a.entityType} {a.entityId}</td>
          <td>{a.oldValue && <pre className="json">{a.oldValue}</pre>}</td><td>{a.newValue && <pre className="json">{a.newValue}</pre>}</td></tr>)}</tbody>
      </table></div>}
    </div>
  );
}
