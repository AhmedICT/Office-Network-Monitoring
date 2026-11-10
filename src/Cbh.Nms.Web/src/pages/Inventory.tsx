import { ReactNode, useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, Region } from '../api';
import { fmtTime, Loading, Modal, useLoad, useToast } from '../components/ui';

const TABS = [['branches', 'Branches'], ['devices', 'Devices'], ['agents', 'Agents'], ['isps', 'ISPs'], ['apps', 'HIS checks'],
  ['snmp', 'SNMP profiles'], ['import', 'Bulk import']] as const;

/** SCR-04 (Super Admin, Network Administrator). */
export default function Inventory() {
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab') ?? 'branches';
  return (
    <>
      <div className="tabs">{TABS.map(([k, l]) => <a key={k} className={tab === k ? 'on' : ''} onClick={() => setParams({ tab: k })}>{l}</a>)}</div>
      {tab === 'branches' && <Branches openId={params.get('branch')} />}
      {tab === 'devices' && <Devices />}
      {tab === 'agents' && <Agents />}
      {tab === 'isps' && <Isps />}
      {tab === 'apps' && <AppChecks />}
      {tab === 'snmp' && <SnmpProfiles />}
      {tab === 'import' && <Import />}
    </>
  );
}

// ───────── shared bits ─────────
function useSave() {
  const toast = useToast();
  return async (fn: () => Promise<unknown>, msg: string, after?: () => void) => {
    try { await fn(); toast(msg); after?.(); return true; } catch (e) { toast((e as Error).message, true); return false; }
  };
}
const F = ({ label, children, full }: { label: string; children: ReactNode; full?: boolean }) =>
  <label className={`field ${full ? 'full' : ''}`}>{label}{children}</label>;
type Setter<T> = <K extends keyof T>(k: K, v: T[K]) => void;
function useForm<T extends object>(init: T): [T, Setter<T>, (v: T) => void] {
  const [v, setV] = useState(init);
  return [v, (k, val) => setV((x) => ({ ...x, [k]: val })), setV];
}

// ───────── branches ─────────
interface BranchRow { branchId: number; code: string; name: string; region: string; regionId: number; tier: number; isActive: boolean; status: string }
interface BranchForm { branchCode: string; branchName: string; regionId: number; address: string; latitude: string; longitude: string; tier: number;
  openTime: string; closeTime: string; mplsRouterIp: string; vpnRouterIp: string; slaTargetPct: string; isActive: boolean }
const emptyBranch: BranchForm = { branchCode: '', branchName: '', regionId: 0, address: '', latitude: '', longitude: '', tier: 2, openTime: '08:00', closeTime: '22:00', mplsRouterIp: '', vpnRouterIp: '', slaTargetPct: '99.5', isActive: true };

function Branches({ openId }: { openId: string | null }) {
  const list = useLoad(() => api.get<{ items: BranchRow[] }>('/branches'), []);
  const regions = useLoad(() => api.get<Region[]>('/regions'), []);
  const [edit, setEdit] = useState<number | 'new' | null>(openId ? +openId : null);
  const [newRegion, setNewRegion] = useState('');
  const save = useSave();
  return (
    <div className="card">
      <h3>Branches <span className="sub">
        <input placeholder="New region" value={newRegion} onChange={(e) => setNewRegion(e.target.value)} style={{ width: 130 }} />
        <button className="btn sm" disabled={!newRegion.trim()} onClick={() => save(() => api.post('/regions', { name: newRegion }), 'Region added', () => { setNewRegion(''); regions.reload(); })}>+ Region</button>
        <button className="btn primary sm" onClick={() => setEdit('new')}>+ Branch</button></span></h3>
      {!list.data ? <Loading error={list.error} /> : (
        <div className="tablewrap"><table>
          <thead><tr><th>Code</th><th>Name</th><th>Region</th><th>Tier</th><th>Status</th><th>Active</th></tr></thead>
          <tbody>{list.data.items.map((b) => (
            <tr key={b.branchId} className="click" onClick={() => setEdit(b.branchId)}>
              <td><b>{b.code}</b></td><td>{b.name}</td><td>{b.region}</td><td>{b.tier}</td><td>{b.status}</td><td>{b.isActive ? '✓' : '—'}</td>
            </tr>))}</tbody>
        </table></div>
      )}
      {edit !== null && regions.data && <BranchEditor id={edit} regions={regions.data} onClose={() => setEdit(null)} onSaved={list.reload} />}
    </div>
  );
}

interface BranchDetailDto {
  branchId: number; code: string; name: string; regionId: number; address?: string; latitude?: number; longitude?: number; tier: number;
  openTime?: string; closeTime?: string; mplsRouterIp?: string; vpnRouterIp?: string; slaTargetPct: number; isActive: boolean; configVersion: number;
  contacts: { name: string; role?: string; phone?: string; email?: string; isPrimary: boolean }[];
  ispLinks: { ispLinkId: number; isp: string; linkRole: string; circuitId?: string; bandwidthMbps: number; wanInterface?: string; probeTarget?: string; isActive: boolean }[];
  agents: { agentId: number; hostName: string; configVersion: number; lastSeenAt?: string }[];
}

function BranchEditor({ id, regions, onClose, onSaved }: { id: number | 'new'; regions: Region[]; onClose: () => void; onSaved: () => void }) {
  const [f, set, setAll] = useForm<BranchForm>({ ...emptyBranch, regionId: regions[0]?.regionId ?? 0 });
  const [detail, setDetail] = useState<BranchDetailDto | null>(null);
  const save = useSave();
  const load = () => id !== 'new' && api.get<BranchDetailDto>(`/branches/${id}`).then((d) => {
    setDetail(d);
    setAll({ branchCode: d.code, branchName: d.name, regionId: d.regionId, address: d.address ?? '', latitude: d.latitude?.toString() ?? '',
      longitude: d.longitude?.toString() ?? '', tier: d.tier, openTime: d.openTime?.slice(0, 5) ?? '', closeTime: d.closeTime?.slice(0, 5) ?? '',
      mplsRouterIp: d.mplsRouterIp ?? '', vpnRouterIp: d.vpnRouterIp ?? '', slaTargetPct: String(d.slaTargetPct), isActive: d.isActive });
  });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { load(); }, [id]);

  const body = () => ({
    ...f, latitude: f.latitude ? +f.latitude : null, longitude: f.longitude ? +f.longitude : null, slaTargetPct: +f.slaTargetPct,
    openTime: f.openTime || null, closeTime: f.closeTime || null, mplsRouterIp: f.mplsRouterIp || null, vpnRouterIp: f.vpnRouterIp || null, address: f.address || null,
  });
  const submit = () => save(() => id === 'new' ? api.post('/branches', body()) : api.put(`/branches/${id}`, body()),
    'Branch saved — agent picks up the change within 5 minutes', () => { onSaved(); if (id === 'new') onClose(); else load(); });

  return (
    <Modal title={id === 'new' ? 'New branch' : `Edit ${f.branchCode}`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Close</button><button className="btn primary" onClick={submit}>Save branch</button></>}>
      <div className="form">
        <F label="Code"><input value={f.branchCode} onChange={(e) => set('branchCode', e.target.value)} placeholder="SAV01" /></F>
        <F label="Name"><input value={f.branchName} onChange={(e) => set('branchName', e.target.value)} /></F>
        <F label="Region"><select value={f.regionId} onChange={(e) => set('regionId', +e.target.value)}>{regions.map((r) => <option key={r.regionId} value={r.regionId}>{r.name}</option>)}</select></F>
        <F label="Tier (1 = most critical)"><select value={f.tier} onChange={(e) => set('tier', +e.target.value)}><option>1</option><option>2</option><option>3</option></select></F>
        <F label="MPLS router IP (HO pings over MPLS)"><input value={f.mplsRouterIp} onChange={(e) => set('mplsRouterIp', e.target.value)} placeholder="10.10.0.1" /></F>
        <F label="VPN tunnel IP (HO pings over VPN)"><input value={f.vpnRouterIp} onChange={(e) => set('vpnRouterIp', e.target.value)} placeholder="172.16.10.1" /></F>
        <F label="SLA target %"><input value={f.slaTargetPct} onChange={(e) => set('slaTargetPct', e.target.value)} /></F>
        <F label="Opens"><input type="time" value={f.openTime} onChange={(e) => set('openTime', e.target.value)} /></F>
        <F label="Closes"><input type="time" value={f.closeTime} onChange={(e) => set('closeTime', e.target.value)} /></F>
        <F label="Latitude"><input value={f.latitude} onChange={(e) => set('latitude', e.target.value)} /></F>
        <F label="Longitude"><input value={f.longitude} onChange={(e) => set('longitude', e.target.value)} /></F>
        <F label="Address" full><input value={f.address} onChange={(e) => set('address', e.target.value)} /></F>
        <label className="chip"><input type="checkbox" checked={f.isActive} onChange={(e) => set('isActive', e.target.checked)} /> Active</label>
      </div>
      {detail && <>
        <h3 style={{ marginTop: 18 }}>Agent config</h3>
        <div className="small">Branch config v{detail.configVersion}. {detail.agents.length === 0 ? 'No agent registered yet.' : detail.agents.map((a) => (
          <span key={a.agentId}>{a.hostName}: has v{a.configVersion} {a.configVersion >= detail.configVersion ? '✓ up to date' : '⏳ will pull within 5 min'} · last heartbeat {fmtTime(a.lastSeenAt, true)}. </span>))}</div>
        <IspLinks branchId={detail.branchId} links={detail.ispLinks} onChanged={load} />
        <Contacts branchId={detail.branchId} initial={detail.contacts} />
      </>}
    </Modal>
  );
}

function IspLinks({ branchId, links, onChanged }: { branchId: number; links: BranchDetailDto['ispLinks']; onChanged: () => void }) {
  const isps = useLoad(() => api.get<{ ispId: number; name: string }[]>('/isps'), []);
  const [f, set] = useForm({ ispId: 0, linkRole: 'PRIMARY', circuitId: '', bandwidthMbps: 50, wanInterface: '', probeTarget: '8.8.8.8' });
  const save = useSave();
  return (
    <>
      <h3 style={{ marginTop: 18 }}>ISP links</h3>
      <table><tbody>{links.map((l) => (
        <tr key={l.ispLinkId}><td>{l.linkRole}</td><td>{l.isp}</td><td>{l.circuitId}</td><td>{l.bandwidthMbps} Mbps</td><td>probe {l.probeTarget ?? '—'}</td>
          <td><button className="btn sm" onClick={() => save(() => api.put(`/branches/${branchId}/isp-links/${l.ispLinkId}`, {
            ispId: isps.data?.find((i) => i.name === l.isp)?.ispId, linkRole: l.linkRole, circuitId: l.circuitId, bandwidthMbps: l.bandwidthMbps,
            wanInterface: l.wanInterface, probeTarget: l.probeTarget, isActive: !l.isActive }), l.isActive ? 'Link disabled' : 'Link enabled', onChanged)}>
            {l.isActive ? 'Disable' : 'Enable'}</button></td></tr>))}</tbody></table>
      <div className="row" style={{ marginTop: 8 }}>
        <select value={f.ispId} onChange={(e) => set('ispId', +e.target.value)}><option value={0}>ISP…</option>{(isps.data ?? []).map((i) => <option key={i.ispId} value={i.ispId}>{i.name}</option>)}</select>
        <select value={f.linkRole} onChange={(e) => set('linkRole', e.target.value)}><option>PRIMARY</option><option>SECONDARY</option><option>MPLS</option></select>
        <input placeholder="Circuit ID" value={f.circuitId} onChange={(e) => set('circuitId', e.target.value)} style={{ width: 110 }} />
        <input type="number" value={f.bandwidthMbps} onChange={(e) => set('bandwidthMbps', +e.target.value)} style={{ width: 80 }} title="Mbps" />
        <input placeholder="Probe IP" value={f.probeTarget} onChange={(e) => set('probeTarget', e.target.value)} style={{ width: 110 }} />
        <button className="btn sm primary" disabled={!f.ispId} onClick={() => save(() => api.post(`/branches/${branchId}/isp-links`, f), 'Link added', onChanged)}>Add link</button>
      </div>
    </>
  );
}

function Contacts({ branchId, initial }: { branchId: number; initial: BranchDetailDto['contacts'] }) {
  const [rows, setRows] = useState(initial.map((c) => ({ ...c, role: c.role ?? '', phone: c.phone ?? '', email: c.email ?? '' })));
  const save = useSave();
  const upd = (i: number, k: string, v: string | boolean) => setRows((r) => r.map((x, n) => (n === i ? { ...x, [k]: v } : x)));
  return (
    <>
      <h3 style={{ marginTop: 18 }}>Contacts</h3>
      {rows.map((c, i) => (
        <div className="row" key={i} style={{ marginBottom: 6 }}>
          <input placeholder="Name" value={c.name} onChange={(e) => upd(i, 'name', e.target.value)} style={{ width: 140 }} />
          <input placeholder="Role" value={c.role} onChange={(e) => upd(i, 'role', e.target.value)} style={{ width: 100 }} />
          <input placeholder="Phone" value={c.phone} onChange={(e) => upd(i, 'phone', e.target.value)} style={{ width: 130 }} />
          <input placeholder="E-mail" value={c.email} onChange={(e) => upd(i, 'email', e.target.value)} style={{ width: 160 }} />
          <label className="small"><input type="checkbox" checked={c.isPrimary} onChange={(e) => upd(i, 'isPrimary', e.target.checked)} /> primary</label>
          <button className="btn sm danger" onClick={() => setRows((r) => r.filter((_, n) => n !== i))}>✕</button>
        </div>
      ))}
      <div className="row">
        <button className="btn sm" onClick={() => setRows((r) => [...r, { name: '', role: 'Branch IT', phone: '', email: '', isPrimary: r.length === 0 }])}>+ Contact</button>
        <button className="btn sm primary" onClick={() => save(() => api.put(`/branches/${branchId}/contacts`, rows), 'Contacts saved')}>Save contacts</button>
      </div>
    </>
  );
}

// ───────── devices ─────────
interface DeviceRow { deviceId: number; branchId: number; type: string; deviceTypeId: number; vendor?: string; hostName: string; ipAddress: string;
  model?: string; serialNo?: string; firmware?: string; warrantyEnd?: string; snmpProfileId?: number; pollIntervalS: number; isCritical: boolean;
  isActive: boolean; parentDeviceIds: number[] }

function Devices() {
  const branches = useLoad(() => api.get<{ items: BranchRow[] }>('/branches'), []);
  const [branchId, setBranchId] = useState(0);
  const list = useLoad(() => api.get<{ items: DeviceRow[] }>(`/devices${branchId ? `?branchId=${branchId}` : ''}`), [branchId]);
  const [edit, setEdit] = useState<DeviceRow | 'new' | null>(null);
  const code = (id: number) => branches.data?.items.find((b) => b.branchId === id)?.code;
  return (
    <div className="card">
      <h3>Devices <span className="sub">
        <select value={branchId} onChange={(e) => setBranchId(+e.target.value)}><option value={0}>All branches</option>
          {(branches.data?.items ?? []).map((b) => <option key={b.branchId} value={b.branchId}>{b.code} · {b.name}</option>)}</select>
        <button className="btn primary sm" onClick={() => setEdit('new')}>+ Device</button></span></h3>
      {!list.data ? <Loading error={list.error} /> : (
        <div className="tablewrap"><table>
          <thead><tr><th>Branch</th><th>Host</th><th>Type</th><th>Vendor</th><th>IP</th><th>Model</th><th>Warranty</th><th>Critical</th><th>Active</th></tr></thead>
          <tbody>{list.data.items.map((d) => (
            <tr key={d.deviceId} className="click" onClick={() => setEdit(d)}>
              <td>{code(d.branchId)}</td><td><b>{d.hostName}</b></td><td>{d.type}</td><td>{d.vendor ?? '—'}</td><td>{d.ipAddress}</td>
              <td>{d.model ?? '—'}</td><td>{d.warrantyEnd ?? '—'}</td><td>{d.isCritical ? '✓' : ''}</td><td>{d.isActive ? '✓' : '—'}</td>
            </tr>))}</tbody>
        </table></div>
      )}
      {edit && branches.data && <DeviceEditor device={edit === 'new' ? null : edit} defaultBranch={branchId || branches.data.items[0]?.branchId}
        branches={branches.data.items} onClose={() => setEdit(null)} onSaved={list.reload} />}
    </div>
  );
}

function DeviceEditor({ device, defaultBranch, branches, onClose, onSaved }: {
  device: DeviceRow | null; defaultBranch: number; branches: BranchRow[]; onClose: () => void; onSaved: () => void;
}) {
  const types = useLoad(() => api.get<{ deviceTypeId: number; name: string }[]>('/device-types'), []);
  const profiles = useLoad(() => api.get<{ snmpProfileId: number; name: string }[]>('/snmp-profiles'), []);
  const [f, set] = useForm({
    branchId: device?.branchId ?? defaultBranch, deviceTypeId: device?.deviceTypeId ?? 1, vendor: device?.vendor ?? '', hostName: device?.hostName ?? '',
    ipAddress: device?.ipAddress ?? '', model: device?.model ?? '', serialNo: device?.serialNo ?? '', firmware: device?.firmware ?? '',
    warrantyEnd: device?.warrantyEnd ?? '', snmpProfileId: device?.snmpProfileId ?? 0, pollIntervalS: device?.pollIntervalS ?? 60,
    isCritical: device?.isCritical ?? false, isActive: device?.isActive ?? true, parentDeviceIds: device?.parentDeviceIds ?? [] as number[],
  });
  const siblings = useLoad(() => api.get<{ items: DeviceRow[] }>(`/devices?branchId=${f.branchId}`), [f.branchId]);
  const save = useSave();
  const body = { ...f, snmpProfileId: f.snmpProfileId || null, warrantyEnd: f.warrantyEnd || null, vendor: f.vendor || null };
  return (
    <Modal title={device ? `Edit ${device.hostName}` : 'New device'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" onClick={() =>
        save(() => device ? api.put(`/devices/${device.deviceId}`, body) : api.post('/devices', body),
          'Device saved — the agent polls it after its next config pull', () => { onSaved(); onClose(); })}>Save</button></>}>
      <div className="form">
        <F label="Branch"><select value={f.branchId} onChange={(e) => set('branchId', +e.target.value)}>{branches.map((b) => <option key={b.branchId} value={b.branchId}>{b.code}</option>)}</select></F>
        <F label="Type"><select value={f.deviceTypeId} onChange={(e) => set('deviceTypeId', +e.target.value)}>{(types.data ?? []).map((t) => <option key={t.deviceTypeId} value={t.deviceTypeId}>{t.name}</option>)}</select></F>
        <F label="Host name"><input value={f.hostName} onChange={(e) => set('hostName', e.target.value)} placeholder="SAV01-FGT-01" /></F>
        <F label="IPv4 address"><input value={f.ipAddress} onChange={(e) => set('ipAddress', e.target.value)} /></F>
        <F label="Vendor"><select value={f.vendor} onChange={(e) => set('vendor', e.target.value)}>
          {['', 'FORTINET', 'CISCO', 'MIKROTIK', 'APC', 'DELL', 'HPE', 'OTHER'].map((v) => <option key={v} value={v}>{v || '—'}</option>)}</select></F>
        <F label="SNMPv3 profile"><select value={f.snmpProfileId} onChange={(e) => set('snmpProfileId', +e.target.value)}><option value={0}>None (ping only)</option>
          {(profiles.data ?? []).map((p) => <option key={p.snmpProfileId} value={p.snmpProfileId}>{p.name}</option>)}</select></F>
        <F label="Model"><input value={f.model} onChange={(e) => set('model', e.target.value)} /></F>
        <F label="Serial no."><input value={f.serialNo} onChange={(e) => set('serialNo', e.target.value)} /></F>
        <F label="Firmware"><input value={f.firmware} onChange={(e) => set('firmware', e.target.value)} /></F>
        <F label="Warranty end"><input type="date" value={f.warrantyEnd} onChange={(e) => set('warrantyEnd', e.target.value)} /></F>
        <F label="Poll interval (s)"><input type="number" value={f.pollIntervalS} onChange={(e) => set('pollIntervalS', +e.target.value)} /></F>
        <F label="Parent device (dependency)"><select value={f.parentDeviceIds[0] ?? 0} onChange={(e) => set('parentDeviceIds', +e.target.value ? [+e.target.value] : [])}>
          <option value={0}>None (top level — branch router)</option>
          {(siblings.data?.items ?? []).filter((d) => d.deviceId !== device?.deviceId).map((d) => <option key={d.deviceId} value={d.deviceId}>{d.hostName}</option>)}</select></F>
        <label className="chip"><input type="checkbox" checked={f.isCritical} onChange={(e) => set('isCritical', e.target.checked)} /> Critical (down ⇒ branch Degraded)</label>
        <label className="chip"><input type="checkbox" checked={f.isActive} onChange={(e) => set('isActive', e.target.checked)} /> Active</label>
      </div>
    </Modal>
  );
}

// ───────── agents ─────────
interface AgentRow { agentId: number; branchId: number; branchCode: string; branchName: string; hostName: string; agentVersion?: string;
  configVersion: number; latestConfigVersion: number; lastSeenAt?: string; bufferRows?: number; registeredAt: string; isRevoked: boolean }

function Agents() {
  const list = useLoad(() => api.get<{ items: AgentRow[] }>('/agents'), [], 30000);
  const branches = useLoad(() => api.get<{ items: BranchRow[] }>('/branches'), []);
  const [branchId, setBranchId] = useState(0);
  const [token, setToken] = useState<{ token: string; expiresAtUtc: string } | null>(null);
  const save = useSave();
  const toast = useToast();
  return (
    <div className="card">
      <h3>Branch agents <span className="sub">
        <select value={branchId} onChange={(e) => setBranchId(+e.target.value)}><option value={0}>Branch…</option>
          {(branches.data?.items ?? []).map((b) => <option key={b.branchId} value={b.branchId}>{b.code}</option>)}</select>
        <button className="btn primary sm" disabled={!branchId} onClick={async () => {
          try { setToken(await api.post('/agents/enroll-token', { branchId })); } catch (e) { toast((e as Error).message, true); }
        }}>+ Enrollment token</button></span></h3>
      {!list.data ? <Loading error={list.error} /> : list.data.items.length === 0 ? <div className="empty">No agents registered yet. Create a token and install the agent at the branch.</div> : (
        <div className="tablewrap"><table>
          <thead><tr><th>Branch</th><th>Host</th><th>Version</th><th>Config</th><th>Last heartbeat</th><th className="num">Buffer rows</th><th>Status</th><th></th></tr></thead>
          <tbody>{list.data.items.map((a) => {
            const silent = !a.lastSeenAt || Date.now() - new Date(a.lastSeenAt).getTime() > 180e3;
            return (
              <tr key={a.agentId} style={a.isRevoked ? { opacity: .5 } : undefined}>
                <td>{a.branchCode} · {a.branchName}</td><td>{a.hostName}</td><td>{a.agentVersion ?? '—'}</td>
                <td>v{a.configVersion}{a.configVersion < a.latestConfigVersion && <span className="muted"> (v{a.latestConfigVersion} pending)</span>}</td>
                <td>{fmtTime(a.lastSeenAt, true)}</td><td className="num">{a.bufferRows ?? '—'}</td>
                <td>{a.isRevoked ? 'revoked' : silent ? <span className="pill s-UNKNOWN">? No heartbeat</span> : <span className="pill s-ONLINE">● OK</span>}</td>
                <td>{!a.isRevoked && <button className="btn sm danger" onClick={() => confirm(`Revoke agent ${a.hostName}? It will stop being accepted immediately.`) &&
                  save(() => api.post(`/agents/${a.agentId}/revoke`), 'Agent revoked', list.reload)}>Revoke</button>}</td>
              </tr>);
          })}</tbody>
        </table></div>
      )}
      {token && (
        <Modal title="One-time enrollment token" onClose={() => setToken(null)} footer={<button className="btn primary" onClick={() => setToken(null)}>Done</button>}>
          <p>Shown once — only its hash is stored. Valid until {fmtTime(token.expiresAtUtc, true)}.</p>
          <p><code style={{ fontSize: 14, userSelect: 'all' }}>{token.token}</code></p>
          <p className="small muted">Install on the branch PC (elevated PowerShell):</p>
          <pre className="json" style={{ maxWidth: '100%' }}>{`.\\install-agent.ps1 -HoBaseUrl "${location.origin}/" -EnrollToken "${token.token}"`}</pre>
        </Modal>
      )}
    </div>
  );
}

// ───────── ISPs / HIS checks / SNMP profiles ─────────
function Isps() {
  const list = useLoad(() => api.get<{ ispId: number; name: string; supportPhone?: string; supportEmail?: string }[]>('/isps'), []);
  const [f, set, setAll] = useForm({ name: '', supportPhone: '', supportEmail: '' });
  const save = useSave();
  return (
    <div className="card"><h3>ISPs</h3>
      {!list.data ? <Loading error={list.error} /> : <table><thead><tr><th>Name</th><th>Support phone</th><th>Support e-mail</th></tr></thead>
        <tbody>{list.data.map((i) => <tr key={i.ispId}><td>{i.name}</td><td>{i.supportPhone}</td><td>{i.supportEmail}</td></tr>)}</tbody></table>}
      <div className="row" style={{ marginTop: 10 }}>
        <input placeholder="ISP name" value={f.name} onChange={(e) => set('name', e.target.value)} />
        <input placeholder="Support phone" value={f.supportPhone} onChange={(e) => set('supportPhone', e.target.value)} />
        <input placeholder="Support e-mail" value={f.supportEmail} onChange={(e) => set('supportEmail', e.target.value)} />
        <button className="btn primary sm" disabled={!f.name} onClick={() => save(() => api.post('/isps', f), 'ISP added', () => { setAll({ name: '', supportPhone: '', supportEmail: '' }); list.reload(); })}>Add</button>
      </div>
    </div>
  );
}

function AppChecks() {
  const list = useLoad(() => api.get<{ appCheckId: number; branchId?: number; name: string; checkType: string; target: string; expectedStatus?: number; timeoutMs: number; businessService?: string; isActive: boolean }[]>('/app-checks'), []);
  const branches = useLoad(() => api.get<{ items: BranchRow[] }>('/branches'), []);
  const [f, set] = useForm({ branchId: 0, name: 'HIS web', checkType: 'HTTP', target: '', timeoutMs: 5000, businessService: 'Registration' });
  const save = useSave();
  const code = (id?: number) => (id ? branches.data?.items.find((b) => b.branchId === id)?.code : 'All branches');
  return (
    <div className="card"><h3>HIS / LIS health checks <span className="sub">service state only — no patient data (§5)</span></h3>
      {!list.data ? <Loading error={list.error} /> : <div className="tablewrap"><table><thead><tr><th>Name</th><th>Run from</th><th>Type</th><th>Target</th><th>Service</th><th>Active</th><th></th></tr></thead>
        <tbody>{list.data.map((c) => <tr key={c.appCheckId}><td>{c.name}</td><td>{code(c.branchId)}</td><td>{c.checkType}</td><td>{c.target}</td><td>{c.businessService ?? '—'}</td><td>{c.isActive ? '✓' : '—'}</td>
          <td><button className="btn sm" onClick={() => save(() => api.put(`/app-checks/${c.appCheckId}`, { ...c, isActive: !c.isActive }), 'Saved', list.reload)}>{c.isActive ? 'Disable' : 'Enable'}</button></td></tr>)}</tbody></table></div>}
      <div className="row" style={{ marginTop: 10 }}>
        <select value={f.branchId} onChange={(e) => set('branchId', +e.target.value)}><option value={0}>All branches</option>
          {(branches.data?.items ?? []).map((b) => <option key={b.branchId} value={b.branchId}>{b.code}</option>)}</select>
        <input placeholder="Name" value={f.name} onChange={(e) => set('name', e.target.value)} style={{ width: 130 }} />
        <select value={f.checkType} onChange={(e) => set('checkType', e.target.value)}><option>HTTP</option><option>TCP</option></select>
        <input placeholder={f.checkType === 'HTTP' ? 'https://his.pdcl.local/health' : 'his-db.pdcl.local:1433'} value={f.target} onChange={(e) => set('target', e.target.value)} style={{ width: 240 }} />
        <select value={f.businessService} onChange={(e) => set('businessService', e.target.value)}><option>Registration</option><option>Billing</option><option>Lab Report</option></select>
        <button className="btn primary sm" disabled={!f.target} onClick={() => save(() => api.post('/app-checks', { ...f, branchId: f.branchId || null }), 'Check added', list.reload)}>Add</button>
      </div>
    </div>
  );
}

const VENDOR_OIDS: Record<string, Record<string, string>> = {
  FORTINET: { cpu_pct: '1.3.6.1.4.1.12356.101.4.1.3.0', mem_pct: '1.3.6.1.4.1.12356.101.4.1.4.0', uptime_s: '1.3.6.1.2.1.1.3.0|div:100' },
  CISCO: { cpu_pct: '1.3.6.1.4.1.9.9.109.1.1.1.1.8.1', uptime_s: '1.3.6.1.2.1.1.3.0|div:100' },
  MIKROTIK: { cpu_pct: '1.3.6.1.2.1.25.3.3.1.2.1', uptime_s: '1.3.6.1.2.1.1.3.0|div:100' },
  APC: { ups_on_battery: '1.3.6.1.2.1.33.1.4.1.0|eq:5', ups_battery_pct: '1.3.6.1.2.1.33.1.2.4.0', ups_input_v: '1.3.6.1.2.1.33.1.3.3.1.3.1', ups_runtime_min: '1.3.6.1.2.1.33.1.2.3.0' },
};

function SnmpProfiles() {
  const list = useLoad(() => api.get<{ snmpProfileId: number; name: string; vendor?: string; snmpUser: string; authProtocol: string; privProtocol: string; oids: string }[]>('/snmp-profiles'), []);
  const [f, set] = useForm({ name: '', vendor: 'FORTINET', snmpUser: 'nms-ro', authProtocol: 'SHA', privProtocol: 'AES', authSecret: '', privSecret: '', oids: JSON.stringify(VENDOR_OIDS.FORTINET, null, 1) });
  const save = useSave();
  const toast = useToast();
  const add = () => {
    let oids: Record<string, string>;
    try { oids = JSON.parse(f.oids); } catch { toast('OID map must be valid JSON', true); return; }
    save(() => api.post('/snmp-profiles', { ...f, oids }), 'Profile saved (secrets encrypted)', list.reload);
  };
  return (
    <div className="card"><h3>SNMPv3 profiles <span className="sub">authPriv only · secrets are write-only</span></h3>
      {!list.data ? <Loading error={list.error} /> : <div className="tablewrap"><table><thead><tr><th>Name</th><th>Vendor</th><th>User</th><th>Auth / Priv</th><th>Metrics</th></tr></thead>
        <tbody>{list.data.map((p) => <tr key={p.snmpProfileId}><td>{p.name}</td><td>{p.vendor}</td><td>{p.snmpUser}</td><td>{p.authProtocol} / {p.privProtocol}</td>
          <td className="small">{Object.keys(JSON.parse(p.oids || '{}')).join(', ')}</td></tr>)}</tbody></table></div>}
      <h3 style={{ marginTop: 16 }}>New profile</h3>
      <div className="form">
        <F label="Name"><input value={f.name} onChange={(e) => set('name', e.target.value)} placeholder="FortiGate read-only" /></F>
        <F label="Vendor"><select value={f.vendor} onChange={(e) => { set('vendor', e.target.value); set('oids', JSON.stringify(VENDOR_OIDS[e.target.value] ?? {}, null, 1)); }}>
          {Object.keys(VENDOR_OIDS).map((v) => <option key={v}>{v}</option>)}</select></F>
        <F label="SNMP user"><input value={f.snmpUser} onChange={(e) => set('snmpUser', e.target.value)} /></F>
        <F label="Auth / Privacy"><div className="row">
          <select value={f.authProtocol} onChange={(e) => set('authProtocol', e.target.value)}><option>SHA</option><option>SHA256</option></select>
          <select value={f.privProtocol} onChange={(e) => set('privProtocol', e.target.value)}><option>AES</option><option>AES256</option></select></div></F>
        <F label="Auth passphrase"><input type="password" value={f.authSecret} onChange={(e) => set('authSecret', e.target.value)} autoComplete="new-password" /></F>
        <F label="Privacy passphrase"><input type="password" value={f.privSecret} onChange={(e) => set('privSecret', e.target.value)} autoComplete="new-password" /></F>
        <F label="OID map (metric_key → OID, optional |eq:N |div:N)" full><textarea rows={6} value={f.oids} onChange={(e) => set('oids', e.target.value)} style={{ fontFamily: 'Consolas, monospace', fontSize: 12 }} /></F>
      </div>
      <button className="btn primary" style={{ marginTop: 10 }} disabled={!f.name || !f.authSecret || !f.privSecret} onClick={add}>Save profile</button>
    </div>
  );
}

// ───────── import ─────────
interface ImportResult { dryRun: boolean; summary: { totalRows: number; valid: number; invalid: number; create: number; update: number; errors: { line: number; errors: string[] }[] } }
function Import() {
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const toast = useToast();
  const run = async (dryRun: boolean) => {
    if (!file) return;
    setBusy(true);
    const fd = new FormData();
    fd.append('file', file);
    try {
      const r = await api.upload<ImportResult>(`/devices/import?dryRun=${dryRun}`, fd);
      setResult(r);
      if (!r.dryRun) toast(`Imported: ${r.summary.create} created, ${r.summary.update} updated`);
    } catch (e) { toast((e as Error).message, true); } finally { setBusy(false); }
  };
  return (
    <div className="card"><h3>Bulk import devices <span className="sub"><a className="btn sm" href="/api/v1/devices/import/template">Download CSV template</a></span></h3>
      <p className="small muted">CSV or Excel (.xlsx). Rows are matched by branch + host name: existing devices are updated, new ones created. Always preview first.</p>
      <div className="row">
        <input type="file" accept=".csv,.xlsx" onChange={(e) => { setFile(e.target.files?.[0] ?? null); setResult(null); }} />
        <button className="btn" disabled={!file || busy} onClick={() => run(true)}>Preview (dry run)</button>
        <button className="btn primary" disabled={!file || busy || !result || result.summary.invalid > 0 || !result.dryRun} onClick={() => run(false)}>Import</button>
      </div>
      {result && (
        <div style={{ marginTop: 14 }}>
          <div className={result.summary.invalid ? 'err' : 'okmsg'}>
            {result.dryRun ? 'Preview' : 'Imported'}: {result.summary.totalRows} rows · {result.summary.valid} valid · {result.summary.invalid} with errors · {result.summary.create} new · {result.summary.update} updates
          </div>
          {result.summary.errors.length > 0 && <table style={{ marginTop: 8 }}><thead><tr><th>Line</th><th>Problems</th></tr></thead>
            <tbody>{result.summary.errors.map((e) => <tr key={e.line}><td>{e.line}</td><td>{e.errors.join('; ')}</td></tr>)}</tbody></table>}
        </div>
      )}
    </div>
  );
}
