# Documentation & Commit Workflow for Claude Code

Purpose: a set of conventions that make an agentic coding session (Claude
Code working autonomously across many small steps) produce a codebase that
reads cleanly to a human afterward — applied here to the Vintage Story
shader mod, but the conventions are project-agnostic.

The core idea: **Claude Code re-derives context every session from what's on
disk** — it doesn't remember yesterday's reasoning unless you wrote it down
somewhere it will read. Every convention below exists to make that
re-derivation cheap and accurate.

---

## 1. The CLAUDE.md file (read this first, every session)

Claude Code automatically looks for and reads a `CLAUDE.md` file at the repo
root at the start of a session. Treat it as **standing instructions**, not
a changelog — it should answer "what does an agent need to know before
touching this repo" in under one screen.

```
/CLAUDE.md
```

What belongs in it:
- **Build/run/test commands** — exact shell commands, not prose descriptions
  ("`dotnet build`", not "build it with dotnet")
- **Repo-specific conventions** this document defines (link to it)
- **Non-obvious constraints** — e.g. "shader patches use regex against
  vanilla files that change every game version; always verify the match
  string against the current installed game's `assets/game/shaders/`
  before trusting an old patch"
- **Where things live** — one line per major folder, not a full file tree
- **What NOT to do** — e.g. "never hand-edit files under `/generated/`,
  they're rebuilt by the atlas preprocessor"

Keep it updated as a living doc — when a convention changes, edit
`CLAUDE.md` in the same commit as the change that caused it.

---

## 2. Commit habits

### Granularity: one logical change per commit
Claude Code should commit after each *working, verifiable* unit — not after
each file edit, and not after an entire multi-file feature. A commit is the
right size if you could `git revert` it alone without breaking the build.

For shader/graphics work specifically: **a commit that changes a shader
patch should be small enough that if it breaks rendering, `git bisect` finds
it in one step** — don't bundle a shader change with an unrelated config
refactor.

### Commit message format
Use **Conventional Commits** — Claude Code can parse and generate these
reliably, and they make `git log` skimmable months later:

```
<type>(<scope>): <short summary, imperative mood, no period>

<optional body: why, not what — the diff already shows what>

<optional footer: closes #12, breaking-change notes>
```

Types: `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `perf`, `style`.

Examples fit to this project:
```
feat(colorgrade): add exposure/saturation live config bindings

fix(reflections): fall back to non-deferred path when shader compile fails

refactor(pbr): extract Sobel pass into standalone AtlasPreprocessor class

docs(readme): document known SSR occlusion limitation
```

**Why "why, not what" in the body matters for an agent specifically:** a
future Claude Code session reading `git log` needs the *reasoning* to avoid
re-breaking something. "Fixed crash" tells it nothing; "deferred rendering
shader compile silently fails on some AMD drivers, add explicit fallback
instead of trusting compile success" tells the next session exactly what
invariant to protect.

### Commit before risky operations
Before any regex shader patch, atlas regeneration logic, or anything that
could corrupt cached/generated files, commit clean first. Agentic sessions
that fail mid-task are much easier to recover from a clean prior commit than
by trying to manually unwind partial edits.

### Never commit generated/cached output
`.gitignore` the derived PBR atlases, build artifacts, and anything the
`AtlasPreprocessor` regenerates. Commit the *code that generates them*, not
their output — otherwise diffs become unreadable binary noise and the repo
balloons.

---

## 3. Code-level documentation

### Doc comments explain *why*, inline comments explain *non-obvious how*
Don't comment what the code already says. Do comment:
- Why a magic number is what it is (`0.35` threshold in the spec-mask BFS
  pass — cite what it was tuned against)
- Why an approach was chosen over an obvious alternative ("using variance
  instead of a proper frequency-domain roughness estimate — cheaper, good
  enough at 16-32px texel resolution")
- Anything that will look like a bug to a future reader but isn't
  ("intentionally not normalizing here, see PBR.md")

### One README per subsystem folder, not just one at repo root
For a project with several semi-independent subsystems, a single root README
gets stale fastest in the section nobody is currently working on. Keep design
detail beside the code instead:

```
/src/PseudoPBR/README.md   <- what this subsystem does, its inputs/outputs,
                               known limitations, how to test it standalone
```

Root `README.md` stays high-level: what the mod is, install instructions,
credits, and links down into subsystem READMEs.

### Keep docs/STATUS.md current in EVERY commit

`CHANGELOG.md` records milestones for users. `docs/STATUS.md` records the state
of every feature and idea for whoever picks this up next, and it is the only
place a half-finished thought is safe from being forgotten.

Update it in the **same commit** as the work, not afterwards:

- A feature that started, finished, or changed verification level moves.
- A feature seen working in game moves to **L4** — that is the only level that
  closes anything, and it is the one most likely to go unrecorded because it
  happens outside the repo.
- An idea that was tried and rejected goes to the abandoned log **with the
  reason**, so it is not proposed again without new information.
- A new idea, however speculative, gets a row rather than living in a commit
  message nobody will search for.

A commit that changes what the mod does and leaves STATUS.md alone is
incomplete.

### Keep a CHANGELOG.md, update it every milestone (not every commit)
Commits are for developers/agents; CHANGELOG is for users. Update it at
phase milestones (matches the implementation plan's milestone gates), using
plain language:

```markdown
## [0.2.0] - Weather
- Added volumetric clouds with terrain shadow casting
- Sky color now responds to sun angle
### Known issues
- Cloud shadows may over-darken during storms (#14)
```

---

## 4. Structuring work so Claude Code can pick it back up

### Break work into checklist-shaped issues/todos, not vague goals
"Implement reflections" is too large a unit for an agent session to track
progress against reliably. "Add raymarch SSR pass for water, binary
on/off, no roughness input yet" is checkable — Claude Code can look at the
code and know if it's done.

If you're using GitHub Issues, mirror the MVP checklist items 1:1 as
issues, and reference the issue number in the commit that closes it
(`closes #7`). This gives you a paper trail an agent can grep for later:
"why does this function exist" → `git log --grep` → issue link → original
reasoning.

### Leave a breadcrumb when stopping mid-task
If a session ends with a subsystem partially done, commit what's working
and leave a `TODO` comment **with enough context to resume without
re-reading the whole file**:

```csharp
// TODO(pbr): normal map pass done, roughness pass next.
// Approach: box-filter variance per README.md, NOT frequency-domain —
// decided against it for performance, see commit a3f9c2.
```

A bare `// TODO: finish this` forces the next session to reconstruct
context it already had once.

### Prefer small, testable functions over large monolithic passes
Every shader/preprocessing step should be a named, isolable unit
(`GenerateNormalFromLuminance(Texture)`, not one giant
`ProcessAllTheThings()`). This matters more for agentic development than
human development, because an agent re-reading the codebase cold benefits
enormously from function names that describe intent — it's doing semantic
search over your code, and vague names return nothing useful.

---

## 5. Quick-reference checklist

Before ending any Claude Code session on this repo:

- [ ] Working tree committed (nothing risky left uncommitted)
- [ ] Commit messages follow Conventional Commits, explain *why* in the body
- [ ] `CLAUDE.md` updated if a convention or constraint changed
- [ ] Subsystem README updated if that subsystem's behavior changed
- [ ] `docs/STATUS.md` updated — **every commit that changes behaviour**
- [ ] `CHANGELOG.md` updated if a milestone was hit
- [ ] No generated/cached files committed
- [ ] Any partial/unfinished work has a context-rich `TODO` comment
- [ ] New functions have intent-revealing names, not just correct behavior
