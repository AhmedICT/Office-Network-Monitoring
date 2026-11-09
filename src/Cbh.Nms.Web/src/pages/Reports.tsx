import { useMemo, useState } from 'react';
import ReactApexChart from 'react-apexcharts';
import { api, qs, Region, ReportTable } from '../api';
import { Loading, useLoad } from '../components/ui';

const KINDS = [
  ['availability', 'Availability'], ['incidents', 'Incidents'], ['isp', 'ISP comparison'], ['power', 'Power'],
] as const;

function months() {
  const out: string[] = [];
  const d = new Date();
  for (let i = 0; i < 13; i++) {
    const m = new Date(d.getFullYear(), d.getMonth() - i, 1);
    out.push(`${m.getFullYear()}-${String(m.getMonth() + 1).padStart(2, '0')}`);
  }
  return out;
}

/** SCR-07: preview table + chart, PDF/Excel export (FR-RP-01..05). */
export default function Reports() {
  const [kind, setKind] = useState<(typeof KINDS)[number][0]>('availability');
  const [month, setMonth] = useState(months()[0]);
  const [regionId, setRegionId] = useState('');
  const regions = useLoad(() => api.get<Region[]>('/regions'), []);
  const params = { month, regionId: kind === 'isp' ? undefined : regionId || undefined };
  const report = useLoad(() => api.get<ReportTable>(`/reports/${kind}${qs(params)}`), [kind, month, regionId]);

  const chart = useMemo(() => {
    const t = report.data;
    if (!t || t.rows.length === 0) return null;
    if (kind === 'availability') {
      const rows = [...t.rows].sort((a, b) => (a[3] as number) - (b[3] as number)).slice(0, 35);
      return { categories: rows.map((r) => r[0] as string), data: rows.map((r) => r[3] as number), label: 'Availability %', min: Math.min(98, ...rows.map((r) => r[3] as number)) };
    }
    if (kind === 'isp') return { categories: t.rows.map((r) => r[0] as string), data: t.rows.map((r) => (r[3] as number) ?? 0), label: 'Avg latency ms', min: 0 };
    if (kind === 'power') return { categories: t.rows.map((r) => r[0] as string), data: t.rows.map((r) => r[4] as number), label: 'Power outages', min: 0 };
    return null;
  }, [report.data, kind]);

  const href = (format: string) => `/api/v1/reports/${kind}${qs({ ...params, format })}`;
  return (
    <div className="card">
      <h3>{report.data?.title ?? 'Reports'}
        <span className="sub">
          <select value={kind} onChange={(e) => setKind(e.target.value as typeof kind)} aria-label="Report">{KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
          <select value={month} onChange={(e) => setMonth(e.target.value)} aria-label="Month">{months().map((m) => <option key={m}>{m}</option>)}</select>
          {kind !== 'isp' && <select value={regionId} onChange={(e) => setRegionId(e.target.value)} aria-label="Region">
            <option value="">All regions</option>{(regions.data ?? []).map((r) => <option key={r.regionId} value={r.regionId}>{r.name}</option>)}</select>}
          <a className="btn sm" href={href('pdf')}>PDF</a>
          <a className="btn sm" href={href('xlsx')}>Excel</a>
        </span>
      </h3>
      {!report.data ? <Loading error={report.error} /> : <>
        <div className="muted small" style={{ marginBottom: 10 }}>{report.data.subtitle}</div>
        {chart && (
          <ReactApexChart type="bar" height={240} series={[{ name: chart.label, data: chart.data }]}
            options={{
              chart: { toolbar: { show: false }, fontFamily: 'inherit', animations: { enabled: false } },
              xaxis: { categories: chart.categories }, yaxis: { min: chart.min, labels: { formatter: (v) => v.toFixed(kind === 'availability' ? 1 : 0) } },
              colors: [kind === 'availability' ? '#1f8a4c' : '#0b5cad'], dataLabels: { enabled: false }, grid: { borderColor: '#eef1f5' },
              annotations: kind === 'availability' ? { yaxis: [{ y: 99.5, borderColor: '#c62828', strokeDashArray: 4, label: { text: 'target 99.5%' } }] } : undefined,
            }} />
        )}
        {report.data.rows.length === 0 ? <div className="empty">No data for this period.</div> : (
          <div className="tablewrap"><table>
            <thead><tr>{report.data.columns.map((c) => <th key={c}>{c}</th>)}</tr></thead>
            <tbody>{report.data.rows.map((r, n) => (
              <tr key={n} className={report.data!.flagged.includes(n) ? 'flag' : ''}>
                {r.map((v, c) => <td key={c} className={typeof v === 'number' ? 'num' : ''}>{v ?? '—'}{report.data!.flagged.includes(n) && c === 5 ? ' ✕' : ''}</td>)}
              </tr>
            ))}</tbody>
          </table></div>
        )}
      </>}
    </div>
  );
}
