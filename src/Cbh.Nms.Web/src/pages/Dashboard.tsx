import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { AlertRow, api, BranchStatus, Paged, Summary, Tile } from '../api';
import AckDialog from '../components/AckDialog';
import { fmtDuration, fmtTime, Loading, Pill, STATUS_ORDER, useCan, useLoad } from '../components/ui';
import { chime, useLive } from '../live';

/** SCR-02: which branch has a problem, and who is on it — in 10 seconds. */
export default function Dashboard({ summary }: { summary: Summary | null }) {
  const nav = useNavigate();
  const can = useCan();
  const tiles = useLoad(() => api.get<{ items: Tile[] }>('/dashboard/branches'), [], 60000);
  const alerts = useLoad(() => api.get<Paged<AlertRow>>('/alerts?pageSize=20'), [], 60000);
  const [region, setRegion] = useState('ALL');
  const [status, setStatus] = useState<'ALL' | 'PROBLEM' | BranchStatus>('ALL');
  const [tier, setTier] = useState(0);
  const [ack, setAck] = useState<number[] | null>(null);
  const [noc, setNoc] = useState(false);
  const gridRef = useRef<HTMLDivElement>(null);

  useLive(useCallback((e: string, p: unknown) => {
    if (e === 'branchStatusChanged' || e === 'reconnected') tiles.reload();
    if (e === 'alertRaised' || e === 'alertCleared' || e === 'reconnected') alerts.reload();
    if (e === 'alertRaised' && noc && (p as { severity?: string })?.severity === 'CRITICAL') chime();
  }, [tiles.reload, alerts.reload, noc]));

  // NOC mode (FR-DB-05): full screen, auto-scrolling tile grid, sound on new Critical.
  useEffect(() => {
    if (!noc) return;
    document.documentElement.requestFullscreen?.().catch(() => {});
    const exit = () => { if (!document.fullscreenElement) setNoc(false); };
    document.addEventListener('fullscreenchange', exit);
    const h = setInterval(() => {
      const el = document.scrollingElement!;
      if (el.scrollTop + el.clientHeight >= el.scrollHeight - 4) el.scrollTo({ top: 0, behavior: 'smooth' });
      else el.scrollBy({ top: 240, behavior: 'smooth' });
    }, 8000);
    return () => { clearInterval(h); document.removeEventListener('fullscreenchange', exit); };
  }, [noc]);

  const regions = useMemo(() => [...new Set((tiles.data?.items ?? []).map((t) => t.region))].sort(), [tiles.data]);
  const list = useMemo(() => (tiles.data?.items ?? [])
    .filter((t) => region === 'ALL' || t.region === region)
    .filter((t) => status === 'ALL' || (status === 'PROBLEM' ? t.status !== 'ONLINE' : t.status === status))
    .filter((t) => !tier || t.tier === tier)
    .sort((a, b) => STATUS_ORDER[a.status] - STATUS_ORDER[b.status] || a.code.localeCompare(b.code)), [tiles.data, region, status, tier]);
  const problems = (tiles.data?.items ?? []).filter((t) => t.status !== 'ONLINE').length;

  const s = summary;
  const kpis: [string, string, string | number, string][] = s ? [
    ['brand', 'Total branches', s.totalBranches, 'In your scope'],
    ['ok', 'Online', s.online, 'Normal'],
    ['warn', 'Degraded', s.degraded, 'Partial service'],
    ['crit', 'Offline · ISP', s.offlineIsp, 'Link / MPLS failure'],
    ['pwr', 'Offline · Power', s.offlinePower, 'UPS on battery / down'],
    ['unk', 'Unknown', s.unknown, 'Monitoring lost'],
    [s.availabilityTodayPct < 99.5 ? 'crit' : 'brand', 'Availability today', `${s.availabilityTodayPct.toFixed(2)}%`,
      `${s.openIncidents} open incidents${s.unackedIncidents ? ` · ${s.unackedIncidents} not acked` : ''}`],
  ] : [];

  return (
    <>
      <div className="kpis">
        {kpis.map(([k, l, v, h]) => (
          <div key={l} className={`kpi ${k}`}><div className="lbl">{l}</div><div className="val">{v}</div>
            <div className="hint" style={l === 'Availability today' && s!.unackedIncidents ? { color: 'var(--crit)' } : undefined}>{h}</div></div>
        ))}
      </div>
      <div className="cols">
        <div className="card" ref={gridRef}>
          <h3>Branch Status <span className="sub">Problem branches first · click a tile
            <button className="btn sm" onClick={() => setNoc((x) => !x)}>{noc ? 'Exit NOC mode' : '⛶ NOC mode'}</button></span></h3>
          <div className="filters">
            <span className={`chip ${status === 'ALL' ? 'on' : ''}`} onClick={() => setStatus('ALL')}>All ({tiles.data?.items.length ?? 0})</span>
            <span className={`chip ${status === 'PROBLEM' ? 'on' : ''}`} onClick={() => setStatus('PROBLEM')}>Problems ({problems})</span>
            <select value={region} onChange={(e) => setRegion(e.target.value)} aria-label="Region">
              <option value="ALL">All regions</option>{regions.map((r) => <option key={r}>{r}</option>)}
            </select>
            <select value={status} onChange={(e) => setStatus(e.target.value as typeof status)} aria-label="Status">
              <option value="ALL">Any status</option><option value="PROBLEM">Any problem</option>
              <option value="OFFLINE_POWER">Offline · Power</option><option value="OFFLINE_ISP">Offline · ISP</option>
              <option value="DEGRADED">Degraded</option><option value="UNKNOWN">Unknown</option><option value="ONLINE">Online</option>
            </select>
            <select value={tier} onChange={(e) => setTier(+e.target.value)} aria-label="Tier">
              <option value={0}>All tiers</option><option value={1}>Tier 1</option><option value={2}>Tier 2</option><option value={3}>Tier 3</option>
            </select>
          </div>
          {!tiles.data ? <Loading error={tiles.error} /> : list.length === 0 ? <div className="empty">No branches match.</div> : (
            <div className="tiles">
              {list.map((t) => (
                <div key={t.branchId} className={`tile ${t.status}`} onClick={() => nav(`/branches/${t.branchId}`)} title={t.reason}>
                  <div className="top"><span className="code">{t.code} · T{t.tier}</span>{t.status !== 'ONLINE' && <Pill s={t.status} short />}</div>
                  <div className="name">{t.name}</div>
                  <div className="meta">
                    <span>{t.latencyMs != null ? `${t.latencyMs} ms` : '— ms'}</span>
                    <span className="isp" title={(t.isps ?? []).map((i) => `${i.isp} (${i.role.toLowerCase()}): ${i.up == null ? 'no data' : i.up ? 'up' : 'down'}`).join('\n') || 'No ISP links'}>
                      {(t.isps ?? []).map((i, n) => <b key={n} className={i.up == null ? 'na' : i.up ? '' : 'down'} />)}
                    </span>
                    {t.ups && <span title={`UPS ${t.ups.onBattery ? 'on battery' : 'on mains'}${t.ups.batteryPct != null ? ` · ${t.ups.batteryPct}%` : ''}`}>
                      {t.ups.onBattery ? '🔋' : '⚡'}</span>}
                    <span style={{ marginLeft: 'auto' }}>{t.status !== 'ONLINE' ? fmtDuration(t.statusSinceUtc) : ''}</span>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
        <div className="card">
          <h3>Active Alerts <span className="sub">{alerts.data?.total ?? 0} active · <a onClick={() => nav('/alerts')}>console</a></span></h3>
          {!alerts.data ? <Loading error={alerts.error} /> : alerts.data.items.length === 0 ? <div className="empty">✓ No active alerts</div> : (
            <div className="feed">
              {alerts.data.items.map((a) => (
                <div key={a.alertId} className={`fi ${a.severity}`} onClick={() => nav(a.incidentId ? `/incidents/${a.incidentId}` : `/branches/${a.branchId}`)}>
                  <div className="t">{a.title}</div>
                  <div className="m">
                    <span>{a.branchCode}{a.device ? ` · ${a.device}` : ''} · {fmtTime(a.raisedAt)}</span>
                    {a.ackBy ? <span>✓ {a.ackBy}</span> : a.severity !== 'INFO' && can('ack') &&
                      <button className="btn sm" onClick={(e) => { e.stopPropagation(); setAck([a.alertId]); }}>Ack</button>}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
      {ack && <AckDialog ids={ack} onClose={() => setAck(null)} onDone={alerts.reload} />}
    </>
  );
}
