import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
import { stateLabel } from './presentation'
import { loadCoverage, type Coverage } from './verificationCoverage'
import type { TestDiscipline } from './TestResultsWorkspace'
// The requirements explorer's stylesheet, imported rather than copied. Browsing a controlled artifact is the
// same job whichever discipline owns it, so the inspector is literally the same one.
import './RequirementsWorkspace.css'
// Coverage moved here from the test change request page, and its card and row styling came with it rather
// than being restyled to look almost the same.
import './TestingCoverageWorkspace.css'
import './TestProcedureExplorer.css'

type Procedure = {
  id: string
  revisionId: string
  displayNumber: string
  title: string
  state: string
  requirementCount: number
  ownerId: string
  objective?: string
  preconditions?: string
  steps?: string
  expectedResult?: string
}
type Revision = {
  id: string; displayNumber: string; revision: number; state: string; authorId: string; createdAt: string
  objective: string; preconditions: string; steps: string; expectedResult: string
  drivenBy: { changeRequest: string; package: string; subjectDisplayNumber: string; action: string }[]
  covers: string[]
}
type History = { id: string; baseNumber: string; title: string; ownerId: string; createdAt: string; revisions: Revision[] }
type Page = { page: number; pageSize: number; totalCount: number; totalPages: number; items: Procedure[] }
type Comment = {
  id: string; body: string; state: string; createdBy: string; createdAt: string; disposition?: string
}

type Tab = 'details' | 'trace' | 'history' | 'discussion'
/**
 * The two questions this page answers, kept apart.
 *
 * "Which procedures does this build carry, and what happened to each" is browsing an inventory. "Is this
 * build covered, and what is not" is a report about requirements. Both are about procedures as they stand,
 * which is why they are on one page — but they are different questions and a reader is asking one of them.
 */
type PageTab = 'procedures' | 'coverage'

/**
 * The scope the procedure list is asked for, which is the discipline's own name.
 *
 * This used to map to "system", "highLevel" and "lowLevel". The endpoint matches "System", "Software",
 * "HighLevelSoftware" and "LowLevelSoftware", so none of those matched anything and no filter was applied:
 * every discipline's Explorer listed all 515 procedures in the Project, System and HLR and LLR together. It
 * went unnoticed because nothing asserted the count, and it matters now that this is the only place
 * procedures are browsed — an HLR engineer confirming coverage could pick an LLR procedure off the list.
 */
const scopeOf = (discipline: TestDiscipline) => discipline
const disciplineLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'Software HLR' : 'Software LLR'

/**
 * Every controlled test procedure in this build, browsed the way requirements are browsed.
 *
 * The requirements explorer answers "what does this artifact say, what does it trace to, what happened to it,
 * and what has anybody said about it". Those are the same four questions asked of a procedure, so this is that
 * component's inspector rather than a second one that resembles it — same tabs, same stylesheet, same order.
 *
 * The trace runs the other way, and that is the one real difference: a requirement's trace shows what derives
 * from it, while a procedure's shows the requirements that drive it. A procedure exists because something has
 * to be verified.
 */
export default function TestProcedureExplorer({ api, projectId, releaseId, discipline, buildName, released }: {
  api: string; projectId: string; releaseId: string; discipline: TestDiscipline; buildName: string
  released: boolean
}) {
  const [data, setData] = useState<Page>()
  // Seeded from the address, so a link to one procedure opens on that procedure rather than on page one of
  // everything. The number narrows the list to it; the identifier selects it once the list arrives. These are
  // the parameter names the coverage page used before its library moved here, so links already in circulation
  // — and the requirement trace's "Open test procedure" — keep working.
  const opening = useRef(typeof location !== 'undefined' ? new URLSearchParams(location.search) : new URLSearchParams()).current
  const [query, setQuery] = useState(opening.get('procedure') ?? '')
  const [procedureState, setProcedureState] = useState(opening.get('procedureState') ?? '')
  const [procedureOutcome, setProcedureOutcome] = useState(opening.get('procedureOutcome') ?? '')
  const [page, setPage] = useState(Number(opening.get('procedurePage') ?? '1') || 1)
  const lastDiscreteState = useRef<string | null>(null)
  const [selectedId, setSelectedId] = useState(opening.get('procedureId') ?? '')
  const [tab, setTab] = useState<Tab>('details')
  const [history, setHistory] = useState<History>()
  const [comments, setComments] = useState<Comment[]>([])
  const [error, setError] = useState('')
  const [pageTab, setPageTab] = useState<PageTab>('procedures')
  const [coverage, setCoverage] = useState<Coverage>()
  const [coverageRead, setCoverageRead] = useState(false)
  const [showAll, setShowAll] = useState(false)

  const scope = scopeOf(discipline)
  // A page at a time, at the requirements explorer's own default. A build holds hundreds of procedures, and
  // the reader is looking for one of them.
  // Only the newest request may write the list.
  //
  // Changing a filter starts a second request while the first is still in flight, and nothing ordered the
  // replies. The unfiltered query is by far the slower one — it scans every procedure's coverage back to the
  // effective baseline — so the narrow filtered reply routinely arrived first and was then buried by the broad
  // reply behind it: the reader typed a search, saw the procedure they wanted, and watched the whole list they
  // had just filtered away come back over the top of it, with their search term still in the box.
  const listTicket = useRef(0)
  const load = useCallback(async () => {
    const mine = ++listTicket.current
    setError('')
    try {
      const response = await fetch(
        `${api}/api/test-procedures?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}` +
        `&search=${encodeURIComponent(query)}&state=${procedureState}&outcome=${procedureOutcome}` +
        `&page=${page}&pageSize=25`)
      if (!response.ok) throw new Error(String(response.status))
      const paged = await response.json()
      if (mine !== listTicket.current) return
      setData(paged)
    } catch (problem) {
      if (mine !== listTicket.current) return
      setError(operationError(problem, 'The procedure library could not be loaded.'))
    }
  }, [api, projectId, releaseId, scope, query, procedureState, procedureOutcome, page])
  useEffect(() => { void load() }, [load])

  // The worklist is in the address, so it can be reloaded, shared and stepped back through.
  useEffect(() => {
    const params = new URLSearchParams(location.search)
    const before = params.toString()
    const apply = (key: string, value: string) => { if (value) params.set(key, value); else params.delete(key) }
    apply('procedure', query)
    apply('procedureState', procedureState)
    apply('procedureOutcome', procedureOutcome)
    apply('procedurePage', page > 1 ? String(page) : '')
    // Seeded from what the address already says, so the reader's first change after a reload still earns a
    // history entry rather than being mistaken for arrival.
    const discrete = `${procedureState}|${procedureOutcome}|${page}`
    if (lastDiscreteState.current === null) lastDiscreteState.current = discrete
    if (params.toString() === before) return
    const next = `${location.pathname}${params.toString() ? `?${params}` : ''}`
    // Choosing a filter or a page is somewhere the reader went, so it earns a history entry and the back
    // button returns to the previous list. Typing in the search box is not somewhere they went; pushing per
    // keystroke would mean pressing back a dozen times to leave one search.
    const push = discrete !== lastDiscreteState.current
    lastDiscreteState.current = discrete
    // window.history explicitly: this component has its own `history` — the revision history of a procedure —
    // and the bare name resolves to that, which throws rather than navigating.
    if (push) window.history.pushState({}, '', next); else window.history.replaceState({}, '', next)
  }, [query, procedureState, procedureOutcome, page])

  // The browser's own navigation must move the list, not just the address bar.
  useEffect(() => {
    const restore = () => {
      const params = new URLSearchParams(location.search)
      setQuery(params.get('procedure') ?? '')
      setProcedureState(params.get('procedureState') ?? '')
      setProcedureOutcome(params.get('procedureOutcome') ?? '')
      setPage(Number(params.get('procedurePage') ?? '1') || 1)
    }
    addEventListener('popstate', restore)
    return () => removeEventListener('popstate', restore)
  }, [])

  const procedures = data?.items ?? []
  // Keyed on the page rather than the derived array, so the identity stays stable between renders. The
  // history and discussion effects watch this object, and a fresh one every render would refetch forever.
  const selected = useMemo(() => data?.items.find(x => x.id === selectedId), [data, selectedId])

  // Loaded when the tab is opened rather than with the list. A reader browsing forty procedures does not need
  // forty revision histories fetched on their behalf.
  useEffect(() => {
    if (!selected || tab !== 'history') return
    let active = true
    void (async () => {
      try {
        const response = await fetch(
          `${api}/api/test-procedures/${selected.id}/history?releaseId=${releaseId}&revisionId=${selected.revisionId}`)
        if (response.ok && active) setHistory(await response.json())
      } catch { if (active) setHistory(undefined) }
    })()
    return () => { active = false }
  }, [api, releaseId, selected, tab])

  const loadComments = useCallback(async (procedureId: string) => {
    try {
      const response = await fetch(`${api}/api/test-procedures/${procedureId}/comments`)
      if (response.ok) setComments(await response.json())
    } catch { setComments([]) }
  }, [api])
  // On selection rather than on opening the tab, because the tab wears the count. A number fetched only once
  // somebody looks is a number that is wrong until they do.
  useEffect(() => {
    if (!selected) return
    setComments([])
    void loadComments(selected.id)
  }, [loadComments, selected])

  const addComment = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!selected) return
    const form = event.currentTarget
    const body = String(new FormData(form).get('body'))
    const mentions = [...body.matchAll(/@([a-z0-9._-]+)/gi)].map(match => match[1])
    setError('')
    try {
      await apiRequest(`${api}/api/test-procedures/${selected.id}/comments`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ revisionId: selected.revisionId, body, mentions }),
      })
      form.reset()
      await loadComments(selected.id)
    } catch (problem) { setError(operationError(problem, 'The comment could not be added.')) }
  }

  // The resolve route reads ArtifactComments by identifier alone, so a procedure comment settles through the
  // same endpoint a requirement comment does rather than a second one that behaves almost the same.
  const resolveComment = async (id: string) => {
    if (!selected) return
    const disposition = window.prompt('Disposition or resolution rationale (optional):') ?? ''
    setError('')
    try {
      await apiRequest(`${api}/api/enterprise-requirements/comments/${id}/resolve`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ disposition }),
      })
      await loadComments(selected.id)
    } catch (problem) { setError(operationError(problem, 'The comment could not be resolved.')) }
  }

  // Switching discipline or build is a different page asking a different question, and this component stays
  // mounted across that switch. Without this, moving from HLR to LLR kept the coverage already read — the LLR
  // page would have shown HLR's numbers under an LLR heading, which is worse than showing nothing. The tab
  // goes back to the procedures it is named for, too.
  useEffect(() => {
    setCoverage(undefined)
    setCoverageRead(false)
    setShowAll(false)
    setPageTab('procedures')
  }, [api, projectId, releaseId, discipline])

  // Read when the coverage tab is first opened, not with the procedure list. Coverage is three requests and a
  // whole-configuration computation, and a reader who came to find one procedure by number should not pay for
  // a report they did not ask for. Read once and kept, because it does not change while the page is open.
  useEffect(() => {
    if (pageTab !== 'coverage' || coverageRead) return
    let active = true
    void (async () => {
      const { coverage: next, failed } = await loadCoverage(api, projectId, releaseId, discipline)
      if (!active) return
      setCoverageRead(true)
      if (next) setCoverage(next)
      if (failed) setError('The requirement coverage for this build could not be read.')
    })()
    return () => { active = false }
  }, [api, projectId, releaseId, discipline, pageTab, coverageRead])

  const uncovered = coverage?.items.filter(x => x.disposition === 'Uncovered') ?? []
  const suspect = coverage?.items.filter(x => x.disposition === 'Suspect') ?? []

  const open = (procedure: Procedure) => {
    setSelectedId(procedure.id); setTab('details'); setHistory(undefined)
    const params = new URLSearchParams(location.search)
    params.set('procedure', procedure.displayNumber)
    params.set('procedureId', procedure.id)
    params.set('procedureRevisionId', procedure.revisionId)
    window.history.replaceState({}, '', `${location.pathname}?${params}`)
  }
  const close = () => {
    setSelectedId('')
    const params = new URLSearchParams(location.search)
    params.delete('procedureId')
    params.delete('procedureRevisionId')
    window.history.replaceState({}, '', `${location.pathname}${params.size ? `?${params}` : ''}`)
  }

  // A workspace is its own <main>: the shell supplies the navigation and context bar, not the landmark.
  return <main className="procedureExplorer">
    <header className="procedureExplorerHead">
      <div>
        <p className="eyebrow">VERIFICATION / {disciplineLabel(discipline).toUpperCase()}</p>
        <h1>Test Procedure Explorer</h1>
        <p>Every controlled {disciplineLabel(discipline)} procedure {buildName} carries, and what it covers.</p>
      </div>
    </header>
    {error && <div className="workspaceError" role="alert">{error}</div>}

    <div className="explorerTabs" role="tablist" aria-label="Test procedure views">
      <button type="button" role="tab" aria-selected={pageTab === 'procedures'}
        className={pageTab === 'procedures' ? 'active' : ''}
        onClick={() => setPageTab('procedures')}>Procedures</button>
      <button type="button" role="tab" aria-selected={pageTab === 'coverage'}
        className={pageTab === 'coverage' ? 'active' : ''}
        onClick={() => setPageTab('coverage')}>Requirement coverage</button>
    </div>

    {pageTab === 'coverage' && (
      <div className="explorerCoverage">
        <section className="coverageSummary" aria-label="Coverage summary">
          <article><b>{coverage?.total ?? 0}</b><span>Requirements</span></article>
          <article><b>{coverage?.covered ?? 0}</b><span>With a procedure</span></article>
          <article className={uncovered.length ? 'attention' : ''}><b>{uncovered.length}</b><span>With none</span></article>
          <article className={suspect.length ? 'attention' : ''}><b>{suspect.length}</b><span>Suspect coverage</span></article>
        </section>

        {(uncovered.length > 0 || suspect.length > 0) && (
          <section className="coverageCard">
            <div className="cardTitle">
              <h2>Requirements needing attention</h2>
              <p>A requirement with no procedure cannot be verified, and coverage carried across a change nobody reconfirmed does not count.</p>
            </div>
            {uncovered.slice(0, 25).map(item => (
              <article className="coverageRow attention" key={item.revisionId}>
                <div><b>{item.displayNumber}</b><i>No procedure</i></div>
                <p>{item.statement}</p>
              </article>
            ))}
            {suspect.slice(0, 25).map(item => (
              <article className="coverageRow attention" key={`suspect-${item.revisionId}`}>
                <div><b>{item.displayNumber}</b><i>Suspect</i></div>
                <p>{item.statement}</p>
                <small>Covered by {item.coveredBy.map(x => x.displayNumber).join(', ')}, written against earlier wording.</small>
              </article>
            ))}
          </section>
        )}

        <section className="coverageCard">
          <div className="cardTitle">
            <h2>Requirement coverage</h2>
            <p>Every effective requirement in this build and the procedures that verify it.</p>
          </div>
          {/* Attention first, then everything. A reader arriving to do work needs the requirements that cannot
              be verified as things stand; a reader answering "is this build covered" needs the whole set. The
              second is much the longer list, so it is asked for rather than imposed. */}
          <button type="button" className="quiet" onClick={() => setShowAll(current => !current)}>
            {showAll ? 'Show only what needs attention' : `Show all ${coverage?.total ?? 0} requirements`}
          </button>
          {showAll && (
            <div className="fullCoverage">
              {(coverage?.items ?? []).map(item => (
                <article className={`coverageRow ${item.covered ? '' : 'attention'}`} key={`all-${item.revisionId}`}>
                  <div>
                    <b>{item.displayNumber}</b>
                    {/* Suspect is read before "no procedure". A requirement whose only procedure was written
                        against an earlier revision is not covered — but saying nothing is testing it hides the
                        procedure somebody has to reconfirm or replace, which is the actual work. */}
                    <i>{item.verified ? 'Verified'
                      : item.coveredBy.some(x => x.coverageState === 'Suspect') ? 'Suspect'
                      : item.covered ? 'Covered'
                      : 'No procedure'}</i>
                  </div>
                  <p>{item.statement}</p>
                  {item.coveredBy.length > 0 && <small>{item.coveredBy.map(x => `${x.displayNumber} (${x.state})`).join(', ')}</small>}
                </article>
              ))}
            </div>
          )}
        </section>

        {coverageRead && !coverage && (
          <p className="coverageNone">
            This build has not materialized its requirements, so there is nothing to report coverage against yet.
          </p>
        )}
      </div>
    )}

    {pageTab === 'procedures' && <>
    {/* Browsing, not just searching. The software side of the demonstration Program carries 440 procedures,
        so a list that could only be searched meant knowing the number of the thing you were looking for
        before you could look for it. State and latest result are how somebody actually narrows this: "the
        drafts", "what failed last time". */}
    <div className="procedureFilters">
      <label className="procedureFind">
        <span>Find a procedure</span>
        <input value={query} onChange={event => { setQuery(event.target.value); setPage(1) }}
          placeholder="Number or title" />
      </label>
      <label>
        <span>Procedure state</span>
        <select value={procedureState} onChange={event => { setProcedureState(event.target.value); setPage(1) }}>
          <option value="">All states</option>
          <option value="Draft">Draft</option>
          <option value="InReview">In review</option>
          <option value="Approved">Approved</option>
        </select>
      </label>
      <label>
        <span>Latest result</span>
        <select value={procedureOutcome} onChange={event => { setProcedureOutcome(event.target.value); setPage(1) }}>
          <option value="">All outcomes</option>
          <option value="Pass">Pass</option>
          <option value="Fail">Fail</option>
          <option value="Blocked">Blocked</option>
        </select>
      </label>
    </div>

    <div className="procedureExplorerSplit">
      <section className="procedureList" aria-label="Test procedures">
        {procedures.length === 0
          ? <p className="procedureEmpty">{query || procedureState || procedureOutcome
            ? 'No procedure matches that. Clear the search or the filters to see the rest.'
            : `This build has no controlled ${disciplineLabel(discipline).toLowerCase()} procedures yet.`}</p>
          : procedures.map(procedure => (
            <button type="button" key={procedure.id}
              className={`procedureRow ${procedure.id === selectedId ? 'selected' : ''}`}
              aria-pressed={procedure.id === selectedId}
              onClick={() => open(procedure)}>
              <b>{procedure.displayNumber}</b>
              <span>{procedure.title}</span>
              <small>{procedure.state} · verifies {procedure.requirementCount}</small>
            </button>
          ))}
        <div className="pager">
          <button disabled={(data?.page ?? 1) <= 1} onClick={() => setPage(x => x - 1)}>← Previous</button>
          <span>
            {(data?.totalCount ?? 0) > 0
              ? `${((data?.page ?? 1) - 1) * (data?.pageSize ?? 25) + 1}–` +
                `${Math.min((data?.page ?? 1) * (data?.pageSize ?? 25), data?.totalCount ?? 0)} ` +
                `of ${(data?.totalCount ?? 0).toLocaleString()}`
              : '0 procedures'}
          </span>
          <button disabled={(data?.page ?? 1) >= (data?.totalPages ?? 1)}
            onClick={() => setPage(x => x + 1)}>Next →</button>
        </div>
      </section>

      {selected && (
        <aside className="requirementInspector" aria-label={`${selected.displayNumber} detail`}>
          <div className="inspectorTop">
            <div>
              <b>{selected.displayNumber}</b>
              <span>{selected.title}</span>
            </div>
            <button type="button" className="inspectorClose" onClick={close}
              aria-label="Close procedure detail">×</button>
          </div>
          <div className="inspectorTabs">
            <button className={tab === 'details' ? 'active' : ''} onClick={() => setTab('details')}>Overview</button>
            <button className={tab === 'trace' ? 'active' : ''} onClick={() => setTab('trace')}>Trace &amp; impact</button>
            <button className={tab === 'history' ? 'active' : ''} onClick={() => setTab('history')}>History</button>
            <button className={tab === 'discussion' ? 'active' : ''} onClick={() => setTab('discussion')}>
              Discussion <span>{comments.length}</span>
            </button>
          </div>

          {tab === 'details' && (
            <div className="inspectorBody">
              <dl className="procedureCase">
                <dt>Objective</dt><dd>{selected.objective || 'Not recorded'}</dd>
                <dt>Preconditions</dt><dd>{selected.preconditions || 'None'}</dd>
                <dt>Steps</dt><dd>{selected.steps || 'Not recorded'}</dd>
                <dt>Expected result</dt><dd>{selected.expectedResult || 'Not recorded'}</dd>
                <dt>State</dt><dd>{selected.state}</dd>
                <dt>Owner</dt><dd><PersonName userName={selected.ownerId} /></dd>
              </dl>
            </div>
          )}

          {tab === 'trace' && (
            <div className="inspectorBody">
              {/* The other direction from a requirement's trace: a requirement shows what derives from it, a
                  procedure shows what it exists to verify. */}
              <p className="inspectorNote">
                This procedure verifies {selected.requirementCount} requirement{selected.requirementCount === 1 ? '' : 's'}.
              </p>
              {selected.requirementCount === 0 && (
                <p className="inspectorNote warn">
                  Nothing is verified by this procedure. Either it has not been linked yet, or the requirement it
                  was written against has been retired.
                </p>
              )}
            </div>
          )}

          {tab === 'history' && (
            <div className="inspectorBody">
              {history
                ? <ul className="revisionList">{history.revisions.map(revision => (
                  <li key={revision.id}>
                    <b>{revision.displayNumber}</b>
                    <span>{revision.state} · written by <PersonName userName={revision.authorId} /></span>
                    {revision.drivenBy.length > 0 && (
                      <span className="revisionDriver">
                        {revision.drivenBy.map(driver => `${driver.package} · ${driver.changeRequest}`).join(', ')}
                      </span>
                    )}
                  </li>))}</ul>
                : <p className="inspectorNote">Loading history…</p>}
            </div>
          )}

          {tab === 'discussion' && (
            <div className="inspectorBody discussionPane">
              {!released ? <form onSubmit={addComment}>
                <textarea name="body" required
                  placeholder="Add an attributable comment. Use @username to mention someone." />
                <div className="commentFoot"><button>Add comment</button></div>
              </form> : <div className="traceEmpty">
                <span>Discussion is read-only in released {buildName}.</span>
              </div>}
              {comments.map(comment => (
                <article key={comment.id} className={comment.state.toLowerCase()}>
                  <div>
                    <b><PersonName userName={comment.createdBy} /></b>
                    <span>{new Date(comment.createdAt).toLocaleString()}</span>
                  </div>
                  <p>{comment.body}</p>
                  {comment.disposition && <small>Disposition: {comment.disposition}</small>}
                  <footer>
                    <i>{stateLabel(comment.state)}</i>
                    {comment.state === 'Open' && !released && (
                      <button onClick={() => void resolveComment(comment.id)}>Resolve / disposition</button>
                    )}
                  </footer>
                </article>
              ))}
            </div>
          )}
        </aside>
      )}
    </div>
    </>}
  </main>
}
