import ReactApexChart from 'react-apexcharts';
import type { ApexOptions } from 'apexcharts';

export interface Series { name: string; data: { x: number; y: number | null }[] }

/** Time-series line chart with an optional dashed threshold line (ApexCharts per SRS §3.1). */
export default function TimeChart({ series, unit, threshold, height = 220 }: { series: Series[]; unit: string; threshold?: number; height?: number }) {
  const options: ApexOptions = {
    chart: { type: 'line', toolbar: { show: false }, zoom: { enabled: false }, animations: { enabled: false }, fontFamily: 'inherit' },
    stroke: { width: 2, curve: 'straight' },
    colors: ['#0b5cad', '#1f8a4c', '#b26b00', '#6a3fb5'],
    xaxis: { type: 'datetime', labels: { datetimeUTC: false, style: { colors: '#5f6b7a' } } },
    yaxis: { min: 0, labels: { formatter: (v) => `${Math.round(v)}`, style: { colors: '#5f6b7a' } }, title: { text: unit } },
    grid: { borderColor: '#eef1f5' },
    legend: { show: series.length > 1, position: 'top', horizontalAlign: 'left' },
    tooltip: { x: { format: 'dd MMM HH:mm' }, y: { formatter: (v) => (v == null ? '—' : `${v.toFixed(1)} ${unit}`) } },
    annotations: threshold != null ? {
      yaxis: [{ y: threshold, borderColor: '#c62828', strokeDashArray: 4, label: { text: `threshold ${threshold} ${unit}`, style: { color: '#fff', background: '#c62828' } } }],
    } : undefined,
    noData: { text: 'No data yet' },
  };
  return <ReactApexChart type="line" height={height} series={series} options={options} />;
}
