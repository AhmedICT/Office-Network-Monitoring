import { useState } from 'react';
import { api } from '../api';
import { Modal, useToast } from './ui';

const local = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);

/** SCR-08 form: target, time, reason, change request. > 24 h needs Head of IT approval. */
export default function MaintenanceDialog({ branchId, branchName, devices, onClose, onDone }: {
  branchId: number; branchName: string; devices: { deviceId: number; hostName: string }[]; onClose: () => void; onDone?: () => void;
}) {
  const now = new Date();
  const [deviceId, setDeviceId] = useState(0);
  const [start, setStart] = useState(local(now));
  const [end, setEnd] = useState(local(new Date(now.getTime() + 2 * 3600e3)));
  const [reason, setReason] = useState('');
  const [cr, setCr] = useState('');
  const [busy, setBusy] = useState(false);
  const toast = useToast();
  const hours = (new Date(end).getTime() - new Date(start).getTime()) / 3600e3;

  const submit = async () => {
    setBusy(true);
    try {
      const r = await api.post<{ needsApproval: boolean }>('/maintenance', {
        branchId: deviceId ? null : branchId, deviceId: deviceId || null,
        startsAt: new Date(start).toISOString(), endsAt: new Date(end).toISOString(), reason, changeRequestNo: cr || null,
      });
      toast(r.needsApproval ? 'Maintenance window saved — waiting for Head of IT approval (> 24 h)' : 'Maintenance window scheduled — alerts suppressed, SLA excluded');
      onDone?.();
      onClose();
    } catch (e) {
      toast((e as Error).message, true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={`Maintenance · ${branchName}`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button>
        <button className="btn primary" disabled={busy || !reason.trim() || hours <= 0} onClick={submit}>Schedule</button></>}>
      <div className="form">
        <label className="field full">Target
          <select value={deviceId} onChange={(e) => setDeviceId(+e.target.value)}>
            <option value={0}>Whole branch</option>
            {devices.map((d) => <option key={d.deviceId} value={d.deviceId}>Device: {d.hostName}</option>)}
          </select></label>
        <label className="field">Start<input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} /></label>
        <label className="field">End<input type="datetime-local" value={end} onChange={(e) => setEnd(e.target.value)} /></label>
        <label className="field full">Reason<input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. FortiGate firmware upgrade" /></label>
        <label className="field">Change Request no.<input value={cr} onChange={(e) => setCr(e.target.value)} placeholder="CR-2026-0142" /></label>
        <div className="small muted" style={{ alignSelf: 'end' }}>
          {hours > 24 ? <span className="err">Longer than 24 h — needs Head of IT approval.</span> : hours > 0 ? `${hours.toFixed(1)} h` : <span className="err">End must be after start</span>}
        </div>
      </div>
    </Modal>
  );
}
