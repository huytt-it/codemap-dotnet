# setup/ — everything you need to fill in, in one place

The files in this directory are the entire "configuration" surface of CodeMap. Clone the repo, complete the four tasks below, and it is ready to use.

| File | What to do with it |
|---|---|
| `codemap.projects.example.json` | **Copy it to `codemap.projects.json` and fill it in** — declares which repos to scan and where the index goes |
| `codemap.permissions.json` | Edit directly — which `codemap` subcommands an AI agent may **run on its own**, and which require your confirmation |
| `SETUP-PROMPT.md` | Copy the prompt block and paste it into an AI to have it run the setup for you (optional) |
| `codemap.instructions.md` | Copy to one of two locations — see "Task 4" below |

---

## Task 1 — Declare your projects

**Decide first: one project, or many?** This decision determines where the file goes, and carries through to Task 4 (the registry and the instructions file must use the same scope) — decide once, and do not switch back and forth.

### Many projects, added or removed over time (recommended if you scan several repos)

Create the file **directly** at `~/.codemap/codemap.projects.json` — do not copy it into `setup/` first and plan to move it later; that step gets forgotten:

```bash
mkdir -p ~/.codemap
cp setup/codemap.projects.example.json ~/.codemap/codemap.projects.json
```

(On Windows, the real path is `C:\Users\<your-name>\.codemap\codemap.projects.json`.) This is the last place the tool checks if no closer copy exists. If a stray copy is left behind — in `setup/` inside the CodeMap clone itself, or at the root of a target repo — the copy nearest the current directory **always takes priority and hides the one at `~/.codemap/`**, and the two can quietly drift apart over time. Because this file now lives outside every repo, **every path inside it must be absolute** — relative paths will not resolve correctly.

### Just one project

```bash
cp setup/codemap.projects.example.json setup/codemap.projects.json
```

No shadowing risk here, since only one copy exists — placing it in `setup/` or at the target repo's root both work.

### Filling it in (applies to either approach)

Open the file you just created, **delete all three example entries**, and add your own:

| Key | Required? | Meaning |
|---|---|---|
| `name` | Required | Short name used with `--project <name>`. Case-insensitive, must be unique. |
| `solution` | Required | The `.sln` or `.slnx` file to scan |
| `output` | Required | Output directory. **The index lives at `<output>/index`**, `MAP.md` at `<output>/MAP.md` |
| `description` | — | What this codebase is — an AI agent reads this for context |
| `repo` | — | Git root. Defaults to the solution's own directory if left blank |
| `frontend` | — | Angular/TypeScript directory. **Omit this key entirely** if there is no separate frontend |
| `commitLanguage` | — | Language the team writes commits in (`ja`/`vi`/`en`/...) — an AI agent reads this to decide which language to use for `where` |

> The `codemap.projects.json` you fill in **is gitignored** (whether it lives in `setup/` or at `~/.codemap/`, it is not part of the Git repo to commit), so internal company paths never leak into it accidentally. To share a configuration with a colleague, edit the `.example.json` file instead of the real one.

The tool searches in this order: `--config <path>` → `codemap.projects.json` in the current directory, then its parent directories → `~/.codemap/codemap.projects.json`.

Once it's filled in, run `codemap projects` **from a few different directories** (the CodeMap repo root, a target repo's root...) — the `Registry: ...` line at the top of the output should always point to the one file you created. If it ever points somewhere else, an old `codemap.projects.json` is still sitting closer to the current directory — find and remove it.

If placed in `setup/`, check it with:

```bash
codemap projects --config setup/codemap.projects.json
```

This prints the resolved paths and the index status for each project — a wrong path is obvious immediately. If placed at `~/.codemap/`, drop `--config` — the tool finds it on its own.

## Task 2 — Scan

```bash
codemap sync --all
```

(Add `--config setup/codemap.projects.json` if you placed the file inside `setup/`.) Runs `scan` → `scan-git` → `scan-fe` → `link` → `map` for every declared project, in order. To scan a single project instead of all of them, use `--project <name>` in place of `--all`.

## Task 3 — Decide which commands an AI agent may run on its own

By default, an AI agent **does not run `codemap` itself** — it prints the command, you paste it into a terminal, then paste the output back for it to read. Safe, but slow.

Open `setup/codemap.permissions.json` and change `"autoRun"` for each subcommand:

```json
"where": { "autoRun": true, "reason": "read-only against the index" }
```

Preconfigured defaults: `find` / `where` / `impact` / `slice` / `projects` (read-only, with no side effect beyond writing a single report file when `--out` is given) are set to `true`; `scan` / `scan-fe` / `scan-git` / `sync` / `map` / `link` (rewrite the index, can be slow) are set to `false`. Change these however you like — nothing here is fixed.

> An agent looks for this file **in the same place as `codemap.projects.json`** (target repo root / a parent directory / `~/.codemap/`), not inside `.github/`. Wherever you placed `codemap.projects.json` in Task 1, put `codemap.permissions.json` there too — don't split them across two locations.

**This is a soft convention** — an agent follows it because `codemap.instructions.md` tells it to, the same way it follows every other rule in that file (how to read "Blind spots", how to write out a symbol name, and so on). It is not an operating-system-level restriction, and it does not distinguish between different AI tools reading it.

### Hard enforcement (optional, Claude Code only)

To make this a real restriction — one an agent cannot bypass even if it "forgets" to read the file — add this to `.claude/settings.json` (or `.claude/settings.local.json`) in the target repo:

```json
{
  "permissions": {
    "allow": [
      "Bash(codemap find:*)",
      "Bash(codemap where:*)",
      "Bash(codemap impact:*)",
      "Bash(codemap slice:*)",
      "Bash(codemap projects:*)"
    ]
  }
}
```

This is Claude Code's own permission mechanism — it runs exactly the listed commands without asking again, while everything else still requires confirmation. Copilot Chat and Cursor each have their own equivalent (usually called "auto-approve" for the terminal), under a setting name that varies by version — check that tool's documentation if you want hard enforcement rather than relying on `codemap.permissions.json` alone.

## Task 4 — Make it readable by an AI agent

`codemap.instructions.md` uses the `.instructions.md` extension — VS Code's "path-specific instructions" convention (read by GitHub Copilot Chat, and in a similar way by Claude Code and Cursor), which differs from `copilot-instructions.md`, a fixed filename reserved for a single file at the root of each repo. Using this extension means the name does not collide with any other instructions a team already has, and it can be placed in one of two locations depending on your needs:

### Option A — set up once, applies to every project (recommended if you scan several repos and add or remove them over time)

```bash
mkdir -p ~/.copilot/instructions
cp setup/codemap.instructions.md ~/.copilot/instructions/codemap.instructions.md
```

On Windows, `~` is `C:\Users\<your-name>\` — the real path is
`C:\Users\<your-name>\.copilot\instructions\codemap.instructions.md`.

This is a **user-level location that VS Code enables by default** (setting `chat.instructionsFilesLocations`) — confirmed directly from the VS Code source, not assumed. A file here applies to **every workspace you open on this machine**, regardless of which project is the current root, and needs no re-copying when a new project is added to `codemap.projects.json`. Do this **once**, and it stays correct no matter how many projects get added later.

Trade-off: it only takes effect on your machine — a colleague who clones the repo does not get it automatically.

### Option B — per repo, shareable via git

```bash
mkdir -p <target-repo-path>/.github/instructions
cp setup/codemap.instructions.md <target-repo-path>/.github/instructions/codemap.instructions.md
```

Commit this file into the target repo — a colleague who clones it has it immediately, with no separate setup. It is still automatic, with no setting to change, **but it only takes effect when the workspace root is exactly that repo** (or that repo is one of the root folders in a multi-root workspace) — opening a parent directory that contains several repos means an agent will not see this file. If you choose Option B for more than one repo, repeat this step for each of them.

### After copying (applies to either option)

The file **contains no hardcoded paths** — it reads `codemap.projects.json` on its own to find the index, and `codemap.permissions.json` to know which commands it may run. Three things worth checking afterward:

- The **"Language"** section is written for a codebase with Japanese commits. If your `commitLanguage` differs, update it to match.
- Confirm an agent can actually **find** `codemap.projects.json` (following the search order from Task 1) — if you chose Option A (many projects), placing `codemap.projects.json` at `~/.codemap/` is the natural choice, since that's visible from every workspace, matching the spirit of Option A.
- If you did not create `codemap.permissions.json`, an agent defaults to treating every command as needing confirmation first — safe, and nothing further to do.

> **Why not use `copilot-instructions.md`**: that name is reserved by GitHub Copilot for a single file at the workspace root — if you (or your team) already use that name for something else, copying CodeMap's version over it would overwrite the existing content. The name `codemap.instructions.md` lets both files exist side by side, each handling its own concern.

> **Choose only one of the two options** for a given repo, not both. Unlike `codemap.projects.json` (where the closer copy hides the farther one), `.instructions.md` files **stack** — if a repo has both a copy at `~/.copilot/instructions/` and one at `.github/instructions/`, an agent receives both at once, and the duplicated content is sent to the model twice. That costs more; it does not produce a wrong answer.

---

## Prefer not to do this yourself? Have an AI do it

Open [SETUP-PROMPT.md](SETUP-PROMPT.md), copy the prompt block it contains, and paste it into Copilot Chat (Agent mode), Claude Code, or Cursor. It detects your SDK, chooses an install method that matches what your machine's policy allows, asks you for paths, and completes all four tasks above.

The prompt deliberately **forbids the agent from running `git clone`** and **forbids editing the PowerShell profile or system PATH** — you keep control over where the source comes from, and nothing runs into corporate machine policy restrictions.
