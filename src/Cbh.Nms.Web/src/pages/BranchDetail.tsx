import { useCallback, useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api, BranchStatus, IncidentRow, Paged, qs } from '../api';
import TimeChart, { Series } from '../components/Chart';
import MaintenanceDialog from '../components/MaintenanceDialog';
import { fmtDuration, fmtSeconds, fmtTime, Loading, Pill, SevTag, StTag, STATUS_COLOR, useCan, useLoad } from '../components/ui';
import { useLive } from '../live';

interface BranchInfo {
  branchId: number; code: string; name: string; region: string; address?: string; tier: number; mplsRouterIp?: string;
  slaTargetPct: number; status: BranchStatus; statusSinceUtc?: string; statusDetail?: string;
  contacts: { name: string; role?: string; phone?: string; email?: string; isPrimary: boolean }[];
  ispLinks: { ispLinkId: number; isp: string; ispSupportPhone?: string; linkRole: string; circuitId?: string; bandwidthMbps: number; isActive: boolean }[];
  agents: { agentId: number; hostName: string; agentVersion?: string; lastSeenAt?: string; bufferRows?: number }[];
}
interface Latest { entity: string; id: number; key: string; value: number; time: string }
interface DeviceRow { deviceId: number; type: string; vendor?: string; hostName: string; ipAddress: string; isCritical: boolean; isActive: boolean; parentDeviceIds: number[]; model?: string }
interface History { status: BranchStatus; startedAt: string; endedAt?: string; inMaintenance: boolean }

const TABS = [['sum', 'Summary'], ['dev', 'Devices'], ['tl', 'Timeline'], ['inc', 'Incidents']] as const;

/** SCR-03: everything about one branch, so the engineer can guess the problem before phoning. */
export default function BranchDetail() {
  const id = +useParams().id!;
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab') ?? 'sum';
  const nav = useNavigate();
  const can = useCan();
  const [maint, setMaint] = useState(false);

  const branch = useLoad(() => api.get<BranchInfo>(`/branches/${id}`), [id]);
  const latest = useLoad(() => api.get<{ items: Latest[] }>(`/branches/${id}/latest`), [id], 60000);
  const devices = useLoad(() => api.get<{ items: DeviceRow[] }>(`/devices?branchId=${id}`), [id]);
  const apps = useLoad(() => api.get<{ appCheckId: number; name: string; checkType: string; target: string; isActive: boolean }[]>(`/app-checks?branchId=${id}`), [id]);

  useLive(useCallback((e: string, p: unknown) => {
    const bid = (p as { branchId?: number })?.branchId;
    if (e === 'reconnected' || bid === id) { branch.reload(); latest.reload(); }
  }, [id, branch.reload, latest.reload]));

  const val = (entity: string, eid: number, key: string) => latest.data?.items.find((x) => x.entity === entity && x.id === eid && x.key === key)?.value;
  const b = branch.data;
  if (!b) return <Loading error={branch.error} />;
  const primaryContact = b.contacts.find((c) => c.isPrimary) ?? b.contacts[0];
  const ups = (devices.data?.items ?? []).filter((d) => d.type === 'UPS');

  return (
    <>
      <div className="bhead">
        <div>
          <h2>{b.name} <Pill s={b.status} /></h2>
          <div className="info">
            {b.code} · {b.region} region · Tier {b.tier}{b.address ? ` · ${b.address}` : ''} · status since {fmtTime(b.statusSinceUtc, true)} ({fmtDuration(b.statusSinceUtc)})
          </div>
          {b.statusDetail && b.status !== 'ONLINE' && <div className="info"><b>Why:</b> {JSON.parse(b.statusDetail).reason}</div>}
          <div className="info">
            {primaryContact ? <>Branch IT: {primaryContact.name} {primaryContact.phone && <a href={`tel:${primaryContact.phone}`}>☎ {primaryContact.phone}</a>}</> : 'No branch contact recorded'}
            {b.ispLinks.map((l) => <span key={l.ispLinkId}> · {l.isp} {l.circuitId && <>({l.circuitId})</>} {l.ispSupportPhone && <a href={`tel:${l.ispSupportPhone}`}>☎ {l.ispSupportPhone}</a>}</span>)}
          </div>
        </div>
        <div className="actions">
          {can('maintenance') && <button className="btn" onClick={() => setMaint(true)}>Start Maintenance</button>}
          {can('inventory.edit') && <button className="btn" onClick={() => nav(`/inventory?branch=${id}`)}>Edit Branch</button>}
          {can('reports') && <a className="btn primary" href={`/api/v1/reports/availability${qs({ branchId: id, format: 'pdf' })}`}>Export Report</a>}
        </div>
      </div>

      <div className="mini">
        {b.ispLinks.filter((l) => l.linkRole !== 'MPLS').slice(0, 2).map((l) => {
          const up = val('ISPLINK', l.ispLinkId, 'up');
          return (
            <div className="card" key={l.ispLinkId}>
              <div className="hd">{l.linkRole === 'PRIMARY' ? 'Primary' : 'Secondary'} ISP {up == null ? <Pill s="UNKNOWN" /> : <Pill s={up ? 'ONLINE' : 'OFFLINE_ISP'} />}</div>
              <div className="kv">Latency <b>{fmt(val('ISPLINK', l.ispLinkId, 'latency_ms'), 'ms')}</b></div>
              <div className="kv">Loss <b>{fmt(val('ISPLINK', l.ispLinkId, 'loss_pct'), '%')}</b></div>
              <div className="kv">Circuit <b>{l.isp} · {l.bandwidthMbps} Mbps</b></div>
            </div>
          );
        })}
        <div className="card">
          <div className="hd">HO link (MPLS) {val('BRANCH', id, 'up') == null ? <Pill s="UNKNOWN" /> : <Pill s={val('BRANCH', id, 'up') ? 'ONLINE' : 'OFFLINE_ISP'} />}</div>
          <div className="kv">Latency <b>{fmt(val('BRANCH', id, 'latency_ms'), 'ms')}</b></div>
          <div className="kv">Jitter <b>{fmt(val('BRANCH', id, 'jitter_ms'), 'ms')}</b></div>
          <div className="kv">Loss <b>{fmt(val('BRANCH', id, 'loss_pct'), '%')}</b></div>
        </div>
        <div className="card">
          {ups.length === 0 ? <><div className="hd">UPS <span className="muted small">none</span></div><div className="kv">No UPS in inventory</div></> : ups.slice(0, 1).map((u) => {
            const onBat = val('DEVICE', u.deviceId, 'ups_on_battery');
            return (
              <div key={u.deviceId}>
                <div className="hd">UPS {onBat == null ? <Pill s="UNKNOWN" /> : onBat ? <Pill s="OFFLINE_POWER" /> : <Pill s="ONLINE" />}</div>
                <div className="kv">Input <b>{fmt(val('DEVICE', u.deviceId, 'ups_input_v'), 'V')}</b></div>
                <div className="kv">Battery <b>{fmt(val('DEVICE', u.deviceId, 'ups_battery_pct'), '%')}</b></div>
                <div className="kv">Runtime <b>{fmt(val('DEVICE', u.deviceId, 'ups_runtime_min'), 'min')}</b></div>
              </div>
            );
          })}
        </div>
        <div className="card">
          <div className="hd">HIS / LIS</div>
          {(apps.data ?? []).filter((a) => a.isActive).length === 0 && <div className="kv">No checks configured</div>}
          {(apps.data ?? []).filter((a) => a.isActive).map((a) => {
            const up = val('APP', a.appCheckId, 'up');
            return <div className="kv" key={a.appCheckId}>{a.name}
              <b>{up == null ? '—' : up ? `✓ ${fmt(val('APP', a.appCheckId, 'response_ms'), 'ms')}` : <span style={{ color: 'var(--crit)' }}>✕ {val('APP', a.appCheckId, 'http_status') ?? 'down'}</span>}</b></div>;
          })}
        </div>
      </div>

      <div className="tabs">
        {TABS.map(([k, l]) => <a key={k} className={tab === k ? 'on' : ''} onClick={() => setParams({ tab: k })}>{l}</a>)}
      </div>
      {tab === 'sum' && <SummaryTab id={id} links={b.ispLinks} />}
      {tab === 'dev' && <DevicesTab devices={devices.data?.items} val={val} />}
      {tab === 'tl' && <TimelineTab id={id} />}
      {tab === 'inc' && <IncidentsTab id={id} />}
      {maint && <MaintenanceDialog branchId={id} branchName={b.name} devices={devices.data?.items ?? []} onClose={() => setMaint(false)} />}
    </>
  );
}

const fmt = (v: number | undefined, unit: string) => (v == null ? '—' : `${Math.round(v * 10) / 10} ${unit}`);

function SummaryTab({ id, links }: { id: number; links: BranchInfo['ispLinks'] }) {
  const to = useMemo(() => new Date().toISOString(), []);
  const from24 = useMemo(() => new Date(Date.now() - 24 * 3600e3).toISOString(), []);
  const lat = useLoad(async () => {
    const reqs: Promise<Series>[] = [
      api.get<{ points: { t: string; avg: number }[] }>(`/branches/${id}/metrics${qs({ key: 'latency_ms', from: from24, to })}`)
        .then((r) => ({ name: 'HO (MPLS)', data: r.points.map((p) => ({ x: +new Date(p.t), y: p.avg })) })),
      ...links.filter((l) => l.linkRole !== 'MPLS').map((l) =>
        api.get<{ points: { t: string; avg: number }[] }>(`/branches/${id}/metrics${qs({ key: 'latency_ms', entity: 'ISPLINK', entityId: l.ispLinkId, from: from24, to })}`)
          .then((r) => ({ name: `${l.isp} (${l.linkRole.toLowerCase()})`, data: r.points.map((p) => ({ x: +new Date(p.t), y: p.avg })) }))),
    ];
    return Promise.all(reqs);
  }, [id]);
  const loss = useLoad(() => api.get<{ points: { t: string; avg: number }[] }>(`/branches/${id}/metrics${qs({ key: 'loss_pct', from: from24, to })}`)
    .then((r) => [{ name: 'Packet loss', data: r.points.map((p) => ({ x: +new Date(p.t), y: p.avg })) }]), [id]);

  return (
    <div className="grid g2">
      <div className="card"><h3>Latency — last 24 h <span className="sub">ms</span></h3>
        {lat.data ? <TimeChart series={lat.data} unit="ms" threshold={300} /> : <Loading error={lat.error} />}</div>
      <div className="card"><h3>Packet loss to HO — last 24 h <span className="sub">%</span></h3>
        {loss.data ? <TimeChart series={loss.data} unit="%" threshold={5} /> : <Loading error={loss.error} />}</div>
      <div className="card" style={{ gridColumn: '1 / -1' }}><h3>Availability — last 30 days</h3><DayBars id={id} days={30} /></div>
    </div>
  );
}

/** One bar per day, coloured by the worst status that day (maintenance shown grey). */
function DayBars({ id, days }: { id: number; days: number }) {
  const from = useMemo(() => new Date(Date.now() - days * 86400e3), [days]);
  const hist = useLoad(() => api.get<{ items: History[] }>(`/branches/${id}/timeline${qs({ from: from.toISOString() })}`), [id, days]);
  if (!hist.data) return <Loading error={hist.error} />;
  const rank: Record<string, number> = { OFFLINE_POWER: 4, OFFLINE_ISP: 3, DEGRADED: 2, UNKNOWN: 1, ONLINE: 0 };
  const bars = Array.from({ length: days }, (_, i) => {
    const s = new Date(from.getTime() + i * 86400e3), e = new Date(s.getTime() + 86400e3);
    const over = hist.data!.items.filter((h) => new Date(h.startedAt) < e && (!h.endedAt || new Date(h.endedAt) > s));
    if (over.length === 0) return { color: 'var(--line)', label: 'no data', day: s };
    if (over.every((h) => h.inMaintenance || h.status === 'ONLINE') && over.some((h) => h.inMaintenance)) return { color: '#c9ced6', label: 'maintenance', day: s };
    const worst = over.filter((h) => !h.inMaintenance).sort((a, b) => rank[b.status] - rank[a.status])[0] ?? over[0];
    return { color: STATUS_COLOR[worst.status], label: worst.status, day: s };
  });
  return (
    <>
      <div className="tl">{bars.map((b, i) => <span key={i} title={`${b.day.toDateString()}: ${b.label}`} style={{ flex: 1, background: b.color, borderRight: '1px solid #fff' }} />)}</div>
      <div className="legend"><span><i style={{ background: 'var(--ok)' }} />Online</span><span><i style={{ background: 'var(--warn)' }} />Degraded</span>
        <span><i style={{ background: 'var(--crit)' }} />Offline-ISP</span><span><i style={{ background: 'var(--pwr)' }} />Offline-Power</span>
        <span><i style={{ background: '#c9ced6' }} />Maintenance / Unknown</span></div>
    </>
  );
}

function DevicesTab({ devices, val }: { devices?: DeviceRow[]; val: (e: string, id: number, k: string) => number | undefined }) {
  if (!devices) return <Loading />;
  const byId = new Map(devices.map((d) => [d.deviceId, d]));
  const roots = devices.filter((d) => d.parentDeviceIds.length === 0);
  const children = (pid: number) => devices.filter((d) => d.parentDeviceIds.includes(pid));
  const Tree = ({ d, depth }: { d: DeviceRow; depth: number }): JSX.Element => (
    <>
      <div style={{ paddingLeft: depth * 18 }} className="small">{depth ? '└ ' : ''}<b>{d.hostName}</b> <span className="muted">{d.type}</span></div>
      {children(d.deviceId).map((c) => <Tree key={c.deviceId} d={c} depth={depth + 1} />)}
    </>
  );
  return (
    <div className="grid" style={{ gridTemplateColumns: 'minmax(0,3fr) minmax(220px,1fr)' }}>
      <div className="card"><h3>Devices <span className="sub">{devices.length} in inventory</span></h3>
        <div className="tablewrap"><table>
          <thead><tr><th>Device</th><th>Type</th><th>Vendor</th><th>IP</th><th>Status</th><th className="num">CPU</th><th className="num">Latency</th><th>Uptime</th><th>Parent</th></tr></thead>
          <tbody>{devices.map((d) => {
            const up = val('DEVICE', d.deviceId, 'up');
            return (
              <tr key={d.deviceId}>
                <td><b>{d.hostName}</b>{d.isCritical && <span className="sev CRITICAL" style={{ marginLeft: 6 }}>critical</span>}</td>
                <td>{d.type}</td><td>{d.vendor ?? '—'}</td><td>{d.ipAddress}</td>
                <td>{!d.isActive ? <span className="muted">inactive</span> : up == null ? <Pill s="UNKNOWN" /> : <Pill s={up ? 'ONLINE' : 'OFFLINE_ISP'} />}</td>
                <td className="num">{fmt(val('DEVICE', d.deviceId, 'cpu_pct'), '%')}</td>
                <td className="num">{fmt(val('DEVICE', d.deviceId, 'latency_ms'), 'ms')}</td>
                <td>{fmtSeconds(val('DEVICE', d.deviceId, 'uptime_s'))}</td>
                <td>{d.parentDeviceIds.map((p) => byId.get(p)?.hostName).join(', ') || '—'}</td>
              </tr>
            );
          })}</tbody>
        </table></div>
      </div>
      <div className="card"><h3>Dependency tree</h3>{roots.map((r) => <Tree key={r.deviceId} d={r} depth={0} />)}</div>
    </div>
  );
}

function TimelineTab({ id }: { id: number }) {
  const [range, setRange] = useState(1);
  const from = useMemo(() => new Date(Date.now() - range * 86400e3), [range]);
  const hist = useLoad(() => api.get<{ items: History[] }>(`/branches/${id}/timeline${qs({ from: from.toISOString() })}`), [id, range]);
  const total = Date.now() - from.getTime();
  return (
    <div className="card">
      <h3>Status timeline <span className="sub">
        {[1, 7, 30].map((d) => <span key={d} className={`chip ${range === d ? 'on' : ''}`} onClick={() => setRange(d)}>{d === 1 ? '24 h' : `${d} days`}</span>)}</span></h3>
      {!hist.data ? <Loading error={hist.error} /> : <>
        <div className="tl">{hist.data.items.map((h, i) => {
          const s = Math.max(new Date(h.startedAt).getTime(), from.getTime());
          const e = h.endedAt ? new Date(h.endedAt).getTime() : Date.now();
          const left = i === 0 ? s - from.getTime() : 0;
          return <span key={i} title={`${h.status} ${fmtTime(h.startedAt, true)} – ${h.endedAt ? fmtTime(h.endedAt, true) : 'now'}`}
            style={{ flex: `${Math.max(e - s, 0) + left} 0 0`, background: h.inMaintenance ? '#c9ced6' : STATUS_COLOR[h.status] }} />;
        })}</div>
        <div className="legend"><span>{fmtTime(from.toISOString(), true)}</span><span style={{ marginLeft: 'auto' }}>now</span></div>
        <div className="tablewrap" style={{ marginTop: 14 }}><table>
          <thead><tr><th>From</th><th>To</th><th>Status</th><th>Duration</th><th>Maintenance</th></tr></thead>
          <tbody>{[...hist.data.items].reverse().map((h, i) => (
            <tr key={i}><td>{fmtTime(h.startedAt, true)}</td><td>{h.endedAt ? fmtTime(h.endedAt, true) : 'now'}</td>
              <td><Pill s={h.status} /></td><td>{fmtDuration(h.startedAt, h.endedAt)}</td><td>{h.inMaintenance ? 'yes' : ''}</td></tr>
          ))}</tbody>
        </table></div>
        {total > 0 && hist.data.items.length === 0 && <div className="empty">No status changes in this period.</div>}
      </>}
    </div>
  );
}

function IncidentsTab({ id }: { id: number }) {
  const nav = useNavigate();
  const list = useLoad(() => api.get<Paged<IncidentRow>>(`/incidents${qs({ branchId: id, status: 'all', pageSize: 100 })}`), [id]);
  if (!list.data) return <Loading error={list.error} />;
  return (
    <div className="card"><h3>Incidents at this branch</h3>
      {list.data.items.length === 0 ? <div className="empty">No incidents recorded.</div> : (
        <div className="tablewrap"><table>
          <thead><tr><th>No.</th><th>Title</th><th>Severity</th><th>Status</th><th>Root cause</th><th>Detected</th><th className="num">MTTR</th></tr></thead>
          <tbody>{list.data.items.map((i) => (
            <tr key={i.incidentId} className="click" onClick={() => nav(`/incidents/${i.incidentId}`)}>
              <td><b>{i.incidentNo}</b></td><td>{i.title}</td><td><SevTag s={i.severity} /></td><td><StTag s={i.status} /></td>
              <td>{i.rootCause ?? '—'}</td><td>{fmtTime(i.detectedAt, true)}</td><td className="num">{i.resolvedAt ? fmtDuration(i.detectedAt, i.resolvedAt) : '—'}</td>
            </tr>
          ))}</tbody>
        </table></div>
      )}
    </div>
  );
}
