import { useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api, qs } from '../api';
import TimeChart, { Series } from '../components/Chart';
import { fmtDuration, fmtSeconds, fmtTime, Loading, useCan, useLoad, useToast } from '../components/ui';

interface Disk { name: string; totalGb: number; freeGb: number; isSystem: boolean }
interface Proc { name: string; cpuPct: number; memMb: number; connections: number }
interface Nic { name: string; type: string; ip?: string; mac?: string; speedMbps: number; up: boolean }
interface Security {
  antivirusSupported: boolean; antivirus: { name: string; enabled: boolean; upToDate: boolean }[];
  firewallDomain?: boolean; firewallPrivate?: boolean; firewallPublic?: boolean; lastUpdateInstalled?: string; pendingReboot: boolean;
}
interface Live {
  time: string;
  health: { cpuPct: number; memPct: number; memTotalMb: number; uptimeS: number; disks: Disk[]; topProcesses: Proc[] };
  network: { primaryIp?: string; gateway?: string; gatewayRttMs?: number; gatewayLossPct?: number; hoRttMs?: number; hoLossPct?: number; rxKbps: number; txKbps: number };
}
interface Detail {
  endpointId: number; branchId: number; branchCode: string; branchName: string; hostName: string; domain?: string; osName?: string; osVersion?: string;
  osBuild?: string; manufacturer?: string; model?: string; serialNo?: string; cpuModel?: string; cpuCores?: number; ramMb?: number; lastBootAt?: string;
  ip?: string; mac?: string; lastUser?: string; security?: Security; nics?: Nic[]; live?: Live; lastSeenAt?: string; inventoryAt?: string;
  registeredAt: string; agentVersion?: string; online: boolean;
}

export default function EndpointDetail() {
  const id = +useParams().id!;
  const [params, setParams] = useSearchParams();
  const can = useCan();
  const nav = useNavigate();
  const toast = useToast();
  const tab = params.get('tab') ?? 'overview';
  const d = useLoad(() => api.get<Detail>(`/endpoints/${id}`), [id], 60000);
  if (!d.data) return <Loading error={d.error} />;
  const e = d.data;
  const tabs: [string, string][] = [['overview', 'Overview'], ['software', 'Software'], ['network', 'Network']];
  if (can('endpoint.activity')) tabs.push(['activity', 'Activity']);
  const revoke = async () => {
    if (!confirm(`Remove ${e.hostName}? Its agent stops being accepted; re-installing with a key adds it again.`)) return;
    await api.del(`/endpoints/${id}`); toast('PC removed'); nav('/endpoints');
  };

  return (
    <>
      <div className="bhead">
        <div>
          <h2>{e.hostName} {e.online ? <span className="pill s-ONLINE">● Online</span> : <span className="pill s-UNKNOWN">Offline</span>}</h2>
          <div className="info">{e.branchCode} · {e.branchName} · {e.lastUser ? `user ${e.lastUser}` : 'nobody logged on'} · {e.ip ?? 'no IP'}
            {' · '}{e.online ? 'reporting now' : e.lastSeenAt ? `last seen ${fmtDuration(e.lastSeenAt)} ago` : 'never reported'}</div>
          <div className="info muted">{[e.manufacturer, e.model].filter(Boolean).join(' ')} {e.serialNo && `· S/N ${e.serialNo}`} · agent {e.agentVersion ?? '?'}</div>
        </div>
        <div className="actions">
          <button className="btn" onClick={() => nav(`/branches/${e.branchId}`)}>Branch</button>
          {can('inventory.edit') && <button className="btn danger" onClick={revoke}>Remove PC</button>}
        </div>
      </div>
      <div className="tabs">{tabs.map(([k, l]) => <a key={k} className={tab === k ? 'on' : ''} onClick={() => setParams({ tab: k })}>{l}</a>)}</div>
      {tab === 'overview' && <Overview e={e} />}
      {tab === 'software' && <Software id={id} />}
      {tab === 'network' && <Network e={e} />}
      {tab === 'activity' && can('endpoint.activity') && <Activity id={id} />}
    </>
  );
}

const yes = (ok: boolean | undefined, good: string, bad: string) =>
  ok == null ? <span className="muted">unknown</span> : ok ? <span style={{ color: 'var(--ok)' }}>✓ {good}</span> : <b style={{ color: 'var(--crit)' }}>✕ {bad}</b>;

function Overview({ e }: { e: Detail }) {
  const h = e.live?.health;
  const s = e.security;
  return (
    <>
      <div className="mini">
        <div className="card"><div className="hd">CPU</div><div className="big">{h ? `${Math.round(h.cpuPct)}%` : '—'}</div><div className="kv">{e.cpuModel ?? ''} {e.cpuCores ? `· ${e.cpuCores} cores` : ''}</div></div>
        <div className="card"><div className="hd">Memory</div><div className="big">{h ? `${Math.round(h.memPct)}%` : '—'}</div><div className="kv">of {h ? (h.memTotalMb / 1024).toFixed(1) : '?'} GB</div></div>
        <div className="card"><div className="hd">Disks</div>
          {(h?.disks ?? []).map((dk) => {
            const free = dk.totalGb > 0 ? (100 * dk.freeGb) / dk.totalGb : 0;
            return <div className="kv" key={dk.name}>{dk.name}<b style={free < 10 ? { color: 'var(--crit)' } : undefined}>{dk.freeGb} / {dk.totalGb} GB free ({Math.round(free)}%)</b></div>;
          })}
        </div>
        <div className="card"><div className="hd">System</div>
          <div className="kv">OS <b>{e.osName ?? '—'}</b></div>
          <div className="kv">Build <b>{e.osBuild ?? '—'}</b></div>
          <div className="kv">Uptime <b>{h ? fmtSeconds(h.uptimeS) : '—'}</b></div>
          <div className="kv">Domain <b>{e.domain ?? '—'}</b></div>
        </div>
        <div className="card"><div className="hd">Security</div>
          {!s ? <div className="kv">Waiting for first inventory</div> : <>
            <div className="kv">Antivirus <span>{!s.antivirusSupported ? <span className="muted">n/a (server OS)</span>
              : s.antivirus.length === 0 ? <b style={{ color: 'var(--crit)' }}>✕ none</b>
              : s.antivirus.map((a) => <div key={a.name}>{a.name}: {yes(a.enabled, 'on', 'off')}{a.enabled && !a.upToDate && <b style={{ color: 'var(--warn)' }}> · out of date</b>}</div>)}</span></div>
            <div className="kv">Firewall <span>{yes(s.firewallDomain !== false && s.firewallPrivate !== false && s.firewallPublic !== false, 'on', 'off on some profiles')}</span></div>
            <div className="kv">Last update <b>{s.lastUpdateInstalled ? fmtTime(s.lastUpdateInstalled, true) : '—'}</b></div>
            <div className="kv">Pending reboot <span>{s.pendingReboot ? <b style={{ color: 'var(--warn)' }}>yes</b> : 'no'}</span></div>
          </>}
        </div>
      </div>
      <div className="grid g2" style={{ marginTop: 16 }}>
        <MetricChart id={e.endpointId} title="CPU and memory — last 24 h" unit="%" keys={[['cpu_pct', 'CPU'], ['mem_pct', 'Memory']]} />
        <div className="card"><h3>Top processes <span className="sub">at {fmtTime(e.live?.time)}</span></h3>
          {!h ? <div className="empty">No data yet.</div> : (
            <div className="tablewrap"><table>
              <thead><tr><th>Process</th><th className="num">CPU</th><th className="num">Memory</th><th className="num">Connections</th></tr></thead>
              <tbody>{h.topProcesses.slice(0, 15).map((p) => (
                <tr key={p.name}><td>{p.name}</td><td className="num">{p.cpuPct}%</td><td className="num">{p.memMb} MB</td><td className="num">{p.connections || ''}</td></tr>
              ))}</tbody>
            </table></div>
          )}
        </div>
      </div>
      <div className="muted small" style={{ marginTop: 10 }}>Inventory updated {fmtTime(e.inventoryAt, true)} · registered {fmtTime(e.registeredAt, true)}{e.lastBootAt && ` · last boot ${fmtTime(e.lastBootAt, true)}`}</div>
    </>
  );
}

function MetricChart({ id, title, unit, keys, threshold }: { id: number; title: string; unit: string; keys: [string, string][]; threshold?: number }) {
  const to = useMemo(() => new Date().toISOString(), []);
  const from = useMemo(() => new Date(Date.now() - 24 * 3600e3).toISOString(), []);
  const data = useLoad(() => Promise.all(keys.map(([k, name]) =>
    api.get<{ points: { t: string; avg: number }[] }>(`/endpoints/${id}/metrics${qs({ key: k, from, to })}`)
      .then((r): Series => ({ name, data: r.points.map((p) => ({ x: +new Date(p.t), y: p.avg })) })))), [id]);
  return (
    <div className="card"><h3>{title} <span className="sub">{unit}</span></h3>
      {data.data ? <TimeChart series={data.data} unit={unit} threshold={threshold} /> : <Loading error={data.error} />}</div>
  );
}

function Software({ id }: { id: number }) {
  const [q, setQ] = useState('');
  const list = useLoad(() => api.get<{ items: { name: string; version?: string; publisher?: string; installDate?: string; flags?: string }[] }>(`/endpoints/${id}/software`), [id]);
  const events = useLoad(() => api.get<{ items: { time: string; action: string; name: string; version?: string; oldVersion?: string }[] }>(`/endpoints/${id}/software-events`), [id]);
  const items = (list.data?.items ?? []).filter((s) => !q || s.name.toLowerCase().includes(q.toLowerCase()));
  return (
    <div className="grid" style={{ gridTemplateColumns: 'minmax(0,3fr) minmax(260px,2fr)' }}>
      <div className="card"><h3>Installed software <span className="sub">{list.data?.items.length ?? 0} programs <input placeholder="Filter" value={q} onChange={(e) => setQ(e.target.value)} /></span></h3>
        {!list.data ? <Loading error={list.error} /> : (
          <div className="tablewrap"><table>
            <thead><tr><th>Name</th><th>Version</th><th>Publisher</th><th>Installed</th></tr></thead>
            <tbody>{items.map((s, i) => (
              <tr key={i}><td><b>{s.name}</b>{s.flags?.includes('BLOCKED') && <span className="sev HIGH" style={{ marginLeft: 6 }}>blocked</span>}</td>
                <td>{s.version ?? ''}</td><td className="small">{s.publisher ?? ''}</td><td className="small" style={{ whiteSpace: 'nowrap' }}>{s.installDate ?? ''}</td></tr>
            ))}</tbody>
          </table></div>
        )}
      </div>
      <div className="card"><h3>Changes</h3>
        {!events.data ? <Loading error={events.error} /> : events.data.items.length === 0 ? <div className="empty">No installs or removals since the first inventory.</div> : (
          <div className="tablewrap"><table>
            <thead><tr><th>When</th><th>Change</th><th>Software</th></tr></thead>
            <tbody>{events.data.items.map((ev, i) => (
              <tr key={i}><td className="small">{fmtTime(ev.time, true)}</td>
                <td><b style={{ color: ev.action === 'INSTALLED' ? 'var(--brand)' : ev.action === 'REMOVED' ? 'var(--crit)' : 'var(--muted)' }}>{ev.action.toLowerCase()}</b></td>
                <td>{ev.name} <span className="muted small">{ev.oldVersion ? `${ev.oldVersion} → ` : ''}{ev.version}</span></td></tr>
            ))}</tbody>
          </table></div>
        )}
      </div>
    </div>
  );
}

function Network({ e }: { e: Detail }) {
  const n = e.live?.network;
  return (
    <>
      <div className="mini">
        <div className="card"><div className="hd">Gateway</div><div className="big">{n?.gatewayRttMs != null ? `${n.gatewayRttMs} ms` : '—'}</div>
          <div className="kv">{n?.gateway ?? '—'} · loss {n?.gatewayLossPct ?? '—'}%</div></div>
        <div className="card"><div className="hd">Head Office</div><div className="big">{n?.hoRttMs != null ? `${n.hoRttMs} ms` : '—'}</div>
          <div className="kv">loss {n?.hoLossPct ?? '—'}%</div></div>
        <div className="card"><div className="hd">Traffic now</div>
          <div className="kv">Download <b>{n ? `${(n.rxKbps / 1000).toFixed(2)} Mbps` : '—'}</b></div>
          <div className="kv">Upload <b>{n ? `${(n.txKbps / 1000).toFixed(2)} Mbps` : '—'}</b></div></div>
      </div>
      <div className="grid g2" style={{ marginTop: 16 }}>
        <MetricChart id={e.endpointId} title="Latency — last 24 h" unit="ms" keys={[['gw_rtt_ms', 'Gateway'], ['ho_rtt_ms', 'Head Office']]} />
        <MetricChart id={e.endpointId} title="Traffic — last 24 h" unit="kbps" keys={[['rx_kbps', 'Download'], ['tx_kbps', 'Upload']]} />
      </div>
      <div className="card" style={{ marginTop: 16 }}><h3>Network adapters</h3>
        <div className="tablewrap"><table>
          <thead><tr><th>Adapter</th><th>Type</th><th>IP</th><th>MAC</th><th className="num">Speed</th><th>State</th></tr></thead>
          <tbody>{(e.nics ?? []).map((x) => (
            <tr key={x.name}><td>{x.name}</td><td>{x.type}</td><td>{x.ip ?? '—'}</td><td className="small">{x.mac ?? ''}</td>
              <td className="num">{x.speedMbps ? `${x.speedMbps} Mbps` : ''}</td><td>{x.up ? 'up' : <span className="muted">down</span>}</td></tr>
          ))}</tbody>
        </table></div>
      </div>
      <div className="card" style={{ marginTop: 16 }}><h3>Programs with network connections <span className="sub">at {fmtTime(e.live?.time)}</span></h3>
        <div className="tablewrap"><table>
          <thead><tr><th>Process</th><th className="num">Open connections</th></tr></thead>
          <tbody>{(e.live?.health.topProcesses ?? []).filter((p) => p.connections > 0).sort((a, b) => b.connections - a.connections).map((p) => (
            <tr key={p.name}><td>{p.name}</td><td className="num">{p.connections}</td></tr>
          ))}</tbody>
        </table></div>
      </div>
    </>
  );
}

interface ActivityData {
  from: string; to: string; trackingEnabled: boolean;
  usage: { user: string; app: string; product?: string; activeSeconds: number; days: number }[];
  events: { time: string; user: string; event: string }[];
}

function Activity({ id }: { id: number }) {
  const [days, setDays] = useState(1);
  const a = useLoad(() => api.get<ActivityData>(`/endpoints/${id}/activity${qs({ days })}`), [id, days]);
  if (!a.data) return <Loading error={a.error} />;
  const users = [...new Set(a.data.usage.map((u) => u.user))];
  const hm = (s: number) => `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
  return (
    <>
      <div className="filters">
        {[1, 7, 30].map((d) => <span key={d} className={`chip ${days === d ? 'on' : ''}`} onClick={() => setDays(d)}>{d === 1 ? 'Today' : `${d} days`}</span>)}
        <span className="muted small" style={{ marginLeft: 'auto' }}>Viewing is recorded in the audit log.</span>
      </div>
      {!a.data.trackingEnabled && <div className="notice">Activity tracking is <b>off</b> (Admin → User PCs → Activity tracking). Older data is shown if any.</div>}
      {users.length === 0 ? <div className="card"><div className="empty">No application usage recorded for this period.</div></div> : users.map((u) => {
        const rows = a.data!.usage.filter((x) => x.user === u);
        const total = rows.reduce((s, x) => s + x.activeSeconds, 0);
        const max = Math.max(...rows.map((x) => x.activeSeconds));
        return (
          <div className="card" key={u} style={{ marginBottom: 16 }}>
            <h3>{u} <span className="sub">active {hm(total)}</span></h3>
            {rows.slice(0, 25).map((x) => (
              <div key={x.app} className="barrow" title={x.app}>
                <span>{x.product ?? x.app}</span>
                <span className="bar"><i style={{ width: `${(100 * x.activeSeconds) / max}%` }} /></span>
                <span className="num">{hm(x.activeSeconds)}</span>
              </div>
            ))}
          </div>
        );
      })}
      <div className="card"><h3>Sessions</h3>
        {a.data.events.length === 0 ? <div className="empty">No session events.</div> : (
          <div className="tablewrap"><table>
            <thead><tr><th>Time</th><th>User</th><th>Event</th></tr></thead>
            <tbody>{[...a.data.events].reverse().map((ev, i) => (
              <tr key={i}><td>{fmtTime(ev.time, true)}</td><td>{ev.user}</td><td>{ev.event.toLowerCase()}</td></tr>
            ))}</tbody>
          </table></div>
        )}
      </div>
    </>
  );
}
