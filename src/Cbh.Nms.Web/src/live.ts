import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useEffect, useState } from 'react';

// One SignalR connection per tab (FR-DB-04). The server only puts the connection in groups for
// the user's branch scope, so events here are already filtered.
let conn: HubConnection | null = null;
const listeners = new Set<(event: string, payload: unknown) => void>();
const stateListeners = new Set<(up: boolean) => void>();
const EVENTS = ['branchStatusChanged', 'alertRaised', 'alertCleared', 'incidentUpdated'];

function ensure() {
  if (conn) return conn;
  conn = new HubConnectionBuilder()
    .withUrl('/hubs/status')
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build();
  for (const e of EVENTS) conn.on(e, (p) => listeners.forEach((l) => l(e, p)));
  const notify = () => stateListeners.forEach((l) => l(conn!.state === HubConnectionState.Connected));
  conn.onreconnecting(notify);
  conn.onreconnected(() => { notify(); listeners.forEach((l) => l('reconnected', null)); });
  conn.onclose(() => { notify(); setTimeout(start, 10000); });
  const start = () => conn!.start().then(notify).catch(() => setTimeout(start, 10000));
  start();
  return conn;
}

/** Calls onEvent for every hub event (and 'reconnected', so pages can refetch what they missed). */
export function useLive(onEvent: (event: string, payload: unknown) => void) {
  useEffect(() => {
    ensure();
    listeners.add(onEvent);
    return () => { listeners.delete(onEvent); };
  }, [onEvent]);
}

export function useLiveState() {
  const [up, setUp] = useState(conn?.state === HubConnectionState.Connected);
  useEffect(() => {
    ensure();
    stateListeners.add(setUp);
    return () => { stateListeners.delete(setUp); };
  }, []);
  return up;
}

/** Short chime for new Critical alerts (FR-DB-05, NOC mode). */
export function chime() {
  try {
    const ctx = new AudioContext();
    [880, 660, 880].forEach((f, i) => {
      const o = ctx.createOscillator();
      const g = ctx.createGain();
      o.frequency.value = f;
      g.gain.setValueAtTime(0.15, ctx.currentTime + i * 0.22);
      g.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + i * 0.22 + 0.2);
      o.connect(g).connect(ctx.destination);
      o.start(ctx.currentTime + i * 0.22);
      o.stop(ctx.currentTime + i * 0.22 + 0.21);
    });
  } catch { /* audio blocked until the user interacts */ }
}
