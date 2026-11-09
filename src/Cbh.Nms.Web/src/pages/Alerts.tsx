import { useCallback, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { AlertRow, api, Paged, qs } from '../api';
import AckDialog from '../components/AckDialog';
import { fmtDuration, fmtTime, Loading, SevTag, useCan, useLoad } from '../components/ui';
import { useLive } from '../live';

/** SCR-05: filter active/recent alerts, bulk Ack with a comment, see suppression reasons. */
export default function Alerts() {
  const [params, setParams] = useSearchParams();
  const nav = useNavigate();
  const can = useCan();
  const status = params.get('status') ?? 'active';
  const severity = params.get('severity') ?? '';
  const suppressed = params.get('suppressed') === 'true';
  const [sel, setSel] = useState<number[]>([]);
  const [ack, setAck] = useState<number[] | null>(null);
  const set = (k: string, v: string) => { const p = new URLSearchParams(params); if (v) p.set(k, v); else p.delete(k); setParams(p); setSel([]); };

  const list = useLoad(() => api.get<Paged<AlertRow>>(`/alerts${qs({ status, severity, suppressed, pageSize: 300 })}`), [status, severity, suppressed]);
  useLive(useCallback((e: string) => { if (e !== 'incidentUpdated' && e !== 'branchStatusChanged') list.reload(); }, [list.reload]));

  const ackable = (list.data?.items ?? []).filter((a) => !a.ackAt && !a.clearedAt && a.severity !== 'INFO');
  return (
    <div className="card">
      <h3>Alert Console <span className="sub">Ack requires a comment · suppressed alerts hidden unless shown</span></h3>
      <div className="filters">
        <span className={`chip ${status === 'active' ? 'on' : ''}`} onClick={() => set('status', '')}>Active</span>
        <span className={`chip ${status === 'recent' ? 'on' : ''}`} onClick={() => set('status', 'recent')}>Last 24 h</span>
        <select value={severity} onChange={(e) => set('severity', e.target.value)} aria-label="Severity">
          <option value="">All severities</option><option value="CRITICAL">Critical</option><option value="HIGH">High</option>
          <option value="WARNING">Warning</option><option value="INFO">Info</option><option value="CRITICAL,HIGH">Critical + High</option>
        </select>
        <label className="chip"><input type="checkbox" checked={suppressed} onChange={(e) => set('suppressed', e.target.checked ? 'true' : '')} /> Show suppressed</label>
        {can('ack') && <button className="btn primary sm right" disabled={sel.length === 0} onClick={() => setAck(sel)}>Acknowledge selected ({sel.length})</button>}
      </div>
      {!list.data ? <Loading error={list.error} /> : list.data.items.length === 0 ? <div className="empty">✓ No alerts match.</div> : (
        <div className="tablewrap"><table>
          <thead><tr>
            {can('ack') && <th><input type="checkbox" aria-label="Select all" checked={sel.length > 0 && sel.length === ackable.length}
              onChange={(e) => setSel(e.target.checked ? ackable.map((a) => a.alertId) : [])} /></th>}
            <th>Raised</th><th>Branch</th><th>Device</th><th>Severity</th><th>Title</th><th>Duration</th><th>Ack</th><th>Incident</th>
          </tr></thead>
          <tbody>{list.data.items.map((a) => (
            <tr key={a.alertId} style={a.clearedAt ? { opacity: .6 } : undefined}>
              {can('ack') && <td><input type="checkbox" aria-label="Select" disabled={!ackable.includes(a)} checked={sel.includes(a.alertId)}
                onChange={(e) => setSel((s) => e.target.checked ? [...s, a.alertId] : s.filter((x) => x !== a.alertId))} /></td>}
              <td className="nowrap">{fmtTime(a.raisedAt)}</td>
              <td className="nowrap"><a onClick={() => nav(`/branches/${a.branchId}`)}>{a.branchCode}</a> · {a.branchName}</td>
              <td>{a.device ?? '—'}</td>
              <td><SevTag s={a.severity} /></td>
              <td>{a.title}{a.isSuppressed && <span className="pill s-UNKNOWN" style={{ marginLeft: 6 }}>suppressed: {a.suppressReason?.toLowerCase().replace('_', ' ')}</span>}
                {a.clearedAt && <span className="pill s-ONLINE" style={{ marginLeft: 6 }}>cleared {fmtTime(a.clearedAt)}</span>}</td>
              <td className="nowrap">{fmtDuration(a.raisedAt, a.clearedAt)}</td>
              <td title={a.ackComment}>{a.ackBy ? `✓ ${a.ackBy}` : a.severity === 'INFO' || a.clearedAt ? '—' : can('ack') ?
                <button className="btn sm" onClick={() => setAck([a.alertId])}>Ack</button> : 'pending'}</td>
              <td>{a.incidentNo ? <a onClick={() => nav(`/incidents/${a.incidentId}`)}>{a.incidentNo}</a> : '—'}</td>
            </tr>
          ))}</tbody>
        </table></div>
      )}
      {ack && <AckDialog ids={ack} onClose={() => setAck(null)} onDone={() => { setSel([]); list.reload(); }} />}
    </div>
  );
}
