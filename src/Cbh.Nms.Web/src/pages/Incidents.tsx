import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { api, IncidentRow, Paged, qs, Sev } from '../api';
import { fmtDuration, fmtSeconds, fmtTime, Loading, SevTag, StTag, useCan, useLoad, useMe, useToast } from '../components/ui';
import { useLive } from '../live';

interface IncidentDetail {
  incidentId: number; incidentNo: string; branchId: number; branchCode: string; branchName: string; severity: Sev; status: string;
  title: string; rootCause?: string; resolutionNote?: string; assignedUserId?: number; assignee?: string; escalationLevel: number;
  taskTicketId?: string; ticketStatus?: string; startedAt: string; detectedAt: string; ackAt?: string; resolvedAt?: string; closedAt?: string;
  metrics: { mttdSeconds: number; mttaSeconds?: number; mttrSeconds?: number };
  alerts: { alertId: number; severity: Sev; title: string; raisedAt: string; clearedAt?: string; isSuppressed: boolean; suppressReason?: string }[];
  timeline: { at: string; kind: string; text: string; by: string }[];
}

const ROOT_CAUSES = ['ISP', 'POWER', 'HARDWARE', 'CONFIG', 'HIS', 'UNKNOWN'];
const NEXT: Record<string, string[]> = {
  OPEN: ['ACK', 'IN_PROGRESS', 'RESOLVED'], ACK: ['IN_PROGRESS', 'RESOLVED'], IN_PROGRESS: ['RESOLVED'],
  RESOLVED: ['CLOSED', 'IN_PROGRESS'], CLOSED: [],
};

/** SCR-06: list + detail with assign, status, notes, resolve (root cause required) and close. */
export default function Incidents() {
  const routeId = useParams().id;
  const nav = useNavigate();
  const [filter, setFilter] = useState<'open' | 'mine' | 'resolved' | 'all'>('open');
  const list = useLoad(() => api.get<Paged<IncidentRow>>(`/incidents${qs({
    status: filter === 'mine' ? 'all' : filter, mine: filter === 'mine' || undefined, pageSize: 200,
  })}`), [filter]);
  useLive(useCallback((e: string) => { if (e === 'incidentUpdated' || e === 'reconnected') list.reload(); }, [list.reload]));

  const selected = routeId ? +routeId : list.data?.items[0]?.incidentId;
  return (
    <div className="inc-layout">
      <div className="card">
        <h3>Incidents <span className="sub">synced with Task Management</span></h3>
        <div className="filters">
          {(['open', 'mine', 'resolved', 'all'] as const).map((f) => (
            <span key={f} className={`chip ${filter === f ? 'on' : ''}`} onClick={() => setFilter(f)}>
              {{ open: 'Open', mine: 'My incidents', resolved: 'Resolved', all: 'All' }[f]}</span>
          ))}
        </div>
        {!list.data ? <Loading error={list.error} /> : list.data.items.length === 0 ? <div className="empty">No incidents.</div> : (
          <div className="tablewrap"><table>
            <thead><tr><th>No.</th><th>Branch</th><th>Severity</th><th>Status</th><th>Assignee</th><th>Opened</th><th>Duration</th><th>Esc.</th><th>Ticket</th></tr></thead>
            <tbody>{list.data.items.map((i) => (
              <tr key={i.incidentId} className={`click ${i.incidentId === selected ? 'sel' : ''}`} onClick={() => nav(`/incidents/${i.incidentId}`)}>
                <td className="nowrap"><b>{i.incidentNo}</b></td><td>{i.branchCode} · {i.branchName}</td><td><SevTag s={i.severity} /></td>
                <td><StTag s={i.status} /></td><td>{i.assignee ?? '—'}</td><td className="nowrap">{fmtTime(i.detectedAt)}</td>
                <td className="nowrap">{fmtDuration(i.detectedAt, i.resolvedAt)}</td><td>{i.escalationLevel ? `L${i.escalationLevel}` : '—'}</td>
                <td>{i.taskTicketId ?? (i.ticketStatus === 'FAILED' ? <span className="err">failed</span> : '—')}</td>
              </tr>
            ))}</tbody>
          </table></div>
        )}
      </div>
      <div className="card">{selected ? <Detail key={selected} id={selected} onChanged={list.reload} /> : <div className="empty">Select an incident.</div>}</div>
    </div>
  );
}

function Detail({ id, onChanged }: { id: number; onChanged: () => void }) {
  const can = useCan();
  const me = useMe();
  const toast = useToast();
  const nav = useNavigate();
  const inc = useLoad(() => api.get<IncidentDetail>(`/incidents/${id}`), [id]);
  const users = useLoad(() => can('incident.update') ? api.get<{ userId: number; displayName: string }[]>('/users/assignable') : Promise.resolve([]), []);
  const [note, setNote] = useState('');
  const [resolve, setResolve] = useState(false);
  const [rootCause, setRootCause] = useState('');
  const [resolution, setResolution] = useState('');
  useLive(useCallback((e: string, p: unknown) => {
    if ((e === 'incidentUpdated' && (p as { incidentId?: number })?.incidentId === id) || e === 'alertCleared') inc.reload();
  }, [id, inc.reload]));
  useEffect(() => { setRootCause(inc.data?.rootCause ?? ''); setResolution(inc.data?.resolutionNote ?? ''); }, [inc.data]);

  const i = inc.data;
  if (!i) return <Loading error={inc.error} />;
  const edit = can('incident.update') && i.status !== 'CLOSED';

  const patch = async (body: object, msg: string) => {
    try { await api.patch(`/incidents/${id}`, body); toast(msg); inc.reload(); onChanged(); setResolve(false); }
    catch (e) { toast((e as Error).message, true); }
  };
  const addNote = async () => {
    try { await api.post(`/incidents/${id}/notes`, { text: note }); setNote(''); inc.reload(); }
    catch (e) { toast((e as Error).message, true); }
  };

  return (
    <>
      <h3>{i.incidentNo} <SevTag s={i.severity} /> <StTag s={i.status} />
        <span className="sub">{i.taskTicketId ? `Ticket ${i.taskTicketId}` : i.ticketStatus === 'FAILED' ? 'Ticket sync failed' : 'No ticket'}</span></h3>
      <div style={{ fontWeight: 600, fontSize: 15 }}>{i.title}</div>
      <div className="muted small" style={{ marginTop: 4 }}>
        <a onClick={() => nav(`/branches/${i.branchId}`)}>{i.branchName} ({i.branchCode})</a> · Detected {fmtTime(i.detectedAt, true)} ·
        Escalation {i.escalationLevel ? `level ${i.escalationLevel}` : 'none'} · Assignee {i.assignee ?? '—'}
      </div>
      <div className="metrics3">
        <div><b>{fmtSeconds(i.metrics.mttdSeconds)}</b><small>MTTD</small></div>
        <div><b>{fmtSeconds(i.metrics.mttaSeconds)}</b><small>MTTA</small></div>
        <div><b>{i.metrics.mttrSeconds != null ? fmtSeconds(i.metrics.mttrSeconds) : fmtDuration(i.detectedAt)}</b><small>{i.metrics.mttrSeconds != null ? 'MTTR' : 'Open for'}</small></div>
      </div>

      {edit && (
        <div className="filters">
          <select value="" onChange={(e) => {
            const s = e.target.value;
            if (s === 'RESOLVED') setResolve(true); else if (s) patch({ status: s }, `Status → ${s.replace('_', ' ')}`);
          }} aria-label="Change status">
            <option value="">Change status…</option>
            {NEXT[i.status].filter((s) => s !== 'CLOSED' || can('incident.close')).map((s) => <option key={s} value={s}>{s.replace('_', ' ')}</option>)}
          </select>
          <select value={i.assignedUserId ?? ''} onChange={(e) => e.target.value && patch({ assignedUserId: +e.target.value }, 'Assigned')} aria-label="Assign">
            <option value="">Assign engineer…</option>
            {(users.data ?? []).map((u) => <option key={u.userId} value={u.userId}>{u.displayName}</option>)}
          </select>
          {i.assignedUserId !== me.userId && <button className="btn sm" onClick={() => patch({ assignedUserId: me.userId, status: i.status === 'OPEN' ? 'ACK' : undefined }, 'Assigned to you')}>Take it</button>}
        </div>
      )}
      {resolve && (
        <div className="card" style={{ background: 'var(--bg)', marginBottom: 12 }}>
          <div className="form">
            <label className="field">Root cause (required)
              <select value={rootCause} onChange={(e) => setRootCause(e.target.value)}><option value="">Choose…</option>{ROOT_CAUSES.map((r) => <option key={r}>{r}</option>)}</select></label>
            <div />
            <label className="field full">Resolution (required)<textarea value={resolution} onChange={(e) => setResolution(e.target.value)} placeholder="What was wrong and what fixed it" /></label>
          </div>
          <div className="row" style={{ marginTop: 8 }}>
            <button className="btn" onClick={() => setResolve(false)}>Cancel</button>
            <button className="btn primary right" disabled={!rootCause || !resolution.trim()}
              onClick={() => patch({ status: 'RESOLVED', rootCause, resolutionNote: resolution }, 'Incident resolved')}>Resolve</button>
          </div>
        </div>
      )}
      {i.rootCause && <div className="small"><b>Root cause:</b> {i.rootCause}{i.resolutionNote && <> — {i.resolutionNote}</>}</div>}

      <h3 style={{ marginTop: 12 }}>Linked alerts ({i.alerts.length})</h3>
      {i.alerts.map((a) => (
        <div key={a.alertId} className={`fi ${a.severity}`} style={{ marginBottom: 6, cursor: 'default' }}>
          <div className="t">{a.title}</div>
          <div className="m"><span>{fmtTime(a.raisedAt)} → {a.clearedAt ? fmtTime(a.clearedAt) : 'active'}</span>{a.isSuppressed && <span>suppressed ({a.suppressReason?.toLowerCase()})</span>}</div>
        </div>
      ))}

      <h3 style={{ marginTop: 14 }}>Timeline</h3>
      <div className="timeline">
        {i.timeline.map((t, n) => <div key={n} className={`ev ${t.kind}`}>{t.text}<small>{fmtTime(t.at, true)} · {t.by}</small></div>)}
      </div>
      {edit && <>
        <textarea placeholder="Add a note…" value={note} onChange={(e) => setNote(e.target.value)} />
        <div className="row" style={{ marginTop: 8 }}>
          <button className="btn" disabled={!note.trim()} onClick={addNote}>Add note</button>
          {i.status !== 'RESOLVED' && <button className="btn primary right" onClick={() => setResolve(true)}>Resolve…</button>}
        </div>
      </>}
    </>
  );
}
