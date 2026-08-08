import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

/**
 * Browsing controlled procedures the way requirements are browsed.
 *
 * The requirements explorer answers what an artifact says, what it traces to, what happened to it, and what
 * anybody has said about it. Those are the same four questions asked of a procedure, so this page uses that
 * component's inspector rather than a second one that resembles it.
 */
test('a procedure opens onto the same four-tab inspector a requirement does', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()

  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  const rows = page.locator('.procedureRow')
  await expect(rows.first()).toBeVisible({ timeout: 30_000 })
  const number = (await rows.first().locator('b').textContent())!.trim()
  expect(number).toMatch(/^SYSTP-\d{6}/)

  await rows.first().click()
  const inspector = page.getByRole('complementary', { name: new RegExp(`${number.replace('.', '\\.')} detail`) })
  await expect(inspector).toBeVisible()

  // The same four, in the same order, from the same stylesheet.
  for (const tab of ['Overview', 'Trace & impact', 'History']) {
    await expect(inspector.getByRole('button', { name: tab })).toBeVisible()
  }
  await expect(inspector.getByRole('button', { name: /^Discussion/ })).toBeVisible()

  await expect(inspector.getByText('Objective', { exact: true })).toBeVisible()

  // Trace runs the other way from a requirement's: a procedure shows what it exists to verify.
  await inspector.getByRole('button', { name: 'Trace & impact' }).click()
  await expect(inspector).toContainText('verifies')

  await inspector.getByRole('button', { name: 'History' }).click()
  await expect(inspector.locator('.revisionList li').first()).toBeVisible({ timeout: 30_000 })

  // Discussion is the requirement pane's own form and article markup, so what is asserted below is what would
  // hold on a requirement: an attributable comment that can then be dispositioned.
  await inspector.getByRole('button', { name: /^Discussion/ }).click()
  const comments = inspector.locator('.discussionPane article')
  const saidBefore = await comments.count()
  await inspector.locator('.discussionPane textarea').fill('Confirmed against the oceanic rig on the 6th.')
  await inspector.getByRole('button', { name: 'Add comment' }).click()
  await expect(comments).toHaveCount(saidBefore + 1, { timeout: 30_000 })
  await expect(comments.last()).toContainText('Confirmed against the oceanic rig on the 6th.')

  // It is a controlled record, not view state: it survives a reload.
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })
  await page.locator('.procedureRow').filter({ hasText: number }).first().click()
  const reopened = page.getByRole('complementary', { name: new RegExp(`${number.replace('.', '\\.')} detail`) })
  await reopened.getByRole('button', { name: /^Discussion/ }).click()
  const reloaded = reopened.locator('.discussionPane article').last()
  await expect(reloaded).toContainText('Confirmed against the oceanic rig on the 6th.', { timeout: 30_000 })

  // Resolving goes through the artifact-comment route the requirements pane uses, not a procedure-only twin.
  page.once('dialog', dialog => void dialog.accept('Rig log attached.'))
  await reloaded.getByRole('button', { name: 'Resolve / disposition' }).click()
  await expect(reopened.locator('.discussionPane article').last()).toContainText('Rig log attached.')
})

/**
 * Who wrote a procedure, and what made them change it.
 *
 * A procedure is read by somebody deciding whether to trust it, and its revisions were once reachable only
 * one at a time with no way to see what drove any of them. The change request behind a revision is reached
 * through the verification decision that resolved to it, which is the record that actually connects the two.
 *
 * Asked here rather than on the change request page, which used to carry a procedure library and no longer
 * does. The question did not move; the library did.
 */
test('a procedure says who wrote it and what drove each revision', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.procedureRow').filter({ hasText: 'SYSTP-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()

  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await inspector.getByRole('button', { name: 'History' }).click()

  // Every revision, newest first, each saying who wrote it — a name, not an account handle.
  const revisions = inspector.locator('.revisionList li')
  await expect(revisions.first()).toBeVisible({ timeout: 30_000 })
  await expect(revisions.first()).toContainText('written by')
  await expect(revisions.first()).toContainText(/SYSTP-000001\.\d{2}/)
  await expect(revisions.first().locator('.personName')).toBeVisible()
  // A revision driven by a controlled package names it rather than leaving the reader to guess.
  await expect(inspector.locator('.revisionDriver').first()).toBeVisible({ timeout: 30_000 })
})

/**
 * Each discipline's Explorer holds its own procedures and its own coverage.
 *
 * HLR and LLR procedures live side by side in one Project, so a page that showed both would let a reader
 * confirm coverage against a procedure from the wrong level. This moved here with the library: the change
 * request page used to make the same guarantee about a procedure list it no longer carries.
 */
test('software HLR and LLR each have their own procedures and coverage', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Software HLR Test Procedure Explorer' }).click()
  await expect(page).toHaveURL(/software-verification\/hlr\/procedures$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE HLR')).toBeVisible()
  await expect(page.locator('.pager')).toContainText('of 160', { timeout: 30_000 })
  await expect(page.locator('.procedureList')).not.toContainText('LLRTP-')
  await page.getByRole('tab', { name: 'Requirement coverage' }).click()
  await expect(page.locator('.coverageSummary article').first().locator('b')).toHaveText('400', { timeout: 30_000 })

  await page.getByRole('link', { name: 'Software LLR Test Procedure Explorer' }).click()
  await expect(page).toHaveURL(/software-verification\/llr\/procedures$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE LLR')).toBeVisible()
  await expect(page.locator('.pager')).toContainText('of 280', { timeout: 30_000 })
  await expect(page.locator('.procedureList')).not.toContainText('HLRTP-')
  await page.getByRole('tab', { name: 'Requirement coverage' }).click()
  await expect(page.locator('.coverageSummary article').first().locator('b')).toHaveText('700', { timeout: 30_000 })
})

/**
 * Nothing here writes a procedure either.
 *
 * The library moved to this page, and the rule moved with it: a procedure is introduced, modified or retired
 * by a test change request and by nothing else. Browsing procedures is reading, so the page that browses them
 * offers no way to write one — the same guarantee the change request page makes, asserted where the list now
 * actually is.
 */
test('the Explorer browses procedures without offering a way to write one', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await expect(page.getByRole('button', { name: /New test procedure/ })).toHaveCount(0)
  await expect(page.getByRole('dialog', { name: 'Create a test procedure' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toHaveCount(0)

  // It still reads, which is the point: procedures are browsable without being writable.
  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  await expect(page.locator('.procedureRow').filter({ hasText: 'SYSTP-000001' }).first())
    .toBeVisible({ timeout: 30_000 })
})

test('released Build 1.5 procedures remain readable without create or edit actions', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page)
  await page.getByRole('button', { name: /Back to Software Builds/ }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await expect(page.getByRole('button', { name: /New test procedure/ })).toHaveCount(0)
  // Build 1.6 carries a later draft of this stable procedure identity. The released Build 1.5 Explorer must
  // keep its exact manifest revision primary, including after the selection becomes a reloadable deep link.
  await page.getByLabel('Find a procedure').fill('SYSTP-000040')
  const row = page.locator('.procedureRow').filter({ hasText: 'SYSTP-000040.00' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.procedureList')).not.toContainText('SYSTP-000040.01')
  await row.click()
  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await expect(inspector).toContainText('SYSTP-000040.00')
  await expect(page).toHaveURL(/procedure=SYSTP-000040\.00.*procedureId=.*procedureRevisionId=/)
  const exactUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(exactUrl)
  await expect(page.locator('.requirementInspector')).toContainText('SYSTP-000040.00', { timeout: 30_000 })
  await expect(inspector).toContainText('Objective')
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toHaveCount(0)
  // A released build is read-only, so its procedures cannot be discussed either.
  await inspector.getByRole('button', { name: /^Discussion/ }).click()
  await expect(inspector.locator('.discussionPane textarea')).toHaveCount(0)
})

/**
 * The whole inventory, on request.
 *
 * The tab opens on what is not covered, because that is the work. The full list is still needed to answer
 * "is this specific requirement tested?", so it is one toggle away rather than a separate page.
 */
test('the full requirement coverage table is one toggle away', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('tab', { name: 'Requirement coverage' }).click()

  const toggle = page.getByRole('button', { name: /Show all \d+ requirements/ })
  await expect(toggle).toBeVisible({ timeout: 30_000 })
  await expect.poll(async () => Number(/Show all (\d+)/.exec((await toggle.textContent()) ?? '')?.[1] ?? 0), { timeout: 30_000 }).toBeGreaterThan(0)
  const listed = Number(/Show all (\d+)/.exec((await toggle.textContent()) ?? '')?.[1] ?? 0)
  await toggle.click()
  await expect(page.getByRole('button', { name: 'Show only what needs attention' })).toBeVisible()
  await expect(page.locator('.fullCoverage .coverageRow')).toHaveCount(listed, { timeout: 30_000 })
})
