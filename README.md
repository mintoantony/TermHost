# TermHost

A Windows terminal host for working with several shells at once, with built-in panels
for the Claude Code sessions and git repositories on the machine.

It is a .NET MAUI app. Each terminal is a real shell attached through the Windows
pseudo console (ConPTY) and drawn with [xterm.js](https://xtermjs.org/).

## Features

- **Any number of terminals** in one window, shown as **tabs**, **tiled** in a grid, or
  side by side in a **single row**. One control switches between the three. In the grid
  and the row, drag the gap between two terminals to resize them; double-click a gap to
  even the sizes out again.
- **Shells:** PowerShell 7, Windows PowerShell, Command Prompt, WSL and Git Bash (the
  ones found on the machine).
- **Startup program:** a command that runs in every new terminal, such as `claude`,
  `copilot` or a script. The shell stays open when the program ends.
- **Themes:** Catppuccin Mocha, Tokyo Night, Dracula, Rosé Pine, Nord, Gruvbox, One Dark
  and Catppuccin Latte (light). A theme recolours the terminals, the toolbar, the panels
  and the window title bar.
- **Claude panel:** every Claude Code session running on the machine, with its status
  (running, waiting, idle), model, token count, sub-agent tree and recent activity.
  Clicking a session that runs in one of this window's terminals jumps to that terminal.
  At the top it shows the plan's usage: how much of the 5-hour and 7-day windows is used
  and when each resets.
- **Git panel:** for each repository that has a Claude session: its remotes, and for
  every worktree the branch, distance from its upstream, changed files and last commit.
- **GitHub panel:** for the same repositories: the open pull requests and open issues,
  each a link to its page, with a filter for your own.

## Install

Requirements:

- Windows 10 version 1809 or later, or Windows 11, 64-bit.
- The WebView2 runtime. Windows 11 includes it; for Windows 10 see
  <https://developer.microsoft.com/microsoft-edge/webview2/>.
- Optional: [Claude Code](https://claude.com/claude-code) for the Claude panel, and
  `git` on the path for the Git panel, and the [GitHub CLI](https://cli.github.com/)
  (`gh`), signed in with `gh auth login`, for the GitHub panel.

Download the `.msi` file from the
[latest release](https://github.com/mintoantony/TermHost/releases/latest) and run it.
You can also build the installer yourself: see [Build the installer](#build-the-installer).

- It installs for the current user, into `%LOCALAPPDATA%\Programs\TermHost`, and adds a
  Start menu shortcut. No administrator rights are needed.
- It adds **Open in TermHost** to the right-click menu of folders and drives in File
  Explorer. On Windows 11 this is under **Show more options** (or hold Shift while you
  right-click).
- .NET and the Windows App SDK are included, so nothing else has to be installed.
- The installer is not signed, so Windows may show a SmartScreen warning.
- To remove it, use **Settings > Apps > Installed apps**. Your settings are kept.
- To update, run the newer installer: it replaces the installed version. TermHost asks
  GitHub once, each time it starts, what the latest release is, and tells you when there
  is a newer one. Nothing else is sent.

## Use

The toolbar is at the top right. Hover over an icon for its name.

| Icon | What it does |
|---|---|
| Plus | Opens a new terminal: the default terminal, running the startup program. |
| Folder with a plus | Opens a dialog to choose the terminal, the folder and the command for one new terminal. |
| Tabs / tiles / row | Chooses the layout: tabs, a grid of all terminals, or all terminals side by side in a single row. In the grid and the row, drag the gaps to resize. |
| Robot | Opens the Claude panel. A dot on the icon is green while a session is running and yellow while one waits for you. |
| Branch | Opens the Git panel. |
| GitHub mark | Opens the GitHub panel. |
| Gear | Opens the settings. |
| Question mark | Shows the version, with links to this README and the GitHub repository. A green dot on the icon means a newer version is available; the dialog then has a button that downloads its installer. |

**Settings** hold the default terminal, the startup program, the window transparency and
the theme. They apply at once and are remembered. With the transparency above zero, the
desktop shows blurred through the window's background (the title bar, the toolbar and the
gaps between terminals); terminals, panels and dialogs stay solid. Leave the startup program empty for a plain shell. A fresh
install starts with `claude` as the startup program.

**Folders:** new terminals start in your home folder. When you start TermHost with
**Open in TermHost** from a folder's right-click menu, the terminals of that window start
in that folder instead. To start one terminal somewhere else, use the folder button: type
the folder or pick it with **Browse…**. The dialog remembers the last folder; it does not
change the settings.

**Claude panel:** click a session to expand it. A session that runs in one of this
window's terminals shows that terminal's name; clicking it also switches to the
terminal. A session that runs anywhere else is labelled **External**; opening it also
brings the window it runs in to the front, when that window can be found (Windows
Terminal, Visual Studio Code or another TermHost window, but not a classic console
window). It switches to the window, not to a tab inside it. A session with Remote
Control on shows a **Remote Control** link that opens it in the browser.

**Git panel:** click a worktree to see its path, upstream, last commit and its files:
the uncommitted ones, and the ones in commits not yet pushed. Click a file to open its
diff in the diff tool git is set up with (`git config diff.tool`). Click the path to
open it in File Explorer. Click a remote to open its web page.
Counts on a worktree row: `+` staged, `~` modified, `?` untracked, `!` in conflict,
`↑` ahead of and `↓` behind the upstream.

**GitHub panel:** click a pull request or an issue to open it in the browser. **All
open** lists every open one; **Only mine** keeps the pull requests you opened or are
assigned to and the issues assigned to you, for the account `gh` is signed in with. The
choice is remembered. A draft pull request is marked **Draft**. Click a repository's name to collapse it, and **Pull requests** or **Issues**
to collapse that list; the counts stay in view. The lists are read again every minute
while the panel is open.

The Claude, Git and GitHub panels share one place at the right of the window, so opening
one closes the others.

## Limits

- The Claude panel lists sessions that Claude Code has registered under `~/.claude`.
  A session that has just started appears after a few seconds; one that has ended is
  no longer shown.
- The usage figures are read from the file that the
  [ClaudeCodeStatusLine](https://github.com/daniel3303/ClaudeCodeStatusLine) status line
  saves (`%TEMP%\claude\statusline-usage-cache.json`). Without that status line the panel
  says the limits are not available. The figures are as fresh as the last time a Claude
  session drew its status line.
- The Git and GitHub panels only list repositories that have a Claude session running in
  them.
- The GitHub panel shows the newest 50 open pull requests and the newest 50 open issues
  of a repository. Without `gh`, or when it is not signed in, the panel says so.
- With WSL, the startup program is run through `bash`.
- The layout of Claude Code's files is not documented and may change between versions.

## Build from source

Requirements: the .NET 10 SDK with the MAUI Windows workload.

    dotnet workload install maui-windows
    dotnet run --project TermHost.csproj

### Build the installer

Requirements: the [WiX toolset](https://wixtoolset.org/) as a .NET tool.

    dotnet tool install --global wix
    pwsh installer\build.ps1 -Version 1.10.0   # artifacts\TermHost-1.10.0-x64.msi

The script publishes a self-contained build to `artifacts\publish` and wraps it in an
MSI. A newer version replaces an installed older one.

## How it is put together

| File | Role |
|---|---|
| `ConPtySession.cs` | One shell process attached to a pseudo console. |
| `MainPage.xaml.cs` | Starts terminals, keeps the settings and passes messages between the shells and the web view. |
| `Resources/Raw/wwwroot/index.html` | The whole user interface: toolbar, tabs, tiles, panels, settings and themes. |
| `ClaudeStatus.cs` | Reads the Claude Code sessions, their sub-agents and activity. Ported from the Claude Mission Control dashboard. |
| `GitDetails.cs` | Reads remotes, worktrees and status with `git`. |
| `GitHubItems.cs` | Reads open pull requests and issues with `gh`. |
| `installer/` | The WiX source and the script that builds the MSI. |

xterm.js and its fit add-on are included under `Resources/Raw/wwwroot` (MIT licence).
