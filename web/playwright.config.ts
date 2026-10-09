import { defineConfig } from '@playwright/test'

// Expects the API (Development, Demo:Enabled=true) and `npm run dev` to be running; see README.
export default defineConfig({
  testDir: 'e2e',
  timeout: 60_000,
  use: { baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5173', trace: 'retain-on-failure' },
})
