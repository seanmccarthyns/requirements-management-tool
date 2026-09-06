import { expect, test } from '@playwright/test'
import { login } from './auth'

test('Password visibility is a compact control that does not cover the password field', async ({ page }) => {
  await page.goto('/')
  const input = page.getByLabel('Password')
  const toggle = page.getByRole('button', { name: 'Reveal typed characters' })

  await expect(input).toBeVisible()
  await expect(toggle).toBeVisible()
  const [inputBox, toggleBox] = await Promise.all([input.boundingBox(), toggle.boundingBox()])
  expect(inputBox).not.toBeNull()
  expect(toggleBox).not.toBeNull()
  expect(toggleBox!.width).toBeLessThanOrEqual(48)
  expect(toggleBox!.height).toBeLessThanOrEqual(inputBox!.height)
  expect(toggleBox!.x).toBeGreaterThan(inputBox!.x + inputBox!.width - 60)
  await expect(toggle).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)')

  await input.fill('AeroLink!2026')
  await toggle.click()
  await expect(input).toHaveAttribute('type', 'text')
  await expect(input).toHaveValue('AeroLink!2026')
  await page.getByRole('button', { name: 'Conceal typed characters' }).click()
  await expect(input).toHaveAttribute('type', 'password')
})

test('AeroLink starts against the real API and presents a valid entry state', async ({ page }) => {
  const seedless = process.env.AEROLINK_E2E_SKIP_SHOWCASE_SEED === 'true'
  await login(page,'admin',{openProject:!seedless})
  await expect(page.getByText(/AeroLink/).first()).toBeVisible()
  await expect(page.getByRole('heading', { name: seedless ? 'Create your first program' : 'Command Center' })).toBeVisible()
})

test('The sign-in story panel gives truthful workspace context without changing authentication', async ({ page }) => {
  await page.goto('/')
  await expect(page.locator('.loginBrand')).toContainText('AeroLink')
  await expect(page.locator('.loginStoryContext')).toContainText('CONTROLLED ENGINEERING WORKSPACE')
  // The owner-mandated sign-in headline (#925 P1), singular "Document" and all.
  await expect(page.locator('.loginStoryContext h1')).toHaveText(
    'Requirements, Verification, Changes, Evidence, Document, and more in one connected record')
  // The explanatory paragraph and the access statement were removed by the same owner direction.
  await expect(page.locator('.loginStoryContext')).not.toContainText('Sign in to reach the programs')
  await expect(page.locator('.loginStoryContext')).not.toContainText('PROJECT ACCESS IS ENFORCED')
  if (process.env.AEROLINK_C6_EVIDENCE) {
    await page.screenshot({ path: `${process.env.AEROLINK_C6_EVIDENCE}/signin-after.png`, fullPage: false })
  }
  await expect(page.locator('.loginStoryEndpoint')).toContainText(new URL(page.url()).origin)
  await expect(page.getByLabel('Username')).toBeVisible()
  await expect(page.getByLabel('Password')).toBeVisible()
  await expect(page.getByRole('button', { name: /Sign in securely/ })).toBeVisible()
  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('AeroLink!2026')
  await page.getByRole('button', { name: /Sign in securely/ }).click()
  await expect(page.getByRole('heading', { name: /Create your first program|Projects/ })).toBeVisible()
})

test('Sign in recovers cleanly when the local API is temporarily unavailable', async ({ page }) => {
  await page.route('**/api/auth/login', route => route.abort('connectionrefused'))
  await page.goto('/')
  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('AeroLink!2026')
  await page.getByRole('button', { name: /Sign in securely/ }).click()

  await expect(page.getByText(/could not reach its local API/i)).toBeVisible()
  await expect(page.getByRole('button', { name: /Sign in securely/ })).toBeEnabled()

  await page.unroute('**/api/auth/login')
  await page.getByRole('button', { name: /Sign in securely/ }).click()
  await expect(page.getByRole('heading', { name: /Create your first program|Projects/ })).toBeVisible()
})
