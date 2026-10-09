import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

// examples/ (repo root) holds the shared workout schema. Same-origin in dev: /api is proxied to the ASP.NET Core API (no CORS needed).
export default defineConfig({
  plugins: [react()],
  server: { port: 5173, fs: { allow: ['..'] }, proxy: { '/api': 'http://localhost:5080', '/health': 'http://localhost:5080' } },
  test: { include: ['src/**/*.test.ts'] },
})
