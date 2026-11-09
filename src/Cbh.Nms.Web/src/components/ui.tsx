import { createContext, ReactNode, useCallback, useContext, useEffect, useState } from 'react';
import type { BranchStatus, Me, Sev } from '../api';

// Status is never colour-only (FR-DB-02, NFR-13): every pill carries an icon and text.
export const STATUS_LABEL: Record<BranchStatus, string> = {
  ONLINE: '● Online',
  DEGRADED: '▲ Degraded',
  OFFLINE_ISP: '✕ Offline · ISP',
  OFFLINE_POWER: '⚡ Offline · Power',
  UNKNOWN: '? Unknown',
};
export const STATUS_ORDER: Record<BranchStatus, number> = { OFFLINE_POWER: 0, OFFLINE_ISP: 1, DEGRADED: 2, UNKNOWN: 3, ONLINE: 4 };
export const STATUS_COLOR: Record<string, string> = {
  ONLINE: 'var(--ok)', DEGRADED: 'var(--warn)', OFFLINE_ISP: 'var(--crit)', OFFLINE_POWER: 'var(--pwr)', UNKNOWN: '#c9ced6',
};

export const Pill = ({ s, short }: { s: BranchStatus; short?: boolean }) => (
  <span className={`pill s-${s}`}>{short ? STATUS_LABEL[s].split(' ·')[0] : STATUS_LABEL[s]}</span>
);
export const SevTag = ({ s }: { s: Sev | string }) => <span className={`sev ${s}`}>{s}</span>;
export const StTag = ({ s }: { s: string }) => <span className={`st ${s}`}>{s.replace('_', ' ')}</span>;

const TZ = 'Asia/Dhaka';   // NFR-14: UTC in the database, Dhaka time on screen
export function fmtTime(iso?: string | null, withDate = false) {
  if (!iso) return '—';
  const d = new Date(iso);
  const today = new Date().toLocaleDateString('en-GB', { timeZone: TZ }) === d.toLocaleDateString('en-GB', { timeZone: TZ });
  return d.toLocaleString('en-GB', {
    timeZone: TZ, hour: '2-digit', minute: '2-digit', hour12: false,
    ...(withDate || !today ? { day: '2-digit', month: 'short' } : {}),
  });
}
export function fmtDuration(fromIso?: string | null, toIso?: string | null) {
  if (!fromIso) return '—';
  const ms = (toIso ? new Date(toIso) : new Date()).getTime() - new Date(fromIso).getTime();
  return fmtSeconds(Math.max(0, ms / 1000));
}
export function fmtSeconds(s?: number | null) {
  if (s === null || s === undefined) return '—';
  const m = Math.floor(s / 60);
  if (m < 1) return `${Math.round(s)} s`;
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  if (h < 48) return `${h} h ${String(m % 60).padStart(2, '0')} min`;
  return `${Math.floor(h / 24)} d ${h % 24} h`;
}

// ───────── session ─────────
export const MeContext = createContext<Me | null>(null);
export const useMe = () => useContext(MeContext)!;
export const useCan = () => {
  const me = useMe();
  return (perm: string) => me.permissions.includes(perm);
};

// ───────── toast ─────────
type Toast = { msg: string; error?: boolean } | null;
const ToastContext = createContext<(msg: string, error?: boolean) => void>(() => {});
export const useToast = () => useContext(ToastContext);
export function ToastProvider({ children }: { children: ReactNode }) {
  const [t, setT] = useState<Toast>(null);
  const show = useCallback((msg: string, error?: boolean) => setT({ msg, error }), []);
  useEffect(() => {
    if (!t) return;
    const h = setTimeout(() => setT(null), t.error ? 6000 : 2500);
    return () => clearTimeout(h);
  }, [t]);
  return (
    <ToastContext.Provider value={show}>
      {children}
      {t && <div className={`toast ${t.error ? 'error' : ''}`} role="status">{t.msg}</div>}
    </ToastContext.Provider>
  );
}

export function Modal({ title, children, onClose, footer }: { title: string; children: ReactNode; onClose: () => void; footer?: ReactNode }) {
  useEffect(() => {
    const k = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, [onClose]);
  return (
    <div className="modal-bg" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-label={title}>
        <h3>{title}</h3>
        {children}
        {footer && <div className="foot">{footer}</div>}
      </div>
    </div>
  );
}

/** Loads data and reloads on demand or on an interval. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[], intervalMs?: number) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((x) => x + 1), []);
  useEffect(() => {
    let live = true;
    load().then((d) => { if (live) { setData(d); setError(null); } }).catch((e) => live && setError(e.message));
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);
  useEffect(() => {
    if (!intervalMs) return;
    const h = setInterval(reload, intervalMs);
    return () => clearInterval(h);
  }, [intervalMs, reload]);
  return { data, error, reload, setData };
}

export const Loading = ({ error }: { error?: string | null }) =>
  error ? <div className="err">{error}</div> : <div className="empty">Loading…</div>;
