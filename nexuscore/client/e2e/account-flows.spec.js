import { expect, test } from '@playwright/test';

const testPassword = 'NexusE2e123!';

async function registerAccount(page) {
  const suffix = `${Date.now()}${Math.floor(Math.random() * 1_000_000)}`;
  const username = `e2e_${suffix}`;
  const email = `${username}@example.test`;

  await page.goto('/register');
  await page.locator('input[type="text"]').fill(username);
  await page.locator('input[type="email"]').fill(email);
  await page.locator('input[type="password"]').fill(testPassword);
  await page.getByRole('button', { name: 'Sign Up' }).click();

  await expect(page).toHaveURL(/\/$/);
  await expect(page.locator('button.avatar-btn')).toBeVisible();
  return { username, email };
}

test('registration creates an account that can open the protected library', async ({ page }) => {
  const { username } = await registerAccount(page);

  await page.goto('/library');
  await expect(page).toHaveURL(/\/library$/);
  await expect(page.getByRole('heading', { name: /library/i })).toBeVisible();

  await page.locator('button.avatar-btn').click();
  await page.getByRole('link', { name: 'Profile' }).click();
  await expect(page.getByRole('heading', { name: username })).toBeVisible();
});

test('a registered user can log in again and sign out', async ({ page }) => {
  const { email } = await registerAccount(page);

  await page.locator('button.avatar-btn').click();
  await page.getByRole('button', { name: 'Sign Out' }).click();
  await expect(page.getByRole('link', { name: 'Log In' })).toBeVisible();

  await page.getByRole('link', { name: 'Log In' }).click();
  await page.locator('input[type="email"]').fill(email);
  await page.locator('input[type="password"]').fill(testPassword);
  await page.getByRole('button', { name: 'Log In' }).click();

  await expect(page).toHaveURL(/\/$/);
  await expect(page.locator('button.avatar-btn')).toBeVisible();
  await page.locator('button.avatar-btn').click();
  await page.getByRole('button', { name: 'Sign Out' }).click();
  await expect(page.getByRole('link', { name: 'Sign Up' })).toBeVisible();
});

test('a regular user is redirected away from the admin panel', async ({ page }) => {
  await registerAccount(page);

  await page.goto('/admin');

  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole('link', { name: 'Log In' })).not.toBeVisible();
  await expect(page.locator('button.avatar-btn')).toBeVisible();
});
