import { defineConfig } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';

const port = 47711;
const data = path.join(os.tmpdir(), `clipfx-e2e-${Date.now()}`);
const dotnet = process.env.DOTNET || 'dotnet';

export default defineConfig({
  testDir: '.',
  timeout: 30000,
  globalTimeout: 6 * 60 * 1000,
  workers: 1,
  fullyParallel: false,
  reporter: [['list']],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    viewport: { width: 1180, height: 760 },
    launchOptions: process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {},
  },
  webServer: {
    command: `"${dotnet}" run --project ../../tools/ClipFx.DevHost -- --quiet --port ${port} --data "${data}"`,
    url: `http://127.0.0.1:${port}/api/snapshot`,
    timeout: 180000,
    reuseExistingServer: false,
  },
});
