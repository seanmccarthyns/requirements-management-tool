import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

/**
 * #925 P3 — the whole My Work metrics section is compact, in both workspace densities, at every
 * supported desktop width.
 *
 * What the owner accepted is a noticeably shorter section with the queue moving up — not a pixel
 * budget. The recorded before values are the **complete section**: 132.6px comfortable and 114.8px
 * compact. An earlier correction made the card grid smaller while a standalone scope line gave the
 * space straight back, which is exactly why these assertions measure the full section and the queue's
 * position — never a sub-element that can pass while the page stays tall.
 */

const WIDTHS = [1280, 1440, 1920]
const BEFORE_SECTION = { comfortable: 132.6, compact: 114.8 } as const
const SECTION_LIMIT = { comfortable: 120, compact: 108 } as const

test('the whole metrics section is compact and the queue moves up, at every width and density', async ({ page, request }) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`

  await login(page, 'software.author', { openProject: false })
  await page.goto(`${root}/my-work`)
  await expect(page.getByRole('heading', { name: 'My Work' })).toBeVisible()

  const evidenceDir = process.env.AEROLINK_C6_EVIDENCE
  const measurements: string[] = []

  for (const density of ['comfortable', 'compact'] as const) {
    for (const width of WIDTHS) {
      await page.evaluate(d => { window.localStorage.setItem('aerolink-density', d) }, density)
      await page.setViewportSize({ width, height: 720 })
      await page.reload()
      await expect(page.getByRole('heading', { name: 'My Work' })).toBeVisible()

      const section = page.locator('.workMetrics')
      await expect(section).toBeVisible()
      // The scope is stated once for the row, and all four server-authoritative metrics remain with a
      // value. Exact counts are deliberately not pinned: other journeys sharing this disposable
      // database legitimately change the signed-in author's work items, and P3 is about the row's
      // presentation, not any moment's queue contents.
      await expect(section.getByText('Current program scope')).toHaveCount(1)
      const cards = page.locator('.workMetricsGrid article')
      await expect(cards).toHaveCount(4)
      for (const label of ['Assigned to me', 'Awaiting signature', 'Overdue', 'Drafts I own']) {
        await expect(cards.filter({ hasText: label }).locator('b')).toHaveText(/^\d+$/)
      }

      const sectionBox = await section.boundingBox()
      const queueBox = await page.locator('.workQueue').boundingBox()
      expect(sectionBox, `section at ${width}×${density}`).not.toBeNull()
      expect(queueBox, `queue at ${width}×${density}`).not.toBeNull()

      // The complete section — scope cell and cards together — must sit clearly under the recorded
      // before value in both densities.
      const height = sectionBox!.height
      expect(
        height,
        `complete section height at ${width}×${density} (before ${BEFORE_SECTION[density]}px)`,
      ).toBeLessThan(SECTION_LIMIT[density])

      // The queue below gains the difference: it must sit at least 12px higher than the recorded
      // before section put it (header height and the section's bottom margin are identical on both
      // sides of the comparison).
      const metricsTop = sectionBox!.y
      expect(
        queueBox!.y,
        `queue position at ${width}×${density}`,
      ).toBeLessThan(metricsTop + BEFORE_SECTION[density] + 20 - 12)

      measurements.push(
        `${width}×${density}: section ${height.toFixed(1)}px (before ${BEFORE_SECTION[density]}px), queue top ${queueBox!.y.toFixed(1)}px`)
      if (evidenceDir && width === 1280) {
        await page.screenshot({ path: `${evidenceDir}/my-work-${density}.png`, fullPage: false })
      }
    }
  }

  await test.info().attach('section-measurements', {
    body: measurements.join('\n'),
    contentType: 'text/plain',
  })
  console.log('My Work section measurements (after / before):\n' + measurements.join('\n'))
})
