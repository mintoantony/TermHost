---
name: termhost-task
description: Use when started as `/termhost-task plan=<file or JIRA key> task=<id> state=<folder>` in a worktree that the termhost-plan skill prepared. Carries out exactly one task of a plan in this worktree, following the project's CLAUDE.md and workflow, finishes it the way the project finishes work (by default commit, push and a pull request), and records the outcome for the planner.
---

# Do one task of a plan

You are a **worker**. Other Claude sessions are working on other tasks of the same plan, in other worktrees,
at the same time. You do exactly one task, in this worktree, on this branch.

## Arguments

| Key | Meaning |
|---|---|
| `plan` | The plan file's absolute path, or a Jira issue key |
| `task` | The task's id: its number in the plan, or the Jira key of the child issue |
| `state` | The planner's progress folder; your file is `<state>/<task>.json` |

## Steps

Track these as todos.

### 1. Check where you are

`git rev-parse --show-toplevel` must be this worktree and `git branch --show-current` its task branch. If the
working tree is not clean before you start, stop and ask: someone else may be using it.

### 2. Read the project's rules

Read `CLAUDE.md` (and `AGENTS.md`, `.claude/CLAUDE.md`) **in this worktree** and follow them for the whole
task: coding standards, required skills and process (TDD, brainstorming, reviews, issue claiming), how to
build and test, and how to finish. Where they and this skill differ, **they win**.

### 3. Read the task

- **File plan:** read the whole plan for context, then find task `<task>`. Its text is your specification.
- **Jira:** fetch the issue `<task>` (and the parent `<plan>` for context) with the Atlassian connector.

Work only on this task. Do not start other tasks of the plan, even small ones. If the task cannot be done
without changing files that the plan gives to another task, or it depends on work not yet merged, stop:
set your status to `blocked` with the reason (step 6) and tell the user.

### 4. Do the work

Follow the task's steps and the project's workflow. Run the project's build and tests before you call it
finished, and report their real results.

### 5. Finish the way the project finishes work

If `CLAUDE.md` describes how work is finished, do exactly that. Otherwise:

1. Commit on this branch with a message that names the plan and the task.
2. `git push -u origin HEAD`.
3. If `gh` is available and the remote is on GitHub, open a pull request against the branch this one was
   made from (`gh pr create --base <base> --fill`, with the task's title and a summary; for Jira, put the
   issue key in the title). Note its address.
4. For Jira, add the pull request to the issue and move it to review if the project's workflow has such a
   status.

Do not merge, and do not remove the worktree or branch.

### 6. Record the outcome

Update `<state>/<task>.json`: keep the other fields, and set

- `status`: `done` when finished, `failed` when the build or tests fail and you could not fix them,
  `blocked` when step 3 stopped you;
- `pr`: the pull request's address, if any;
- `note`: one line on what happened;
- `updated`: the current time, ISO 8601, UTC.

Create the file if it does not exist. Only ever write your own task's file.

### 7. Report

Tell the user in a few lines what was done, the test results, the pull request, and anything left open.
Then stop. The planner's "next batch" will pick up the next tasks.
