import { useState } from 'react';
import { api, Tile } from '../api';
import MaintenanceDialog from '../components/MaintenanceDialog';
import { fmtDuration, fmtTime, Loading, useCan, useLoad, useToast } from '../components/ui';

interface Window {
  maintenanceId: number; branchId: number; branchCode: string; branchName: string; deviceId?: number; device?: string;
  startsAt: string; endsAt: string; reason: string; changeRequestNo?: string; createdBy: string; approvedBy?: string;
  needsApproval: boolean; isActive: boolean;
}

/** SCR-08: planned maintenance suppresses alerts and is excluded from SLA (FR-AL-05). */
export default function Maintenance() {
  const can = useCan();
  const toast = useToast();
  const [past, setPast] = useState(false);
  const [branch, setBranch] = useState<Tile | null>(null);
  const [pick, setPick] = useState(false);
  const list = useLoad(() => api.get<{ items: Window[] }>(`/maintenance${past ? '?includePast=true' : ''}`), [past]);
  const branches = useLoad(() => api.get<{ items: Tile[] }>('/dashboard/branches'), []);
  const devices = useLoad(() => branch ? api.get<{ items: { deviceId: number; hostName: string }[] }>(`/devices?branchId=${branch.branchId}`) : Promise.resolve({ items: [] }), [branch]);

  const act = async (fn: () => Promise<unknown>, msg: string) => {
    try { await fn(); toast(msg); list.reload(); } catch (e) { toast((e as Error).message, true); }
  };

  return (
    <div className="card">
      <h3>Maintenance windows
        <span className="sub">
          <label className="chip"><input type="checkbox" checked={past} onChange={(e) => setPast(e.target.checked)} /> Include past</label>
          {can('maintenance') && <button className="btn primary sm" onClick={() => setPick(true)}>+ New window</button>}
        </span></h3>
      {pick && (
        <div className="filters">
          <select autoFocus defaultValue="" onChange={(e) => { setBranch(branches.data?.items.find((b) => b.branchId === +e.target.value) ?? null); setPick(false); }}>
            <option value="" disabled>Choose a branch…</option>
            {(branches.data?.items ?? []).map((b) => <option key={b.branchId} value={b.branchId}>{b.code} · {b.name}</option>)}
          </select>
          <button className="btn sm" onClick={() => setPick(false)}>Cancel</button>
        </div>
      )}
      {!list.data ? <Loading error={list.error} /> : list.data.items.length === 0 ? <div className="empty">No upcoming or active maintenance.</div> : (
        <div className="tablewrap"><table>
          <thead><tr><th>Branch / device</th><th>Start</th><th>End</th><th>Length</th><th>Reason</th><th>CR</th><th>Created by</th><th>Approval</th><th></th></tr></thead>
          <tbody>{list.data.items.map((w) => (
            <tr key={w.maintenanceId}>
              <td>{w.branchCode} · {w.device ?? 'whole branch'} {w.isActive && <span className="pill s-UNKNOWN">active</span>}</td>
              <td className="nowrap">{fmtTime(w.startsAt, true)}</td><td className="nowrap">{fmtTime(w.endsAt, true)}</td>
              <td>{fmtDuration(w.startsAt, w.endsAt)}</td><td>{w.reason}</td><td>{w.changeRequestNo ?? '—'}</td><td>{w.createdBy}</td>
              <td>{w.needsApproval ? (can('maintenance.approve')
                ? <button className="btn sm primary" onClick={() => act(() => api.post(`/maintenance/${w.maintenanceId}/approve`), 'Approved')}>Approve</button>
                : <span className="err">awaiting Head of IT</span>) : w.approvedBy ? `✓ ${w.approvedBy}` : '≤ 24 h'}</td>
              <td>{can('maintenance') && new Date(w.endsAt) > new Date() &&
                <button className="btn sm danger" onClick={() => act(() => api.del(`/maintenance/${w.maintenanceId}`), w.isActive ? 'Maintenance ended' : 'Window cancelled')}>
                  {w.isActive ? 'End now' : 'Cancel'}</button>}</td>
            </tr>
          ))}</tbody>
        </table></div>
      )}
      {branch && devices.data && <MaintenanceDialog branchId={branch.branchId} branchName={branch.name} devices={devices.data.items}
        onClose={() => setBranch(null)} onDone={list.reload} />}
    </div>
  );
}
