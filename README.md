# CodeMap

**Static impact analysis for .NET codebases, for developers and AI agents.**

CodeMap scans a .NET solution once and builds an index of its call graph, HTTP entry points, dependency-injection bindings, frontend API calls and git history. You can then ask it, from a terminal or through an AI agent, the questions that text search answers badly:

- *What does changing this method affect?* Which endpoints, background jobs, screens and tests reach it.
- *Where is this feature implemented?* Which code a ticket or a business description points to.
- *Show me the real code and the call path from the API down to here.*

Key properties:

- **Fully offline.** No network calls; no code leaves your machine.
- **Read-only.** It never modifies the solution it scans; everything it writes goes to an output folder you choose.
- **Honest about what it cannot see.** Anything it cannot resolve is recorded in `diagnostics.json` and in each report's *Blind spots* section instead of being guessed.
- **Two ways to use it:** run commands in a terminal and read (or attach) the generated Markdown, or let the agent in VS Code call CodeMap directly through its MCP server, `codemap mcp`.

---

## Contents

- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Building the index](#building-the-index)
- [Using CodeMap from the terminal](#using-codemap-from-the-terminal)
- [Using CodeMap from an AI agent](#using-codemap-from-an-ai-agent)
- [Keeping the index fresh](#keeping-the-index-fresh)
- [Command reference](#command-reference)
- [Index contents](#index-contents)
- [Limitations](#limitations)
- [Troubleshooting](#troubleshooting)
- [Development](#development)
- [Further documentation](#further-documentation)

---

## How it works

CodeMap separates **building** an index, which is slow and done occasionally, from **querying** it, which is instant and done constantly.

**Build pipeline.** `codemap sync` runs these steps in order; each can also be run on its own.

| Step | Reads | Produces |
|---|---|---|
| `scan` | The `.sln`/`.slnx`, through Roslyn | Symbols, call edges, HTTP entry points, DI registrations, diagnostics |
| `scan-git` | `git log` of the repository | Ticket → files history, files that change together (co-change) |
| `scan-fe` | Angular/TypeScript sources, jQuery in `.js` and `.cshtml` | Frontend API calls |
| `link` | Frontend calls and backend endpoints | Which screens call which endpoints |
| `map` | The whole index | `MAP.md`, a human-readable overview capped at 500 lines, plus `modules/*.md` |

**Queries.** `find` and `where` locate code, `impact` lists what a change reaches, and `slice` returns the real source plus the call path. The same engine serves the MCP tools.

**Accuracy model.** Edges carry their provenance: a direct call is a fact; a call through an interface, a virtual override or MediatR is an *expansion*, reported as such. Entry points reached only through an implementation that DI does not bind at that call site are listed separately under *Other possible implementations* and never counted in the risk score.

---

## Requirements

| Requirement | Needed for | Notes |
|---|---|---|
| **.NET SDK 8, 9 or 10** | Building and running CodeMap | Any one is enough. CodeMap targets `net8.0` with `RollForward=Major`, so it runs on runtime 8, 9 or 10. Scanned solutions may target `net8.0`, `net9.0` or `net10.0`. |
| **git** | Cloning, ticket history, staleness checks | |
| **node + npm** | Frontend scanning only | Angular/TypeScript parsing needs `npm install` in the frontend folder. |

```bash
dotnet --list-sdks
```

---

## Installation

```bash
git clone https://github.com/huytt-it/codemap-dotnet.git
cd codemap-dotnet
```

### Option A (recommended): install the `codemap` command

No admin rights, no profile edits. From the cloned folder:

```bash
dotnet pack CodeMap.Cli -c Release
```

```bash
dotnet tool install --global --add-source ./nupkg CodeMap.Cli
```

Open a new terminal and run `codemap`. The tool lives in your user folder (`~/.dotnet/tools`). If the command is not found, that folder is missing from `PATH`; calling `~/.dotnet/tools/codemap` directly works the same way.

### Option B: run the built DLL

Use this when policy blocks `dotnet tool install`:

```bash
dotnet build CodeMap.Cli -c Release
```

Then use `dotnet "<repo>/CodeMap.Cli/bin/Release/net8.0/CodeMap.Cli.dll"` wherever this document says `codemap`.

### Updating

After pulling new code:

```bash
dotnet pack CodeMap.Cli -c Release; dotnet tool update --global --add-source ./nupkg CodeMap.Cli
```

`codemap --help` lists every command; if `codemap mcp` is missing, the installed tool predates the MCP server and needs this update. If an MCP server is running in VS Code, close VS Code first: the running server locks the tool's files.

### Verifying the build (optional)

```bash
dotnet test CodeMap.slnx
```

All tests should pass (some need MSBuild, so allow a minute or two). Each machine is set up independently; nothing depends on another machine's state.

---

## Quick start

### Let an AI agent set it up

Open [setup/SETUP-PROMPT.md](setup/SETUP-PROMPT.md) and paste its prompt block into GitHub Copilot Chat in **Agent** mode (or Claude Code, Cursor). The agent:

1. checks the SDK, builds and installs `codemap`;
2. **asks you** which repositories to index (any number), where the indexes should go, and which folder you open in VS Code;
3. writes the project registry to `~/.codemap/`, checks each repository's ticket convention, and scans;
4. installs the agent instructions at `~/.copilot/instructions/` and adds the MCP server to VS Code's user-level configuration;
5. verifies the result and tells you to restart VS Code.

It never writes into the repositories it indexes unless you allow it case by case, never runs `git clone`, and never edits your shell profile or system `PATH`. The registry, instructions and MCP configuration live outside this repository, so run the prompt once on each machine.

### Set it up by hand

1. **Declare the codebase** in `~/.codemap/codemap.projects.json` (all paths absolute):

   ```json
   {
     "projects": [
       {
         "name": "shop",
         "description": "Order and checkout backend",
         "solution": "D:/Repos/Shop/Shop.sln",
         "output": "D:/CodeMapIndex/Shop",
         "commitLanguage": "en"
       }
     ]
   }
   ```

2. **Build the index:**

   ```bash
   codemap sync --project shop
   ```

3. **Ask a question:**

   ```bash
   codemap find --project shop --query "OrderService.Cancel"
   ```

   ```bash
   codemap impact --project shop --symbol "M:Shop.Orders.OrderService.Cancel(System.Int32)"
   ```

To use it from VS Code's agent, continue with [Using CodeMap from an AI agent](#using-codemap-from-an-ai-agent).

---

## Configuration

CodeMap has two configuration files with unrelated jobs. Use either, both or neither.

| File | Answers | Location |
|---|---|---|
| `codemap.projects.json` | **What** to index and where the output goes | Searched in: `--config`, then the current folder and its parents, then `~/.codemap/` |
| `codemap.config.json` | **How** to read one codebase: its ticket format, DI attribute, frontend layout | Searched upward from the solution, repository and frontend folders |

### Project registry: `codemap.projects.json`

Declare each codebase once; every command then takes `--project <name>` instead of long paths. A template with worked examples is in [setup/codemap.projects.example.json](setup/codemap.projects.example.json).

```json
{
  "projects": [
    {
      "name": "shop",
      "description": "Order and checkout backend",
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

| Key | Required | Meaning |
|---|---|---|
| `name` | Yes | Short handle for `--project`. Case-insensitive, unique. |
| `solution` | Yes | The `.sln` or `.slnx` to scan. |
| `output` | Yes | Output folder. The index goes to `<output>/index`, the overview to `<output>/MAP.md`. |
| `description` | No | What the codebase is; agents read it for context. |
| `repo` | No | Git root. When omitted, git is asked for the root of the repository that holds the solution (the solution's folder if git cannot answer), so a solution deeper in the repository (`src/App.sln`) needs nothing extra. Set it only when git cannot find the root from the solution's folder. |
| `frontend` | No | Angular/TypeScript folder. **Omit the key** when there is none: an empty string resolves to the registry's own folder and scans it. |
| `commitLanguage` | No | Language of commit and ticket text (`ja`, `vi`, `en`…). Agents use it to phrase `where` queries. |

Relative paths resolve against the registry file's folder, not the current directory, so a tree that contains its registry still works after being copied elsewhere. A registry in `~/.codemap/` serves every repository on the machine and needs absolute paths. Run `codemap projects` from a few folders: its `Registry:` line shows which file was found. A copy closer to the current folder always wins over `~/.codemap/`.

### One entry describes one (backend, frontend) pair

`link` matches frontend calls against backend endpoints inside a single index, so an entry has exactly one `solution` and at most one `frontend`. A frontend that calls several backends, or a backend that serves several frontends, needs one entry per pair; the same path may appear in several entries.

```json
{
  "projects": [
    { "name": "shop-orders",  "solution": "D:/Repos/Orders/Orders.sln",   "output": "D:/CodeMapIndex/Orders",  "frontend": "D:/Repos/Shop.Web" },
    { "name": "shop-billing", "solution": "D:/Repos/Billing/Billing.sln", "output": "D:/CodeMapIndex/Billing", "frontend": "D:/Repos/Shop.Web" }
  ]
}
```

Two consequences:

- **Each entry scans independently.** A backend shared by three frontends is scanned three times.
- **`diagnostics.json` is scoped to its pair.** In `shop-orders`, calls aimed at Billing appear under *unmatched frontend calls*. They are outside that pair, not broken links.

### Codebase conventions: `codemap.config.json`

Optional. Without it, defaults apply.

```json
{
  "ticketPattern": "(?:#|TICKET-|BUG-|JIRA-)(\\d{3,6})",
  "diAttribute": "InjectableAttribute",
  "frontendAppDir": "src/app"
}
```

| Key | Set it when |
|---|---|
| `ticketPattern` | Commits reference tickets in another format. The first capture group is the ticket ID, so capture the whole token (`([A-Z][A-Z0-9]*_\d+)`, not `(?:SHO\|ABC)_(\d+)`, which would merge `SHO_12` and `ABC_12`). Remember to write `\\d` in JSON. |
| `diAttribute` | Services are registered by a custom attribute rather than `AddScoped`/`AddSingleton`/`AddTransient`. |
| `frontendAppDir` | The frontend does not use Angular CLI's `src/app` layout. |

The file is found by walking up from the folder each step reads (solution, repository, frontend). Put it at the repository root, or in a parent folder **outside** the repository to apply one convention to every repository below it without adding a file to any of them; the nearest file wins.

> **Check the ticket pattern before trusting `where`.** The default pattern accepts `#123`, which is also how GitHub writes pull requests (`Fix cancel (#1019)`). If most "tickets" are PR numbers, `codemap_status` warns about it. Ticket IDs are read from commit subjects and bodies, and a commit that mentions several tickets counts for each.

### Where each file lives

| File | Typical location | Tracked by git |
|---|---|---|
| Project registry | `~/.codemap/codemap.projects.json` | No |
| Index and `MAP.md` | The `output` folder you choose, e.g. `D:/CodeMapIndex/<name>` | No (keep it outside the repository, or gitignore it) |
| Codebase conventions | `codemap.config.json` at the repository root or a parent folder | Your choice |
| Agent instructions | `~/.copilot/instructions/codemap.instructions.md` | No |
| MCP server entry | VS Code user-level `mcp.json`, or `.vscode/mcp.json` in the repository | User-level: no. Workspace: your choice |
| Agent permissions (terminal workflow only) | `codemap.permissions.json` next to the registry | No |

---

## Building the index

### With `sync` (recommended)

```bash
codemap sync --project shop
```

```bash
codemap sync --all
```

`sync` runs `scan` → `scan-git` → `scan-fe` → `link` → `map` for the project (or for every project). It skips `scan-git` when there is no git repository (the `repo` folder has no `.git`, or, with `repo` omitted, git finds none around the solution), and the frontend steps when there is no `frontend`; `sync-complete.json` and `codemap projects` show which steps were skipped. It builds the new index in a staging folder next to the output and swaps it in only after `scan` and `map` succeed, so a failed run leaves the previous index untouched. The result carries `index/sync-complete.json`, which records when and from which commit it was built and which steps ran or were skipped. `codemap projects` shows each project's index status.

Nothing is written into the scanned repositories: the scan reads source and runs read-only git commands, and all output goes to `output` (plus a lock file and a staging folder beside it).

### Step by step, without a registry

Every step works with explicit paths. With `--index` instead of `--project`, run query commands from inside the repository: the current folder is used for the staleness check and for `slice`'s source files.

```bash
codemap scan --solution D:/Repos/MyApp/MyApp.sln --out D:/CodeMapIndex/MyApp
```

```bash
codemap scan-git --repo D:/Repos/MyApp --out D:/CodeMapIndex/MyApp
```

```bash
codemap scan-fe --root D:/Repos/MyApp.Web --out D:/CodeMapIndex/MyApp
```

```bash
codemap link --index D:/CodeMapIndex/MyApp/index
```

```bash
codemap map --index D:/CodeMapIndex/MyApp/index --out D:/CodeMapIndex/MyApp
```

Notes:

- **`scan` failing:** run `dotnet restore` in the repository first. If the repository has both `.sln` and `.slnx`, name one (`dotnet restore MyApp.sln`) to avoid MSB1011. As a fallback, `--syntax-only` scans without building: calls, entry points and DI are still recorded, but calls into NuGet and framework types cannot be resolved. Projects that fail to build are scanned shallowly and reported as *degraded*; the scan carries on.
- **Run `scan-git` after `scan`.** Git reports paths from the repository root, the scan from the solution folder; `scan-git` reads `meta.json` from the scan to reconcile them when the solution sits deeper (`src/MyApp.sln`). `--since 2024-01-01` limits the history read.
- **"No ticket ID matched":** the repository's convention differs from the default; set `ticketPattern` (see above). Co-change data is still written, the command exits with code 2, and only `ticket-files.jsonl` is skipped. Under the default pattern, the PR number in `Merge pull request #456 from …` is not taken as a ticket.
- **`scan-fe`:** Angular parsing needs `node_modules/typescript` in the frontend folder; without it only jQuery is scanned, and the command says so. jQuery calls are read from `.js` files and `<script>` blocks in `.cshtml`; vendored folders (`node_modules`, `dist`, `wwwroot/lib`) are skipped. URLs built with Razor (`@Url.Action(...)`) are listed in `diagnostics.json` rather than guessed.
- **`map` runs last,** so that entry points in `MAP.md` list their linked screens.

---

## Using CodeMap from the terminal

Every query takes `--project <name>` (or `--index <dir>`). Add `--out <file.md>` to `impact` or `slice` to write a file you can open or attach to a chat.

### Is this change risky?

Find the symbol's identifier (a documentation ID such as `M:Namespace.Type.Method(Args)`), then ask for its impact:

```bash
codemap find --project shop --query "OrderService.Cancel"
```

```bash
codemap impact --project shop --symbol "M:Shop.Orders.OrderService.Cancel(System.Int32)" --out impact.md
```

The report lists the entry points that reach the symbol (HTTP endpoints, background services, MediatR handlers), the frontend screens behind those endpoints, the tests that reach it, a risk score, past tickets on its file, files that usually change with it, and blind spots. `--depth` sets how many caller levels to follow (default 5); `--full` adds every intermediate caller and expands hub summaries.

### Where is this feature or ticket?

```bash
codemap where --project shop --query "注文のキャンセル"
```

`where` ranks candidates and prints **why** each was picked. Its strongest signal is past ticket messages, so phrase the query in the team's commit language (`commitLanguage`). To look up a ticket, write it the way commits do (`#4821`, `TICKET-4821`, `SHO-1234`); a bare number is not treated as a ticket. Feed the best `M:…` identifier to `impact` or `slice`.

### Show me the code and the call path

```bash
codemap slice --project shop --symbol "M:Shop.Orders.OrderService.Cancel(System.Int32)" --out slice.md
```

`slice` reads the source **from disk at run time**, so the code is current even when the index is not. It shows up to 40 lines per member (`--max-lines` for more), the call path from entry points down (`--depth`, default 3), and related tickets. If the symbol could only be re-located by name because its signature changed, the output says so.

| | `impact` | `slice` |
|---|---|---|
| Answers | Is this safe to touch? | What exactly does it touch? |
| Content | A compact list, readable in seconds | Real code, call path and past tickets |
| Use it | Before deciding | After deciding to dig in |

### Reading a report

- **Freshness first.** Every report opens with how far the index is behind `HEAD`, including uncommitted edits to the files that matter (see [Keeping the index fresh](#keeping-the-index-fresh)).
- **"0 recognized" is not "no impact".** It means no caller is an entry point *kind* CodeMap recognizes (see [Limitations](#limitations)); read *Intermediate callers*. When callers exist but none is an entry point or a test, the risk line says `not scored — N unclassified caller(s)` instead of `0/10`.
- **Inferred items are labelled.** `route inferred from convention` (no route attribute; `{controller}/{action}/{id?}` assumed), `(matched by route suffix only)` (the frontend URL starts with a base-URL variable), `(ambiguous match)`, and anything under *Other possible implementations* are inferences, not facts.
- **Fan-in and risk.** *Direct fan-in* counts distinct calling methods. The risk score and the hub warning count only bindings confirmed by real DI registrations.
- **Blind spots are mandatory reading.** If a question falls into one, the report cannot answer it.
- **Git data is history.** Tickets and co-change describe the past and may name deleted or renamed files; treat them as leads, not structure.

---

## Using CodeMap from an AI agent

### MCP server for VS Code

`codemap mcp` is a read-only [Model Context Protocol](https://modelcontextprotocol.io) server over stdio. The agent calls CodeMap itself instead of printing commands for you to run.

| Tool | Answers |
|---|---|
| `codemap_impact` | What changing a method, class or property affects. Same report as `codemap impact`, opening with a one-line freshness header. Accepts a docId or a `Type.Member` name. |
| `codemap_status` | Whether CodeMap covers the open workspace, how fresh the index is, how it was built, and the exact `codemap sync` command when it needs rebuilding. |

**Setup** (the projects must already be declared and synced):

1. **Register the server.** Either in the repository's `.vscode/mcp.json`, which can be committed for the whole team:

   ```json
   {
     "servers": {
       "codemap": {
         "type": "stdio",
         "command": "codemap",
         "args": ["mcp", "--workspace", "${workspaceFolder}"]
       }
     }
   }
   ```

   or, to add nothing to the repository, in VS Code's user-level configuration (command **MCP: Open User Configuration**) with absolute paths:

   ```json
   {
     "servers": {
       "codemap": {
         "type": "stdio",
         "command": "codemap",
         "args": ["mcp", "--config", "C:/Users/me/.codemap/codemap.projects.json", "--workspace", "D:/Repos"]
       }
     }
   }
   ```

   Use absolute paths here rather than `${workspaceFolder}`. If the workspace folder holds several indexed projects, the first call in a chat answers `AMBIGUOUS_PROJECT` with the list and the agent retries with `project=<name>`. If you open a different folder each time, drop `--workspace`. With Option B, use `"command": "dotnet"` and put the DLL path first in `args`.

2. **Install the agent instructions** (next section). They tell the agent to look for the `codemap_*` tools and use them; tool descriptions alone were not enough when an agent had older terminal-workflow instructions.

3. **Quit VS Code completely and reopen it**, then ask normally: *"What does changing `OrderService.Cancel` affect?"*

**How the server picks the project:** the tool's `project` argument if the agent passes one; otherwise the `--workspace` folder; otherwise the folder VS Code starts it in. That folder is matched against the registry (found via `--config`, then the folder and its parents, then `~/.codemap/`): inside a solution folder first, then inside a repository, inside a frontend, and finally a parent folder of a repository. No match yields `NOT_INDEXED` with the known projects; `codemap_status` explains what was tried.

**Behaviour to expect** (VS Code 1.142 with the Copilot CLI agent):

- The server starts after the first chat message; pressing *Start* in the editor before that reports an error.
- The agent discovers MCP tools through tool search, which is why the instructions name the tools.
- Read-only tools run without an approval prompt.
- Instruction files and MCP configuration are read when VS Code starts the agent. After changing them, quit VS Code completely; *Reload Window* and a new chat keep the old ones.
- The server reloads an index on its own when `codemap sync` finishes, and keeps answering from the previous copy (with a warning) if a new one cannot be loaded.
- A running server locks the files it runs from. With Option B pointing into your clone, rebuilding that configuration fails until VS Code is closed.

### Agent instructions

[setup/codemap.instructions.md](setup/codemap.instructions.md) teaches an agent how to use CodeMap and how to read its reports. Its first section sends the agent to the `codemap_*` tools when they exist; otherwise it falls back to the terminal workflow.

Install it once per machine:

```bash
mkdir -p ~/.copilot/instructions
cp setup/codemap.instructions.md ~/.copilot/instructions/codemap.instructions.md
```

This user-level location applies to every workspace and is read by VS Code without any setting. A per-repository copy at `<repo>/.github/instructions/codemap.instructions.md` also works, but adds a file to that repository. Instruction files stack, so use one location, and **remove older CodeMap instruction copies** (for example a `.github/copilot-instructions.md` made from an earlier version): an outdated copy kept the agent printing commands even with the tools available. The *Language* section assumes Japanese commits; adjust it if `commitLanguage` differs.

### Terminal workflow (agents without MCP)

Without the MCP server, the agent prints a `codemap` command for you to run and reads the output you paste back. To let it run the read-only commands (`find`, `where`, `impact`, `slice`, `projects`) itself, copy [setup/codemap.permissions.json](setup/codemap.permissions.json) next to your registry. This is a convention the instructions ask the agent to follow, not an enforcement mechanism; [setup/README.md](setup/README.md) (Task 3) shows how to enforce it in Claude Code. The MCP server does not use this file.

---

## Keeping the index fresh

The index is a snapshot; it does not update itself. Every report says how stale it is:

```
current HEAD b7e1d04 · 11 commit(s) behind, 6 relevant file(s) changed since the scan
```

Uncommitted edits count too (`matches the scan, but 2 relevant file(s) have uncommitted changes`, or `(2 uncommitted)`). A report about one symbol counts only the files its answer depends on, so unrelated work in progress does not make it look stale. Through MCP, the same facts appear as one line, e.g. `codemap · shop · HEAD 4da8212 · index fresh`.

When the numbers grow, rebuild with `codemap sync --project <name>` (or `--all`). A running MCP server picks up the new index automatically.

**Adding a frontend later.** Add `frontend` to the entry and run `sync` again, or, when the backend has not changed, run only the missing steps: `scan-fe`, then `link`, then `map`.

**Notes in `MAP.md`.** Text between `<!-- human:start -->` and `<!-- human:end -->` is preserved across rebuilds.

---

## Command reference

`<index>` means `--project <name>` (resolved through the registry) or `--index <dir>`. Every command accepts `--verbose` to print the full stack trace on an unexpected error.

| Command | Purpose | Options |
|---|---|---|
| `codemap projects` | List registered projects, resolved paths and index status | `[--config <file>]` |
| `codemap sync` | Build or rebuild indexes with the full pipeline | `--project <name>` or `--all`, `[--config <file>]` |
| `codemap scan` | Roslyn scan of a solution | `--solution <sln/slnx> --out <dir> [--syntax-only] [--include-external]` |
| `codemap scan-git` | Ticket history and co-change from git | `--repo <path> --out <dir> [--since <yyyy-mm-dd>]` |
| `codemap scan-fe` | Frontend API calls (Angular/TypeScript, jQuery) | `--root <dir> --out <dir>` |
| `codemap link` | Match frontend calls to backend endpoints | `<index>` |
| `codemap map` | Generate `MAP.md` and `modules/*.md` | `<index> --out <dir>` |
| `codemap find` | Find symbols by name; prints docIds | `<index> --query <text>` |
| `codemap where` | Rank code for a business description or ticket ID | `<index> --query <text>` |
| `codemap impact` | What a change to a symbol reaches | `<index> --symbol <docId> [--depth 5] [--full] [--out <file>]` |
| `codemap slice` | Current source and call path of a symbol | `<index> --symbol <docId> [--depth 3] [--max-lines 40] [--out <file>]` |
| `codemap mcp` | MCP server over stdio | `[--config <file>] [--workspace <dir>]` |

`--include-external` makes `scan` emit edges to symbols outside the solution (framework, NuGet) instead of only logging them to diagnostics.

---

## Index contents

`<output>/index/` holds JSON and JSON Lines files, readable by any tool:

| File | Contents |
|---|---|
| `symbols.jsonl` | Types and members, with file and line |
| `edges.jsonl` | Call edges, with provenance (`via`: interface, override, MediatR, delegate) |
| `entrypoints.json` | HTTP endpoints, background services, MediatR handlers |
| `di.json`, `di-confirmed.json` | DI registrations; the confirmed file keeps only real bindings |
| `diagnostics.json` | Everything the scan could not resolve, and degraded projects |
| `meta.json` | Scan time, commit, solution path, counts |
| `ticket-files.jsonl` | Ticket → commits and files |
| `co-change.jsonl` | File pairs that change together, with strength |
| `frontend-calls.jsonl`, `api-links.jsonl` | Frontend calls and their matched endpoints |
| `sync-complete.json` | Written by `sync` when a run completes: run ID, commit, steps |

`<output>/MAP.md` and `<output>/modules/*.md` are the human-readable overview.

---

## Limitations

- **Entry points recognized:** ASP.NET Core MVC and Web API controllers, `BackgroundService`/`IHostedService` implementations, and MediatR handlers. Razor Pages handlers, Minimal API endpoints and Blazor components are not recognized, so impact on such apps often reports "0 recognized" with unclassified callers.
- **Outside static analysis:** reflection, assembly scanning, stored procedures and SQL, runtime logic and feature flags, environment-specific configuration, database contents, and Razor view ↔ view model relationships.
- **Expansions can over-approximate.** Calls through interfaces and virtual methods are expanded to every implementation or override; at run time usually one is taken. Confirmed DI bindings are separated from the rest.
- **Frontend linking** covers Angular/TypeScript and jQuery, within one backend–frontend pair per entry. URLs assembled at run time cannot always be matched.
- **The index is a snapshot.** It reflects the commit it was built from; `slice` reads current source, but every other answer is as fresh as the last `sync`.
- **Text is not indexed.** Comments, string literals and configuration values are not in the index; use text search for those.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| A project is reported as *degraded* | It did not build, so it was scanned shallowly; the scan carried on. The reason is in `index/diagnostics.json`. |
| `impact` shows 0 entry points or `not scored` | Raise `--depth`, check *Intermediate callers*, or the callers are an entry point kind CodeMap does not recognize. A symbol nothing calls is a leaf. |
| `slice` cannot re-locate a symbol | It was renamed or removed since the scan. Run `sync`, then `find` for the current docId. |
| `where` finds nothing | Retry in the team's commit language, write ticket IDs as commits do, or use `find` with a likely symbol name. |
| `scan-git`: "No ticket ID matched" | Set `ticketPattern` in `codemap.config.json`. |
| `scan-fe`: "typescript package not found" | Run `npm install` in the frontend folder. |
| `dotnet restore` fails with MSB1011 | The folder has both `.sln` and `.slnx`; name one explicitly. |
| The agent prints `codemap` commands instead of calling the tools | Install the current instructions, remove older copies, and restart VS Code completely. |
| A tool answers `NOT_INDEXED` | The workspace matches no registry entry. Check `--workspace`/`--config` in the MCP entry, or let the agent pass `project=<name>`. `codemap_status` shows what was tried. |
| Build fails with MSB3027 "file is locked by .NET Host" | An MCP server is running from that build. Close VS Code, or build another configuration. |

CodeMap's rule is to report what it does not know rather than guess. When a report says it cannot tell, it genuinely cannot.

---

## Development

```bash
dotnet build CodeMap.slnx
```

```bash
dotnet test CodeMap.slnx
```

| Project | Role |
|---|---|
| `CodeMap.Cli` | The `codemap` entry point, `sync` and `slice` |
| `CodeMap.Roslyn` | `scan`: solution loading (MSBuildWorkspace, MSBuildLocator) and the semantic and syntax-only scanners |
| `CodeMap.Query` | Index model and every command that needs no Roslyn: `scan-git`, `scan-fe`, `link`, `map`, `find`, `where`, `impact`, `projects` |
| `CodeMap.Mcp` | The MCP server: project resolution, index cache and tools |
| `tests/CodeMap.Tests` | MSTest suite, including tests that start the real MCP server over stdio |

The MCP server uses stdout exclusively for the protocol, so nothing in `CodeMap.Mcp` may write to the console; diagnostics go to stderr.

---

## Further documentation

- [setup/README.md](setup/README.md): the configuration surface, step by step, including enforcing agent permissions.
- [setup/SETUP-PROMPT.md](setup/SETUP-PROMPT.md): the prompt for AI-assisted setup.
- [setup/codemap.instructions.md](setup/codemap.instructions.md): the agent instructions.
- [docs/FEATURES.md](docs/FEATURES.md): a detailed feature list. Where it disagrees with this README, this README is current.
