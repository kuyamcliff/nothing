import { test, expect } from '@playwright/test';

const H = { 'X-ClipFx': '1' };
const PID = { chrome: 1001, steam: 1002, update: 2001 };

async function inject(request, pid, bytes, sent = false) {
  const r = await request.post('/api/dev/traffic', { headers: H, data: { pid, bytes, sent } });
  expect(r.ok()).toBeTruthy();
}
async function snapshot(request) { return (await request.get('/api/snapshot')).json(); }
async function appByName(request, name) { return (await snapshot(request)).apps.find((a) => a.name === name); }
const row = (page, name) => page.locator('.row', { has: page.locator('.app-name', { hasText: new RegExp(`^${name}$`) }) });

test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ request }) => {
  // Return to a known state between tests.
  await request.post('/api/overall/limit', { headers: H, data: { bytes: null } });
  for (const a of (await snapshot(request)).apps) {
    await request.post(`/api/apps/${a.id}/blocked`, { headers: H, data: { blocked: false } });
    await request.post(`/api/apps/${a.id}/limit`, { headers: H, data: { bytes: null } });
  }
  await request.post('/api/usage/reset', { headers: H });
  await request.post('/api/settings', { headers: H, data: { theme: 'system', showSystem: false, resetPeriod: 'monthly', resetDay: 1 } });
  await inject(request, PID.chrome, 3 * 1024 * 1024);
  await inject(request, PID.steam, 1024 * 1024);
  await inject(request, PID.update, 5 * 1024 * 1024);
});

test('shows a loading state, then live data', async ({ page }) => {
  let release;
  const gate = new Promise((r) => (release = r));
  await page.route('**/api/stream', async (route) => { await gate; await route.continue(); });
  await page.goto('/');
  await expect(page.locator('.row.skeleton').first()).toBeVisible();
  await expect(page.locator('#app')).toHaveAttribute('aria-busy', 'true');
  await expect(page.locator('.banner', { hasText: 'Connecting' })).toBeVisible();
  release();
  await expect(row(page, 'Google Chrome')).toBeVisible();
  await expect(page.locator('.row.skeleton')).toHaveCount(0);
  await expect(page.locator('#app')).not.toHaveAttribute('aria-busy', 'true');
  await expect(page.locator('.banner')).toHaveCount(0);
  await expect(page.locator('#usedValue')).toHaveText('9.00 MB'); // includes the hidden system item
});

test('updates live without reloading', async ({ page, request }) => {
  await page.goto('/');
  await expect(row(page, 'Google Chrome').locator('.cell-used')).toHaveText('3.00 MB');
  await inject(request, PID.chrome, 1024 * 1024);
  await expect(row(page, 'Google Chrome').locator('.cell-used')).toHaveText('4.00 MB');
  await expect(row(page, 'Google Chrome').locator('.dot')).toHaveClass(/on/);
});

test('hides system items until asked, and remembers the choice', async ({ page }) => {
  await page.goto('/');
  await expect(row(page, 'Google Chrome')).toBeVisible();
  await expect(row(page, 'Windows Update')).toHaveCount(0);
  await page.getByText('Show system').click();
  await expect(row(page, 'Windows Update')).toBeVisible();
  await expect(row(page, 'Windows Update').locator('.tag-system')).toBeVisible();
  await page.reload();
  await expect(row(page, 'Windows Update')).toBeVisible();
  await expect(page.locator('#showSystem')).toHaveAttribute('aria-checked', 'true');
});

test('switching an app off blocks all its data', async ({ page, request }) => {
  await page.goto('/');
  const r = row(page, 'Google Chrome');
  const sw = r.getByRole('switch');
  await expect(sw).toHaveAttribute('aria-checked', 'true');
  await sw.click();
  await expect(sw).toHaveAttribute('aria-checked', 'false');
  await expect(sw).toHaveAttribute('aria-busy', 'false');
  await expect(r.locator('.tag-blocked')).toHaveText('Blocked');
  const chrome = await appByName(request, 'Google Chrome');
  expect(chrome.blocked && chrome.applied).toBeTruthy();

  await inject(request, PID.chrome, 50 * 1024 * 1024);
  await page.waitForTimeout(1200);
  await expect(r.locator('.cell-used')).toHaveText('3.00 MB');

  await sw.click();
  await expect(sw).toHaveAttribute('aria-checked', 'true');
  await expect(r.locator('.tag-blocked')).toHaveCount(0);
  await inject(request, PID.chrome, 1024 * 1024);
  await expect(r.locator('.cell-used')).toHaveText('4.00 MB');
});

test('an app limit stops the app when reached', async ({ page, request }) => {
  await page.goto('/');
  const r = row(page, 'Steam');
  await r.hover();
  await r.getByRole('button', { name: 'Set limit' }).click();
  const dialog = page.locator('#appDialog');
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('#adName')).toHaveText('Steam');
  await expect(page.locator('#adLimit')).toBeFocused();
  await page.locator('#adLimit').fill('2');
  await dialog.getByRole('radio', { name: 'MB' }).click();
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.toast', { hasText: 'Limit saved' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(r.locator('.limit-btn .val')).toHaveText('2.00 MB');

  await inject(request, PID.steam, 1024 * 1024); // reaches exactly 2 MB
  await expect(r.locator('.tag-limit')).toHaveText('Limit reached');
  await expect(r.locator('.limit-btn .bar i')).toHaveClass(/full/);
  await inject(request, PID.steam, 10 * 1024 * 1024);
  await page.waitForTimeout(1200);
  await expect(r.locator('.cell-used')).toHaveText('2.00 MB');
});

test('rejects an invalid limit', async ({ page }) => {
  await page.goto('/');
  await row(page, 'Steam').locator('.app-name').click();
  await page.locator('#adLimit').fill('0');
  await page.locator('#appDialog').getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('#adLimit')).toHaveClass(/invalid/);
});

test('overall limit blocks everything and can be reset', async ({ page, request }) => {
  await page.goto('/');
  await page.locator('#overallLimitBtn').click();
  await page.locator('#ldValue').fill('12');
  await page.locator('#ldUnit').getByRole('radio', { name: 'MB' }).click();
  await page.locator('#ldSave').click();
  await expect(page.locator('#limitDialog')).toBeHidden();
  await expect(page.locator('#usedMeter')).toBeVisible();
  await expect(page.locator('#overallLimitBtn')).toContainText('Edit limit');

  await inject(request, PID.chrome, 3 * 1024 * 1024); // 9 MB + 3 MB = 12 MB
  const banner = page.locator('.banner-danger', { hasText: 'Data limit reached' });
  await expect(banner).toBeVisible();
  await inject(request, PID.steam, 50 * 1024 * 1024);
  await page.waitForTimeout(1000);
  await expect(page.locator('#usedValue')).toContainText('12.0 MB');

  await banner.getByRole('button', { name: 'Reset usage' }).click();
  await page.locator('#confirmDialog').getByRole('button', { name: 'Reset' }).click();
  await expect(banner).toBeHidden();
  await expect(page.locator('#usedValue')).toContainText('0 B');
  await inject(request, PID.steam, 1024);
  await expect(page.locator('#usedValue')).toContainText('1.00 KB');
});

test('theme switches and persists', async ({ page, request }) => {
  await page.goto('/');
  await page.getByRole('radio', { name: 'Dark' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  const bg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  expect(bg).toBe('rgb(24, 24, 27)');
  await expect.poll(async () => (await snapshot(request)).settings.theme).toBe('dark');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page.getByRole('radio', { name: 'Dark' })).toHaveAttribute('aria-checked', 'true');
  await page.getByRole('radio', { name: 'Light' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.getByRole('radio', { name: 'System' }).click();
  await expect(page.locator('html')).not.toHaveAttribute('data-theme', /.+/);
});

test('system theme follows the OS', async ({ browser }) => {
  const ctx = await browser.newContext({ colorScheme: 'dark' });
  const page = await ctx.newPage();
  await page.goto('/');
  await expect(page.locator('.row').first()).toBeVisible();
  expect(await page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe('rgb(24, 24, 27)');
  await ctx.close();
});

test('search filters and sort orders', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('.row')).toHaveCount(2);
  await expect(page.locator('.row .app-name')).toHaveText(['Google Chrome', 'Steam']);
  await page.locator('#sort').selectOption('name');
  await expect(page.locator('.row .app-name')).toHaveText(['Google Chrome', 'Steam']);
  await page.keyboard.press('/');
  await expect(page.locator('#search')).toBeFocused();
  await page.keyboard.type('ste');
  await expect(page.locator('.row .app-name')).toHaveText(['Steam']);
  await page.keyboard.type('zzz');
  await expect(page.locator('#empty')).toBeVisible();
  await expect(page.locator('#emptyText')).toHaveText('No matches');
  await page.keyboard.press('Escape');
  await expect(page.locator('.row')).toHaveCount(2);
});

test('details dialog shows usage and can remove an app', async ({ page, request }) => {
  await page.goto('/');
  await row(page, 'Steam').locator('.app-name').click();
  const d = page.locator('#appDialog');
  await expect(d.locator('#adTotal')).toHaveText('1.00 MB');
  await expect(d.locator('#adPath')).toHaveText('C:\\Program Files (x86)\\Steam\\steam.exe');
  await expect(d.locator('#adKind')).toHaveText('Program');
  // the dialog switch controls blocking too
  await d.getByRole('switch').click();
  await expect(row(page, 'Steam').getByRole('switch')).toHaveAttribute('aria-checked', 'false');
  await expect(d.locator('#adForget')).toBeDisabled();
  await d.getByRole('switch').click();
  await expect(d.locator('#adForget')).toBeEnabled();
  await d.locator('#adForget').click();
  await page.locator('#confirmDialog').getByRole('button', { name: 'Remove' }).click();
  await expect(d).toBeHidden();
  await expect(row(page, 'Steam')).toHaveCount(0);
  await inject(request, PID.steam, 10); // comes back when it uses data again
  await expect(row(page, 'Steam')).toBeVisible();
});

test('settings change the reset schedule', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Settings' }).click();
  await expect(page.locator('#sdDayField')).toBeVisible();
  await page.locator('#sdPeriod').selectOption('never');
  await expect(page.locator('#sdDayField')).toBeHidden();
  await page.locator('#sdSave').click();
  await expect(page.locator('#periodNext')).toHaveText('Never');
  await page.getByRole('button', { name: 'Settings' }).click();
  await page.locator('#sdPeriod').selectOption('monthly');
  await page.locator('#sdDay').fill('31');
  await page.locator('#sdSave').click();
  await expect(page.locator('#sdDay')).toHaveClass(/invalid/);
});

test('no labels carry helper text and no console errors', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  await page.goto('/');
  await expect(page.locator('.row').first()).toBeVisible();
  await page.waitForTimeout(1500);
  expect(errors).toEqual([]);
});
