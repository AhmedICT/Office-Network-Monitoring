import { useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, ApiError, qs } from '../api';
import { fmtDuration, fmtTime, Loading, Modal, SevTag, useCan, useLoad, useToast } from '../components/ui';

export interface EndpointRow {
  endpointId: number; hostName: string; branchId: number; branchCode: string; lastUser?: string; ip?: string; os?: string; model?: string;
  lastSeenAt?: string; online: boolean; cpuPct?: number; memPct?: number; diskFreeMinPct?: number; activeAlerts: number;
  agentVersion?: string; hoRttMs?: number;
}
interface ActivitySettings { enabled: boolean; retentionDays: number; enabledBy?: string; enabledAt?: string }

/** User PCs (D-14): health, software, network and — when enabled by policy — application usage of staff PCs. */
export default function Endpoints() {
  const [params, setParams] = useSearchParams();
  const can = useCan();
  const tab = params.get('tab') ?? 'pcs';
  const tabs: [string, string][] = [['pcs', 'PCs'], ['software', 'Software search'], ['rules', 'Software rules']];
  if (can('inventory.edit')) tabs.push(['enroll', 'Enrollment keys']);
  if (can('admin')) tabs.push(['settings', 'Activity tracking']);
  return (
    <>
      <div className="tabs">{tabs.map(([k, l]) => <a key={k} className={tab === k ? 'on' : ''} onClick={() => setParams({ tab: k })}>{l}</a>)}</div>
      {tab === 'pcs' && <PcList />}
      {tab === 'software' && <FleetSoftware />}
      {tab === 'rules' && <Rules />}
      {tab === 'enroll' && can('inventory.edit') && <EnrollKeys />}
      {tab === 'settings' && can('admin') && <Settings />}
    </>
  );
}

const pct = (v?: number) => (v == null ? '—' : `${Math.round(v)}%`);
const warnIf = (bad: boolean) => (bad ? { color: 'var(--crit)', fontWeight: 600 } : undefined);

function PcList() {
  const nav = useNavigate();
  const [status, setStatus] = useState('');
  const [q, setQ] = useState('');
  const [branchId, setBranchId] = useState('');
  const branches = useLoad(() => api.get<{ items: { branchId: number; code: string }[] }>('/branches'), []);
  const list = useLoad(() => api.get<{ items: EndpointRow[] }>(`/endpoints${qs({ status, q, branchId })}`), [status, q, branchId], 60000);
  const items = list.data?.items ?? [];
  const online = items.filter((i) => i.online).length;

  return (
    <div className="card">
      <div className="filters">
        {[['', 'All'], ['online', 'Online'], ['offline', 'Offline']].map(([v, l]) =>
          <span key={v} className={`chip ${status === v ? 'on' : ''}`} onClick={() => setStatus(v)}>{l}</span>)}
        <select value={branchId} onChange={(e) => setBranchId(e.target.value)}>
          <option value="">All branches</option>
          {(branches.data?.items ?? []).map((b) => <option key={b.branchId} value={b.branchId}>{b.code}</option>)}
        </select>
        <input placeholder="PC name, user, IP or serial" value={q} onChange={(e) => setQ(e.target.value)} style={{ minWidth: 240 }} />
        <span className="muted small" style={{ marginLeft: 'auto' }}>{items.length} PCs · {online} online · {items.filter((i) => i.activeAlerts > 0).length} with alerts</span>
      </div>
      {!list.data ? <Loading error={list.error} /> : items.length === 0 ? (
        <div className="empty">No PCs yet. Create an enrollment key and install the Endpoint Agent on a PC (see docs\DEPLOYMENT.md).</div>
      ) : (
        <div className="tablewrap"><table>
          <thead><tr><th>PC</th><th>Branch</th><th>User</th><th>IP</th><th>OS</th><th className="num">CPU</th><th className="num">RAM</th>
            <th className="num">Disk free</th><th className="num">HO ping</th><th>Last seen</th><th className="num">Alerts</th></tr></thead>
          <tbody>{items.map((e) => (
            <tr key={e.endpointId} className="click" onClick={() => nav(`/endpoints/${e.endpointId}`)}>
              <td><span className={`dot ${e.online ? 'ok' : 'off'}`} title={e.online ? 'Online' : 'Offline'} /> <b>{e.hostName}</b>
                {e.model && <div className="muted small">{e.model}</div>}</td>
              <td>{e.branchCode}</td><td>{e.lastUser ?? '—'}</td><td>{e.ip ?? '—'}</td><td className="small">{e.os ?? '—'}</td>
              <td className="num" style={warnIf((e.cpuPct ?? 0) > 90)}>{pct(e.cpuPct)}</td>
              <td className="num" style={warnIf((e.memPct ?? 0) > 90)}>{pct(e.memPct)}</td>
              <td className="num" style={warnIf((e.diskFreeMinPct ?? 100) < 10)}>{pct(e.diskFreeMinPct)}</td>
              <td className="num">{e.hoRttMs == null ? '—' : `${e.hoRttMs} ms`}</td>
              <td className="small">{e.online ? 'now' : e.lastSeenAt ? `${fmtDuration(e.lastSeenAt)} ago` : 'never'}</td>
              <td className="num">{e.activeAlerts > 0 ? <span className="sev HIGH">{e.activeAlerts}</span> : ''}</td>
            </tr>
          ))}</tbody>
        </table></div>
      )}
    </div>
  );
}

function FleetSoftware() {
  const [q, setQ] = useState('');
  const list = useLoad(() => api.get<{ items: { name: string; version?: string; pcs: number; hosts: string[] }[] }>(`/software${qs({ q })}`), [q]);
  return (
    <div className="card">
      <h3>Installed software on all PCs <span className="sub"><input placeholder="Search software, e.g. AnyDesk" value={q} onChange={(e) => setQ(e.target.value)} style={{ minWidth: 260 }} /></span></h3>
      {!list.data ? <Loading error={list.error} /> : list.data.items.length === 0 ? <div className="empty">Nothing found.</div> : (
        <div className="tablewrap"><table>
          <thead><tr><th>Software</th><th>Version</th><th className="num">PCs</th><th>On</th></tr></thead>
          <tbody>{list.data.items.map((s, i) => (
            <tr key={i}><td><b>{s.name}</b></td><td>{s.version ?? '—'}</td><td className="num">{s.pcs}</td>
              <td className="small muted">{s.hosts.join(', ')}{s.pcs > s.hosts.length ? ` +${s.pcs - s.hosts.length} more` : ''}</td></tr>
          ))}</tbody>
        </table></div>
      )}
    </div>
  );
}

interface Rule { ruleId: number; kind: 'BLOCKED' | 'REQUIRED'; pattern: string; severity: string; note?: string; isEnabled: boolean; violations: number }

function Rules() {
  const can = useCan();
  const toast = useToast();
  const list = useLoad(() => api.get<Rule[]>('/software-rules'), []);
  const [edit, setEdit] = useState<Partial<Rule> | null>(null);
  const save = async () => {
    try {
      const body = { kind: edit!.kind, pattern: edit!.pattern, severity: edit!.severity, note: edit!.note || null, isEnabled: edit!.isEnabled ?? true };
      if (edit!.ruleId) await api.put(`/software-rules/${edit!.ruleId}`, body); else await api.post('/software-rules', body);
      toast('Rule saved — alerts update within a minute'); setEdit(null); list.reload();
    } catch (e) { toast((e as ApiError).message, true); }
  };
  const del = async (r: Rule) => {
    if (!confirm(`Delete rule "${r.pattern}"?`)) return;
    await api.del(`/software-rules/${r.ruleId}`); list.reload();
  };
  return (
    <div className="card">
      <h3>Software rules <span className="sub">
        <span className="muted">BLOCKED: alert when installed · REQUIRED: alert when missing · * matches any text</span>
        {can('inventory.edit') && <button className="btn primary sm" onClick={() => setEdit({ kind: 'BLOCKED', severity: 'HIGH', isEnabled: true, pattern: '' })}>+ Rule</button>}</span></h3>
      {!list.data ? <Loading error={list.error} /> : list.data.length === 0 ? <div className="empty">No rules. Example: BLOCKED "*torrent*", REQUIRED "*Defender*" or your antivirus.</div> : (
        <div className="tablewrap"><table>
          <thead><tr><th>Type</th><th>Pattern</th><th>Severity</th><th>Note</th><th className="num">PCs affected</th><th>Enabled</th><th /></tr></thead>
          <tbody>{list.data.map((r) => (
            <tr key={r.ruleId}><td><b>{r.kind}</b></td><td><code>{r.pattern}</code></td><td><SevTag s={r.severity} /></td><td className="small">{r.note ?? ''}</td>
              <td className="num" style={warnIf(r.violations > 0)}>{r.violations}</td><td>{r.isEnabled ? 'yes' : 'no'}</td>
              <td>{can('inventory.edit') && <><button className="btn sm" onClick={() => setEdit(r)}>Edit</button> <button className="btn sm danger" onClick={() => del(r)}>Delete</button></>}</td></tr>
          ))}</tbody>
        </table></div>
      )}
      {edit && (
        <Modal title={edit.ruleId ? 'Edit rule' : 'New software rule'} onClose={() => setEdit(null)}
          footer={<><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" onClick={save}>Save</button></>}>
          <div className="form">
            <label className="field">Type<select value={edit.kind} onChange={(e) => setEdit({ ...edit, kind: e.target.value as Rule['kind'] })}><option>BLOCKED</option><option>REQUIRED</option></select></label>
            <label className="field">Severity<select value={edit.severity} onChange={(e) => setEdit({ ...edit, severity: e.target.value })}>{['CRITICAL', 'HIGH', 'WARNING', 'INFO'].map((s) => <option key={s}>{s}</option>)}</select></label>
            <label className="field full">Software name pattern<input value={edit.pattern} onChange={(e) => setEdit({ ...edit, pattern: e.target.value })} placeholder="*AnyDesk*" /></label>
            <label className="field full">Note<input value={edit.note ?? ''} onChange={(e) => setEdit({ ...edit, note: e.target.value })} placeholder="Why — shown to the engineer" /></label>
            <label className="field"><span><input type="checkbox" checked={edit.isEnabled ?? true} onChange={(e) => setEdit({ ...edit, isEnabled: e.target.checked })} /> Enabled</span></label>
          </div>
        </Modal>
      )}
    </div>
  );
}

interface EnrollKey { enrollKeyId: number; branchCode: string; label?: string; keyHint: string; expiresAt: string; maxUses?: number; uses: number; isRevoked: boolean; createdBy?: string; createdAt: string }

function EnrollKeys() {
  const toast = useToast();
  const list = useLoad(() => api.get<EnrollKey[]>('/endpoint-enroll-keys'), []);
  const branches = useLoad(() => api.get<{ items: { branchId: number; code: string }[] }>('/branches'), []);
  const [form, setForm] = useState<{ branchId: number; label: string; validDays: number; maxUses: string } | null>(null);
  const [created, setCreated] = useState<string | null>(null);
  const create = async () => {
    try {
      const r = await api.post<{ key: string }>('/endpoint-enroll-keys', { branchId: form!.branchId, label: form!.label || null, validDays: form!.validDays, maxUses: form!.maxUses ? +form!.maxUses : null });
      setCreated(r.key); setForm(null); list.reload();
    } catch (e) { toast((e as ApiError).message, true); }
  };
  const revoke = async (k: EnrollKey) => { if (confirm('Revoke this key? PCs already installed keep working.')) { await api.del(`/endpoint-enroll-keys/${k.enrollKeyId}`); list.reload(); } };
  const ho = `${location.protocol}//${location.host}/`;
  return (
    <div className="card">
      <h3>Enrollment keys <span className="sub"><span className="muted">One key can enroll many PCs of a branch (GPO / SCCM rollout)</span>
        <button className="btn primary sm" onClick={() => setForm({ branchId: branches.data?.items[0]?.branchId ?? 0, label: '', validDays: 30, maxUses: '' })}>+ Enrollment key</button></span></h3>
      {created && (
        <div className="notice">
          <b>Copy this key now — it is not shown again.</b>
          <pre className="code">{created}</pre>
          Install on a PC (elevated PowerShell, from the published <code>Endpoint</code> folder):
          <pre className="code">{`.\\install-endpoint.ps1 -HoBaseUrl "${ho}" -EnrollKey "${created}"`}</pre>
          <button className="btn sm" onClick={() => { navigator.clipboard?.writeText(created); toast('Copied'); }}>Copy key</button>{' '}
          <button className="btn sm" onClick={() => setCreated(null)}>Done</button>
        </div>
      )}
      {!list.data ? <Loading error={list.error} /> : (
        <div className="tablewrap"><table>
          <thead><tr><th>Branch</th><th>Label</th><th>Key</th><th>Expires</th><th className="num">Used</th><th>Created</th><th /></tr></thead>
          <tbody>{list.data.map((k) => {
            const expired = new Date(k.expiresAt) < new Date();
            return (
              <tr key={k.enrollKeyId} style={k.isRevoked || expired ? { opacity: 0.5 } : undefined}>
                <td><b>{k.branchCode}</b></td><td>{k.label ?? ''}</td><td><code>…{k.keyHint}</code></td>
                <td>{k.isRevoked ? 'revoked' : expired ? 'expired' : fmtTime(k.expiresAt, true)}</td>
                <td className="num">{k.uses}{k.maxUses ? ` / ${k.maxUses}` : ''}</td><td className="small">{k.createdBy} · {fmtTime(k.createdAt, true)}</td>
                <td>{!k.isRevoked && !expired && <button className="btn sm danger" onClick={() => revoke(k)}>Revoke</button>}</td>
              </tr>
            );
          })}</tbody>
        </table></div>
      )}
      {form && (
        <Modal title="New enrollment key" onClose={() => setForm(null)}
          footer={<><button className="btn" onClick={() => setForm(null)}>Cancel</button><button className="btn primary" onClick={create}>Create</button></>}>
          <div className="form">
            <label className="field">Branch<select value={form.branchId} onChange={(e) => setForm({ ...form, branchId: +e.target.value })}>
              {(branches.data?.items ?? []).map((b) => <option key={b.branchId} value={b.branchId}>{b.code}</option>)}</select></label>
            <label className="field">Label<input value={form.label} onChange={(e) => setForm({ ...form, label: e.target.value })} placeholder="Front desk PCs" /></label>
            <label className="field">Valid for (days)<input type="number" min={1} max={365} value={form.validDays} onChange={(e) => setForm({ ...form, validDays: +e.target.value })} /></label>
            <label className="field">Max PCs (blank = no limit)<input type="number" min={1} value={form.maxUses} onChange={(e) => setForm({ ...form, maxUses: e.target.value })} /></label>
          </div>
        </Modal>
      )}
    </div>
  );
}

function Settings() {
  const toast = useToast();
  const s = useLoad(() => api.get<ActivitySettings>('/endpoint-settings'), []);
  const [confirmNotice, setConfirmNotice] = useState(false);
  const [days, setDays] = useState<number | null>(null);
  if (!s.data) return <Loading error={s.error} />;
  const retention = days ?? s.data.retentionDays;
  const save = async (enabled: boolean) => {
    try {
      s.setData(await api.put<ActivitySettings>('/endpoint-settings', { enabled, retentionDays: retention, noticeConfirmed: confirmNotice }));
      toast(enabled ? 'Activity tracking ON — PCs start within 5 minutes' : 'Activity tracking OFF — PCs stop within 5 minutes');
    } catch (e) { toast((e as ApiError).message, true); }
  };
  return (
    <div className="card" style={{ maxWidth: 820 }}>
      <h3>Application usage tracking {s.data.enabled ? <span className="pill s-ONLINE">ON</span> : <span className="pill s-UNKNOWN">OFF</span>}</h3>
      <p>When on, the Endpoint Agent records <b>which program is in the foreground and for how long</b> (only while the user is active),
        plus logon / logoff / lock / idle times. It never records keystrokes, screenshots, window titles, web addresses or file contents.
        Only Super Admin and Head of IT can see this data, and every view is written to the audit log.</p>
      <p className="muted small">Health, software inventory and network monitoring work without this setting.</p>
      {s.data.enabled && <p className="small">Enabled by {s.data.enabledBy} on {fmtTime(s.data.enabledAt, true)}.</p>}
      <div className="form" style={{ marginTop: 12 }}>
        <label className="field">Keep activity data for (days)<input type="number" min={7} max={365} value={retention} onChange={(e) => setDays(+e.target.value)} /></label>
      </div>
      {!s.data.enabled && (
        <label style={{ display: 'flex', gap: 8, margin: '14px 0', alignItems: 'flex-start' }}>
          <input type="checkbox" checked={confirmNotice} onChange={(e) => setConfirmNotice(e.target.checked)} />
          <span>PDCL has a written IT monitoring policy covering this, and staff using these PCs have been informed that application usage is recorded.</span>
        </label>
      )}
      <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
        {s.data.enabled
          ? <><button className="btn" onClick={() => save(true)}>Save retention</button><button className="btn danger" onClick={() => save(false)}>Turn off</button></>
          : <button className="btn primary" disabled={!confirmNotice} onClick={() => save(true)}>Turn on</button>}
      </div>
    </div>
  );
}
