// Thin client for /api/v1. Same-origin cookie session; errors surface RFC 7807 details.

export class ApiError extends Error {
  constructor(public status: number, message: string, public errors?: Record<string, string[]>) {
    super(message);
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const init: RequestInit = { method, credentials: 'same-origin', headers: {} };
  if (body instanceof FormData) init.body = body;
  else if (body !== undefined) {
    init.body = JSON.stringify(body);
    (init.headers as Record<string, string>)['Content-Type'] = 'application/json';
  }
  const res = await fetch('/api/v1' + path, init);
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!res.ok) {
    const errors: Record<string, string[]> | undefined = data?.errors;
    const detail = errors ? Object.values(errors).flat().join(' · ') : data?.detail ?? data?.title ?? res.statusText;
    throw new ApiError(res.status, detail, errors);
  }
  return data as T;
}

export const api = {
  get: <T>(p: string) => request<T>('GET', p),
  post: <T>(p: string, b?: unknown) => request<T>('POST', p, b ?? {}),
  put: <T>(p: string, b: unknown) => request<T>('PUT', p, b),
  patch: <T>(p: string, b: unknown) => request<T>('PATCH', p, b),
  del: <T>(p: string) => request<T>('DELETE', p),
  upload: <T>(p: string, f: FormData) => request<T>('POST', p, f),
};

export function qs(params: Record<string, string | number | boolean | undefined | null>) {
  const s = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== null && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`)
    .join('&');
  return s ? '?' + s : '';
}

// ───────── types (mirror the Core's JSON) ─────────

export type BranchStatus = 'ONLINE' | 'DEGRADED' | 'OFFLINE_ISP' | 'OFFLINE_POWER' | 'UNKNOWN';
export type Sev = 'CRITICAL' | 'HIGH' | 'WARNING' | 'INFO';

export interface Me {
  userId: number; userName: string; displayName: string; role: string; email?: string; mobile?: string;
  scopedBranchIds: number[] | null; permissions: string[];
}

export interface Summary {
  totalBranches: number; online: number; degraded: number; offlineIsp: number; offlinePower: number; unknown: number;
  availabilityTodayPct: number; openIncidents: number; unackedIncidents: number; activeAlerts: number; unackedCritical: number;
}

export interface Tile {
  branchId: number; code: string; name: string; region: string; tier: number; status: BranchStatus;
  statusSinceUtc?: string; reason?: string; latencyMs?: number;
  isps?: { role: string; isp: string; up: number | null }[]; ups?: { onBattery: boolean; batteryPct?: number };
  openIncidentNo?: string;
}

export interface AlertRow {
  alertId: number; branchId: number; branchCode: string; branchName: string; device?: string; severity: Sev; title: string;
  lastValue?: number; raisedAt: string; clearedAt?: string; isSuppressed: boolean; suppressReason?: string;
  ackBy?: string; ackAt?: string; ackComment?: string; incidentId?: number; incidentNo?: string;
}

export interface IncidentRow {
  incidentId: number; incidentNo: string; branchId: number; branchCode: string; branchName: string; severity: Sev; status: string;
  title: string; assignee?: string; assignedUserId?: number; detectedAt: string; ackAt?: string; resolvedAt?: string;
  escalationLevel: number; taskTicketId?: string; ticketStatus?: string; rootCause?: string;
}

export interface Paged<T> { items: T[]; page: number; pageSize: number; total: number; }

export interface Region { regionId: number; name: string; }
export interface ReportTable { title: string; subtitle: string; columns: string[]; rows: (string | number | null)[][]; flagged: number[]; }
