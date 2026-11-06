import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.E2E_BASE_URL || 'http://127.0.0.1:5173';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  reporter: 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
    ...devices['Desktop Chrome'],
  },
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1',
    url: 'http://127.0.0.1:5173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
