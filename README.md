# TermHost

A Windows terminal host for working with several shells at once, with built-in panels
for the Claude Code sessions and git repositories on the machine.

It is a .NET MAUI app. Each terminal is a real shell attached through the Windows
pseudo console (ConPTY) and drawn with [xterm.js](https://xtermjs.org/).

## Features

- **Any number of terminals** in one window, shown as **tabs** or **tiled** in a grid.
  One toggle switches between the two. In the grid, drag the gap between two terminals
  to resize them; double-click a gap to even the sizes out again.
- **Shells:** PowerShell 7, Windows PowerShell, Command Prompt and WSL (the ones found
  on the machine).
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

## Install

Requirements:

- Windows 10 version 1809 or later, or Windows 11, 64-bit.
- The WebView2 runtime. Windows 11 includes it; for Windows 10 see
  <https://developer.microsoft.com/microsoft-edge/webview2/>.
- Optional: [Claude Code](https://claude.com/claude-code) for the Claude panel, and
  `git` on the path for the Git panel.

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

## Use

The toolbar is at the top right. Hover over an icon for its name.

| Icon | What it does |
|---|---|
| Plus | Opens a new terminal: the default terminal, running the startup program. |
| Folder with a plus | Opens a dialog to choose the terminal, the folder and the command for one new terminal. |
| Tabs / grid switch | Switches between tabs and a grid of all terminals. |
| Robot | Opens the Claude panel. A dot on the icon is green while a session is running and yellow while one waits for you. |
| Branch | Opens the Git panel. |
| Gear | Opens the settings. |

**Settings** hold the default terminal, the startup program and the theme. They apply
at once and are remembered. Leave the startup program empty for a plain shell. A fresh
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
window). It switches to the window, not to a tab inside it.

**Git panel:** click a worktree to see its path, upstream, last commit and changed
files. Click a remote to open its web page. Counts on a worktree row: `+` staged,
`~` modified, `?` untracked, `!` in conflict, `↑` ahead of and `↓` behind the upstream.

The Claude and Git panels share one place at the right of the window, so opening one
closes the other.

## Limits

- The Claude panel lists sessions that Claude Code has registered under `~/.claude`.
  A session that has just started appears after a few seconds; one that has ended is
  no longer shown.
- The usage figures are read from the file that the
  [ClaudeCodeStatusLine](https://github.com/daniel3303/ClaudeCodeStatusLine) status line
  saves (`%TEMP%\claude\statusline-usage-cache.json`). Without that status line the panel
  says the limits are not available. The figures are as fresh as the last time a Claude
  session drew its status line.
- The Git panel only lists repositories that have a Claude session running in them.
- With WSL, the startup program is run through `bash`.
- The layout of Claude Code's files is not documented and may change between versions.

## Build from source

Requirements: the .NET 10 SDK with the MAUI Windows workload.

    dotnet workload install maui-windows
    dotnet run --project TermHost.csproj

### Build the installer

Requirements: the [WiX toolset](https://wixtoolset.org/) as a .NET tool.

    dotnet tool install --global wix
    pwsh installer\build.ps1 -Version 1.4.0   # artifacts\TermHost-1.4.0-x64.msi

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
| `installer/` | The WiX source and the script that builds the MSI. |

xterm.js and its fit add-on are included under `Resources/Raw/wwwroot` (MIT licence).
