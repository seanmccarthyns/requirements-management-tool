import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

/**
 * #925 P2 — the instance badge names the installation without shouting deployment vocabulary.
 *
 * A declared instance label may carry its classification as a suffix ("HOME CANONICAL"). Routine pages
 * show the plain installation name; the full declared label, classification, source, database and
 * snapshot facts stay in the operator tooltip. Nothing here reclassifies or renames the installation —
 * the payload below is exactly the shape /health/identity returns, replayed so the proof does not
 * depend on which installation the build happens to run against.
 */

test('the badge shows the installation name and keeps the declaration in the tooltip', async ({ page, request }) => {
  await showcaseSeed(request)
  // The declaration spells the classification as one PascalCase word (`HomeCanonical`) while the label
  // spaces it — the payload is the production shape, not the display words.
  await page.route('**/health/identity', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      sourceShortSha: 'abc1234',
      mode: 'HOME-PRODUCTION',
      instance: { label: 'HOME CANONICAL', classification: 'HomeCanonical', snapshot: null },
      database: { name: 'aerolink' },
    }),
  }))
  await login(page, 'admin')

  const badge = page.getByTestId('instance-badge')
  await expect(badge).toHaveText('HOME')
  await expect(badge).not.toContainText('CANONICAL')
  await expect(badge).toHaveAttribute('title', /Instance: HOME CANONICAL \(HomeCanonical\)/)
  await expect(badge).toHaveAttribute('title', /Database: aerolink/)
  await expect(badge).toHaveAttribute('data-classification', 'HomeCanonical')
})

test('an undeclared installation keeps its modest label unchanged', async ({ page, request }) => {
  await showcaseSeed(request)
  await page.route('**/health/identity', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      sourceShortSha: 'abc1234',
      mode: 'UNKNOWN',
      instance: { label: 'AEROLINK', classification: 'Undeclared', snapshot: null },
      database: { name: 'aerolink' },
    }),
  }))
  await login(page, 'admin')

  const badge = page.getByTestId('instance-badge')
  await expect(badge).toHaveText('AEROLINK')
  await expect(badge).toHaveAttribute('data-classification', 'Undeclared')
})

test('custom declared labels render verbatim under other supported classifications', async ({ page, request }) => {
  await showcaseSeed(request)
  await login(page, 'admin')
  // The explicit rule names HOME CANONICAL and nothing else: a Demo or work-laptop declaration keeps
  // its operator's own label, word for word (#925 P2 / Astra C-ASTRA-R2-F01).
  for (const declared of [
    { label: 'Customer Demo', classification: 'LocalDemo' },
    { label: 'Flight Test Local', classification: 'WorkLaptopLocal' },
  ]) {
    await page.route('**/health/identity', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        sourceShortSha: 'abc1234',
        mode: 'UNKNOWN',
        instance: { label: declared.label, classification: declared.classification, snapshot: null },
        database: { name: 'aerolink' },
      }),
    }))
    await page.reload()
    const badge = page.getByTestId('instance-badge')
    await expect(badge).toHaveText(declared.label)
    await expect(badge).toHaveAttribute('data-classification', declared.classification)
  }
})
