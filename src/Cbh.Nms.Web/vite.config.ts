import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Production: built into the Core's wwwroot so one IIS site serves UI, API and SignalR (same origin, cookie auth).
// Development: `npm run dev` proxies to a locally running Core.
const core = process.env.CBH_CORE_URL ?? 'http://localhost:5219';

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../Cbh.Nms.Core/wwwroot', emptyOutDir: true, chunkSizeWarningLimit: 1200 },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: core, changeOrigin: false },
      '/hubs': { target: core, ws: true },
      '/health': { target: core },
    },
  },
});
