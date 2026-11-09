import { useState } from 'react';
import { api } from '../api';
import { Modal, useToast } from './ui';

/** SCR-05: acknowledging needs a comment; it stops repeat notifications (FR-AL-08). */
export default function AckDialog({ ids, onClose, onDone }: { ids: number[]; onClose: () => void; onDone: () => void }) {
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const toast = useToast();
  const submit = async () => {
    setBusy(true);
    try {
      await api.post('/alerts/ack', { ids, comment });
      toast(`${ids.length} alert(s) acknowledged — repeat notifications stopped`);
      onDone();
      onClose();
    } catch (e) {
      toast((e as Error).message, true);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal title={`Acknowledge ${ids.length} alert${ids.length > 1 ? 's' : ''}`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button>
        <button className="btn primary" disabled={busy || !comment.trim()} onClick={submit}>Acknowledge</button></>}>
      <label className="field">Comment (required)
        <textarea autoFocus value={comment} onChange={(e) => setComment(e.target.value)} placeholder="e.g. Called ISP, ticket raised; checking UPS on site" />
      </label>
    </Modal>
  );
}
