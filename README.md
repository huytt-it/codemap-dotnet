# CodeMap

A CLI tool that scans a .NET codebase and produces static files (`.md`, `.jsonl`) to attach to an AI chat (GitHub Copilot), answering the question: *"what does changing this affect?"*

- Runs **fully offline** — no network calls, no code ever leaves your machine.
- **Read-only** — it never modifies files in the solution it scans.
- Not an MCP server — you run commands in a terminal, then attach the output files to a chat yourself.

---

## Part 1 — Setup (one time, identical on every machine)

### Prerequisites

| Requirement | Needed? | Purpose |
|---|---|---|
| **.NET SDK 8, 9, or 10** | Required | Builds and runs the tool — **any one of the three is enough**, .NET 8 specifically is not required |
| **git** | Required | Clones this repo, and reads ticket history / staleness warnings when the tool runs |
| **node + npm** | Only for frontend scanning | Reads API calls in Angular/TypeScript files |

Quick check:

```bash
dotnet --list-sdks
```

> The tool builds against `net8.0` but sets `RollForward=Major`, so it **runs on runtime 8, 9, or 10**. You do not need to install .NET 8 just to use it. The same applies to the codebase you scan — `net8.0`, `net9.0`, and `net10.0` targets are all supported (verified against real projects).

### Get the source

```bash
git clone https://github.com/huytt-it/codemap-dotnet.git
cd codemap-dotnet
```

### Install — pick one of two options

#### Option A (recommended): install a real `codemap` command

No admin rights, no PowerShell profile edits, no alias. Run two commands from the cloned directory:

```bash
dotnet pack CodeMap.Cli -c Release
```

```bash
dotnet tool install --global --add-source ./nupkg CodeMap.Cli
```

Done. Open a new terminal and type `codemap` from anywhere. The tool installs into your user directory (`~/.dotnet/tools`) and does not touch the system.

To update after pulling new code:

```bash
dotnet pack CodeMap.Cli -c Release; dotnet tool update --global --add-source ./nupkg CodeMap.Cli
```

> **If `codemap` reports "command not found"**: `~/.dotnet/tools` is not on PATH (rare — the .NET SDK installer usually adds it automatically). Calling it by full path, `"$HOME/.dotnet/tools/codemap"`, works the same way.

#### Option B: no install, call the dll directly

Use this when local policy blocks `dotnet tool install`:

```bash
dotnet build CodeMap.Cli -c Release
```

Then replace every `codemap` in this document with `dotnet "<repo-path>\CodeMap.Cli\bin\Release\net8.0\CodeMap.Cli.dll"`, where `<repo-path>` is the directory you cloned into.

### Verify the install (recommended on a new machine)

```bash
dotnet test tests/CodeMap.Tests
```

All tests should pass. If they fail right after a fresh clone, check `dotnet --list-sdks` before assuming something else is wrong.

Setting up on another machine, or having a colleague clone the repo, means repeating Part 1 from the top — no step depends on the previous machine's state.

### Prefer not to read all this? Have an AI do it

Clone this repo (the "Get the source" step above), then open [setup/SETUP-PROMPT.md](setup/SETUP-PROMPT.md) and paste the prompt block it contains into Copilot, Claude Code, or Cursor. It will detect your SDK, choose an install method that matches what your machine's policy allows, declare `codemap.projects.json`, decide which commands it may run on its own in `codemap.permissions.json`, place `codemap.instructions.md` so an agent picks it up automatically, and run the scan — the same four tasks described in [setup/README.md](setup/README.md). The prompt deliberately **forbids the agent from running `git clone` itself** and from editing your PowerShell profile or system PATH: you clone, then hand it the path.

> **Setting up a second machine:** `codemap.projects.json`, `codemap.permissions.json`, and `codemap.instructions.md` (when placed at `~/.copilot/instructions/`) normally live at `~/.codemap/` and `~/.copilot/instructions/` — **outside this git repo**. Cloning the repo again on a new machine does not bring them along; repeat this AI-driven setup (or copy those files over by hand) on each machine separately. The one exception is `codemap.instructions.md` placed per-repo under `<target-repo>/.github/instructions/` (Option B in [setup/README.md](setup/README.md)) — that copy is committed into the target repo, so it arrives automatically with `git clone` of that repo.

---

## Part 2 — Declare projects, then scan (recommended)

Instead of remembering four absolute paths per repo, declare each codebase **once** in `codemap.projects.json`; every command after that just needs `--project <name>`.

### Step 1 — Create `codemap.projects.json`

A template with three worked examples is provided in [setup/](setup/) — copy it and fill it in:

```bash
cp setup/codemap.projects.example.json setup/codemap.projects.json
```

It can live anywhere: inside `setup/`, at a repo root, in a shared workspace directory, or at `~/.codemap/`. The tool searches in this order: `--config` → the current directory, then its parent directories → `~/.codemap/`.

```json
{
  "description": "Codebases I'm indexing",
  "projects": [
    {
      "name": "shop",
      "description": "Order backend — Razor Pages + PublicApi",
      "solution": "D:/Repos/Shop/Shop.sln",
      "output": "D:/CodeMapIndex/Shop",
      "frontend": "D:/Repos/Shop.Web",
      "commitLanguage": "ja"
    },
    {
      "name": "billing",
      "description": "Standalone billing service",
      "solution": "D:/Repos/Billing/Billing.slnx",
      "output": "D:/CodeMapIndex/Billing",
      "commitLanguage": "en"
    }
  ]
}
```

| Key | Required? | Meaning |
|---|---|---|
| `name` | Required | Short name used with `--project`. Case-insensitive, must be unique. |
| `solution` | Required | The `.sln` or `.slnx` file to scan. |
| `output` | Required | Output directory. **The index lives at `<output>/index`**, `MAP.md` at `<output>/MAP.md`. |
| `description` | — | A description for people (and AI) to understand what this codebase is. |
| `repo` | — | Git root. Defaults to the solution's own directory if left blank. |
| `frontend` | — | Angular/TypeScript directory. **Omit the key entirely** when there is no separate frontend — an empty string is not the same thing, it resolves to the directory holding `codemap.projects.json` and scans that. |
| `commitLanguage` | — | Language the team writes commits in (`ja`/`vi`/`en`/...). Tells an AI agent which language to phrase `where` queries in. |

> Paths can be **relative** — resolved against the location of `codemap.projects.json` itself, not against your current working directory. That means the whole directory tree still works after being copied to another machine.

### One entry describes one (backend, frontend) pair

`link` matches frontend API calls against backend endpoints **inside a single index**, so an entry carries exactly one `solution` and one `frontend`. A frontend that calls several backends, or a backend serving several frontends, is declared as one entry per pair — the same path simply appears in more than one entry:

```json
{
  "projects": [
    {
      "name": "shop-orders",
      "solution": "D:/Repos/OrdersService/Orders.sln",
      "output": "D:/CodeMapIndex/Orders",
      "frontend": "D:/Repos/Shop.Web"
    },
    {
      "name": "shop-billing",
      "solution": "D:/Repos/BillingService/Billing.sln",
      "output": "D:/CodeMapIndex/Billing",
      "frontend": "D:/Repos/Shop.Web"
    }
  ]
}
```

Two consequences to know before doing this:

- **Each entry scans independently.** A backend shared by three frontends is scanned three times — there is no shared cache between entries. Scan time grows linearly with the number of pairs, not with the number of distinct repos.
- **`diagnostics.json` is scoped to its own pair.** In `shop-orders`, every frontend call aimed at Billing appears under "unmatched frontend calls". Those calls are outside the pair being indexed, not broken links. Read that count per pair; it is not a repo-wide health metric.

### Step 2 — Scan

```bash
codemap sync --project shop
```

One command runs `scan` → `scan-git` → `scan-fe` → `link` → `map`, in the order their data dependencies require. To scan every declared project at once:

```bash
codemap sync --all
```

If `scan` fails, that project's run stops there — a half-built index is worse than none. `scan-git` and `scan-fe` are optional enrichment: a repo with no git history, or no separate frontend, still produces a usable `MAP.md`.

### Step 3 — Check

```bash
codemap projects
```

Lists every project, its resolved paths, and **index status**: whether it has been built, how many symbols, when it was last scanned, and how many days ago.

### Day-to-day use

```bash
codemap where --project shop --query "注文のキャンセル"
```

Every query command (`find`, `where`, `impact`, `slice`, `map`, `link`) accepts `--project <name>` in place of a long `--index <path>`.

---

## Part 2b — Manual scanning, without the config file

Everything still works without creating `codemap.projects.json`.

Assume your repo is at `D:\Repos\MyApp`, with solution `D:\Repos\MyApp\MyApp.sln`.

**Important:** always `cd` into the repo before running a command — the tool uses the current directory to check whether the index is stale.

```bash
cd D:\Repos\MyApp
```

### Step 1 — Scan the backend (required)

```bash
codemap scan --solution MyApp.sln --out D:\CodeMapIndex\MyApp
```

`--solution` accepts **both `.sln` and `.slnx`** (the XML format the .NET 10 SDK generates by default).

> **If this step fails:** try `dotnet restore` in the repo first. If it still fails, add `--syntax-only` — a shallower scan that does not require the solution to build, at the cost of less detail.
>
> If the repo has **both `.sln` and `.slnx`** (common mid-migration), a bare `dotnet restore` fails with MSB1011 because it cannot pick one — name it explicitly: `dotnet restore MyApp.sln`.

### Step 2 — Scan git history (recommended)

```bash
codemap scan-git --repo . --out D:\CodeMapIndex\MyApp
```

> **Run this after Step 1, not before.** `git log` reports paths from the repository root, while the scan records them from the solution's directory. When the solution sits deeper in the repo (`src/MyApp.sln`), `scan-git` reads `meta.json` from Step 1 to reconcile the two — without it, no ticket or co-change entry can match a scanned file, which costs `where` its strongest ranking signal. The command says which reconciliation it applied, and warns if nothing matched.

> **If it reports "No ticket ID matched":** your repo names commits differently from the default convention (`#1234`, `TICKET-1234`, `BUG-1234`, `JIRA-1234`). Create a `codemap.config.json` at the repo root — see [Part 5](#part-5--configuration-optional).

### Step 3 — Scan the frontend (skip if there is no separate frontend)

```bash
codemap scan-fe --root D:\Repos\MyApp.Web --out D:\CodeMapIndex\MyApp
```

```bash
codemap link --index D:\CodeMapIndex\MyApp\index
```

> **Note:** the frontend directory must already have `npm install` run (needs `node_modules/typescript`). If not, the tool still runs but skips Angular, scanning jQuery only — and says so clearly on screen.

### Step 4 — Generate the overview map

```bash
codemap map --index D:\CodeMapIndex\MyApp\index --out D:\CodeMapIndex\MyApp
```

Open `D:\CodeMapIndex\MyApp\MAP.md` to see the result. It is human-readable and capped at 500 lines.

---

## Part 3 — Day-to-day use

### Scenario A: "I'm about to change this method — is it risky?"

**Step 1 — find the method's identifier** (nobody types this by hand):

```bash
codemap find --index D:\CodeMapIndex\MyApp\index --query "OrderService.Cancel"
```

Copy the `M:...` line from the result.

**Step 2 — check the impact:**

```bash
codemap impact --index D:\CodeMapIndex\MyApp\index --symbol "M:Orders.OrderService.Cancel(System.Int32)" --out impact.md
```

Open `impact.md`, or **attach it directly to a Copilot chat** and ask normally.

### Scenario B: "The ticket says 'fix order cancellation' — where is that in the code?"

```bash
codemap where --index D:\CodeMapIndex\MyApp\index --query "hủy đơn hàng"
```

Returns a list of candidates **with the reason each was picked**. Take the matching `M:...` identifier and feed it to `impact` as above.

### Scenario C: "I need the real code, plus the path from the API to here"

```bash
codemap slice --index D:\CodeMapIndex\MyApp\index --symbol "M:Orders.OrderService.Cancel(System.Int32)" --out slice.md
```

`slice` reads code **directly from disk at run time**, so even if the index was scanned yesterday, the code in the output file is current.

### `impact` vs. `slice`

| | `impact` | `slice` |
|---|---|---|
| Answers | "Is this safe to touch?" | "What exactly does it touch?" |
| Content | A compact list, readable in ten seconds | Real code included, plus past tickets |
| When to use | Before deciding | After deciding to dig in |

---

## Part 4 — Refreshing the index

The tool does **not** update itself. As code changes, the index gets stale.

That is expected — every generated `.md` file carries a warning banner at the top, of the form:

```
current HEAD b7e1d04 · 11 commit(s) behind, 6 relevant file(s) changed since the scan
```

Once that number grows, scan again: `codemap sync --project <name>` (or `--all`). It overwrites the old output directory safely. Without a config file, repeat the commands in [Part 2b](#part-2b--manual-scanning-without-the-config-file).

### Adding a frontend to a project that was already scanned

If a project was declared without `frontend` and scanned, adding the key later does not require discarding the existing index. Add it to that entry in `codemap.projects.json`, then either re-run the whole pipeline:

```bash
codemap sync --project shop
```

or, when the backend scan is slow and the backend itself has not changed, run only the steps that were skipped:

```bash
codemap scan-fe --root D:/Repos/Shop.Web --out D:/CodeMapIndex/Shop
```

```bash
codemap link --index D:/CodeMapIndex/Shop/index
```

```bash
codemap map --index D:/CodeMapIndex/Shop/index --out D:/CodeMapIndex/Shop
```

`map` must run last: entry points in `MAP.md` only list their linked frontend screens after `link` has written `api-links.jsonl`.

> Your handwritten notes in `MAP.md` (between `<!-- human:start -->` and `<!-- human:end -->`) are **always preserved** on re-scan. Add notes there freely.

For automated nightly scans (currently **disabled**, run by hand): see `docs/OPS-NIGHTLY-SCAN.md` — an internal document, not included in the public repo (see the "Further documentation" section below).

---

## Part 5 — Configuration (optional)

There are **two** config files, entirely unrelated — do not confuse them:

| File | Location | Answers |
|---|---|---|
| `codemap.projects.json` | Wherever you choose (a workspace, or `~/.codemap/`) | **What to scan, where output goes** — see [Part 2](#part-2--declare-projects-then-scan-recommended) |
| `codemap.config.json` | **Root of the repo being scanned** | **How to scan** — that repo's own conventions (below) |

A repo with unusual conventions needs the second file; scanning several repos at once needs the first. They are independent — use either, both, or neither.

### `codemap.config.json` — a repo's own conventions

Create this at the **root of the repo being scanned**, if needed. Without it, the tool falls back to sensible defaults and still runs normally.

```json
{
  "ticketPattern": "(?:#|TICKET-|BUG-|JIRA-)(\\d{3,6})",
  "diAttribute": "InjectableAttribute",
  "frontendAppDir": "src/app"
}
```

| Key | When it's needed |
|---|---|
| `ticketPattern` | The team's commit convention uses a different ticket format (e.g. `ABC-123`) |
| `diAttribute` | The team marks DI with a custom attribute instead of `AddScoped`/`AddSingleton` |
| `frontendAppDir` | The frontend does not follow Angular CLI's standard `src/app/` layout |

---

## Part 6 — Troubleshooting

| Symptom | What to do |
|---|---|
| `scan` reports a project as "degraded" | Normal — that project failed to build, so the tool falls back to a shallower scan and **keeps going**. Check the reason in `index\diagnostics.json`. |
| `impact` returns 0 entry points | Increase `--depth` (default 5). Or the method genuinely has no callers. Or the entry point is a kind the tool doesn't yet recognize (see the limitations in [FEATURES.md](docs/FEATURES.md)). |
| `slice` reports "Could not re-locate this symbol" | The symbol was renamed or removed since the last scan. Re-scan, then run `find` again to get the current identifier. |
| `where` returns nothing | The tool reports "not found" honestly rather than guessing. Retry in the language the team actually writes commits/tickets in (`where`'s strongest signal is matching past ticket messages), or try `find` with an English term if you already have a guess at the symbol name. |
| `scan-fe` reports "typescript package not found" | Run `npm install` in the frontend directory first. |

**The tool's general principle:** anything it cannot analyze goes into `diagnostics.json` and the "Blind spots" section of the report — **it never guesses silently**. If a report says it doesn't know, that means it genuinely doesn't — don't disregard that.

---

## For AI agents to read (GitHub Copilot, Claude, ...)

[setup/codemap.instructions.md](setup/codemap.instructions.md) is the full instruction set for an AI agent: the question-and-answer workflow, how to read a report, which language to use for `where`, and what is off-limits. It contains no hardcoded paths — it reads `codemap.projects.json` itself to find the index.

Place this file at **`~/.copilot/instructions/`** (applies to every project on your machine — the better choice if you scan multiple repos and add or remove them over time) or at **`<target-repo>/.github/instructions/`** (per-repo, shareable via git). Both are read automatically by VS Code, no setting changes needed. See "Task 4" in [setup/README.md](setup/README.md) for details on choosing between them.

By default, an agent **does not run `codemap` itself** — it prints the command and you run it. To let an agent run the read-only commands (`find`/`where`/`impact`/`slice`) on its own, configure [setup/codemap.permissions.json](setup/codemap.permissions.json) — see "Task 3" in [setup/README.md](setup/README.md).

## Further documentation

- **[setup/](setup/)** — everything you need to fill in, in one place: the `codemap.projects.json` template, the AI setup prompt, and the agent instructions file. See [setup/README.md](setup/README.md).
- [docs/FEATURES.md](docs/FEATURES.md) — what the tool can and cannot do (worth reading before trusting its output)
- `docs/CODEMAP-SPEC.md`, `docs/OPS-NIGHTLY-SCAN.md`, `docs/TEST-REPORT-PHASE*.md` — internal documents (design spec, nightly-scan operations, phase-by-phase test reports). **Not included in the public repo** — these exist only in the original working copy and have not been pushed.
