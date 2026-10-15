---
name: termhost-plan
description: Use when asked to run a plan's tasks in parallel in TermHost, or when started as `/termhost-plan plan=<file or JIRA key> tasks=<n>` from TermHost's Run a plan dialog. Loads a Markdown plan from the repository or a Jira issue, picks up to n open tasks that can run at the same time, asks the user, then creates a branch and worktree for each and opens each in its own TermHost terminal running Claude with the termhost-task skill.
---

# Run a plan's tasks in parallel

You are the **planner**. You do not implement any task yourself. You choose tasks, prepare a branch and
worktree for each, and open one TermHost terminal per task, in which a separate Claude session does the
work with the `termhost-task` skill.

## Arguments

Given as `key=value`, values possibly in single quotes:

| Key | Meaning | Default |
|---|---|---|
| `plan` | A Markdown file, relative to the repository root, **or** a Jira issue key such as `PROJ-123` | required |
| `tasks` | How many tasks to run at the same time | 4 |
| `shell` | The TermHost terminal for the workers, e.g. `PowerShell 7`, `Git Bash` | TermHost's default |
| `mode` | Claude permission mode for the workers: `default`, `acceptEdits`, `auto`, `plan` | none (Claude's default) |

If `plan` is missing, ask for it. Do nothing else until you have it.

## Steps

Track these as todos.

### 1. Read the project's rules

Read `CLAUDE.md` (and `AGENTS.md`, and `.claude/CLAUDE.md` if present) at the repository root. Note
anything about:

- branch names, the base branch, worktree location;
- how work is finished: commits, pull requests, reviews, Jira transitions;
- required skills or process (for example TDD, a review step, claiming an issue before starting).

**These rules override every default in this skill.** Mention to the user which ones you will apply.

### 2. Load the plan and its tasks

**Markdown file.** Read the whole file. Tasks are, in order of preference:

1. headings like `## Task 3: ...` or `### Task 3 ...` (the superpowers plan format);
2. otherwise numbered top-level sections or a numbered list of work items;
3. otherwise top-level checkbox items `- [ ] ...`.

Give each task an **id**: its number (`3`), or for checkboxes its position. Keep for each: title, full
text, the files it names, and any dependency it states ("after Task 2", "depends on", "requires").

**Jira issue.** Use the Atlassian connector (Jira tools). Fetch the issue; its tasks are its child issues
(epic children, or subtasks). The id is the child's key. Dependencies are "is blocked by" links. If the
Jira tools are not available, say so and stop: do not guess the tasks.

If no tasks can be recognised, say so, show what the plan looks like, and stop before creating anything.

### 3. Work out each task's status

The progress folder is `<git common dir>/termhost/<plan-slug>/`, where `<git common dir>` is the output of
`git rev-parse --path-format=absolute --git-common-dir` and `<plan-slug>` is the plan file's name without
`.md`, or the Jira key, in lower case. It holds one `<task-id>.json` per task that has been started:

```json
{ "task": "3", "title": "Add login API", "status": "running", "branch": "task/auth-t3-login-api",
  "worktree": "D:\\repo\\.worktrees\\task-auth-t3-login-api", "pr": null, "note": null, "updated": "2026-10-15T10:00:00Z" }
```

`status` is one of `running`, `done`, `failed`, `blocked`. A task is:

- **done** if its file says `done`, the plan marks it done (`[x]`, `Status: done`, `✅`), or its Jira
  issue is in a done category;
- **running** if its file says `running`, or its branch or worktree already exists;
- **open** otherwise (`failed` and `blocked` tasks count as open, but say why they stopped).

### 4. Choose the tasks to run now

Take up to `tasks` open tasks, in plan order, such that:

- every task it depends on is **done** (not merely running);
- no two chosen tasks — and no chosen task and a running one — name the same files or clearly change the
  same area. When the plan does not name files, judge from the text; when unsure, leave the later task out;
- nothing in the plan says the tasks must run in sequence.

If fewer than `tasks` qualify, take fewer and say what the others wait for.

### 5. Ask the user — always

Show a table and stop until the user answers:

| Task | Branch | Worktree | Why it can run now |
|---|---|---|---|

Below it list the open tasks not chosen and why. Say which `CLAUDE.md` rules apply. The user may say
go, drop or swap tasks, or change the number. Do not create anything before they say go.

### 6. Create the branches and worktrees

Unless `CLAUDE.md` says otherwise:

- Base: the branch the repository is on now. Run `git fetch` first; if the base has an upstream and is
  behind it, tell the user and ask before going on.
- Branch: `task/<plan-slug>-t<id>-<short-slug>` for a file plan, `<JIRA-KEY>-<short-slug>` for Jira. Lower
  case, `a-z0-9-`, at most 50 characters.
- Worktree: `<repo root>/.worktrees/<branch name with / replaced by ->`. Inside the repository, so the
  workers are covered by the trust the user already gave this folder and Claude does not stop each one to
  ask again. Keep the folder out of git without a commit: add the line `.worktrees/` to
  `<git common dir>/info/exclude` if it is not there yet (unless `.gitignore` already ignores it).

```bash
git worktree add -b "<branch>" "<worktree>" "<base>"
```

If the plan file is not committed on the base, the worktrees will not contain it. That is fine: workers
read the plan from its absolute path in this checkout.

Then write the task's progress file with `status: running`. For Jira, move the issue to In Progress if the
project's workflow uses that status (and claim or assign it if `CLAUDE.md` asks for that).

### 7. Open a terminal for each task

TermHost's `th` command opens a tab in the TermHost window. It is on the PATH of every TermHost terminal; if
it is not found, use the path in the `TERMHOST_TH` environment variable. Run, for each task, from bash:

```bash
th "<worktree>" --shell "<shell>" --title "T<id> <short title>" \
   --command "claude <--permission-mode MODE> \"/termhost-task plan='<plan>' task=<id> state='<progress folder>'\""
```

- `<plan>` is the plan file's **absolute** path in this checkout, or the Jira key.
- Leave out `--shell` when no shell was given, and `--permission-mode MODE` when no mode was given.
- Paths use forward slashes inside the command (`D:/repo/docs/plan.md`); none may contain `'`, `"`, `$`,
  `` ` ``, `%` or `!`. If one does, tell the user and print the command instead of running it.
- `th` exits 0 when the tab opened. Otherwise show its message, mark that task `failed` with the reason,
  and go on with the others.

If `th` cannot be found at all, print the commands for the user to run and stop.

### 8. Report and stay available

Summarise: which tasks started, in which tabs, branches and worktrees, and what is left. Tell the user they
can say **"next batch"** here once tasks finish: then repeat from step 3. **"status"** re-reads the progress
files and shows the table again.

Never merge, delete worktrees or branches, or close issues unless the user asks.
