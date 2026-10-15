using System.Text.RegularExpressions;

namespace TermHost;

// "Run a plan": the plans a repository has, and the planner session that runs one.
// TermHost only starts the planner; the termhost-plan skill does the rest, opening the workers with th.
static partial class PlanRun
{
	public static readonly string[] Modes = ["", "default", "acceptEdits", "auto", "plan"];

	// Makes th reachable from every terminal, so the planner can open the workers, and puts the
	// skills that come with TermHost where Claude Code finds them.
	public static void Prepare(Version version)
	{
		var th = Path.Combine(AppContext.BaseDirectory, "th.exe");
		if (File.Exists(th))
		{
			Environment.SetEnvironmentVariable("TERMHOST_TH", th);
			Environment.SetEnvironmentVariable("PATH", AppContext.BaseDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
		}
		try
		{
			InstallSkills(version);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Without them Run a plan cannot work, but everything else can.
		}
	}

	// Copies skills\<name> beside the executable to ~/.claude/skills/<name>, once per version of
	// TermHost, so that changes made to the copies by hand last until the next update.
	static void InstallSkills(Version version)
	{
		var shipped = Path.Combine(AppContext.BaseDirectory, "skills");
		if (!Directory.Exists(shipped))
			return;
		var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");
		foreach (var skill in Directory.GetDirectories(shipped))
		{
			var to = Path.Combine(target, Path.GetFileName(skill));
			var marker = Path.Combine(to, ".termhost-version");
			if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version.ToString())
				continue;
			foreach (var file in Directory.GetFiles(skill, "*", SearchOption.AllDirectories))
			{
				var copy = Path.Combine(to, Path.GetRelativePath(skill, file));
				Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
				File.Copy(file, copy, overwrite: true);
			}
			File.WriteAllText(marker, version.ToString());
		}
	}

	// The top folder of the git repository at or above folder, or null when it is not in one.
	public static string? RepoRoot(string folder)
	{
		if (!Directory.Exists(folder))
			return null;
		var top = ClaudeStatus.RunGit(folder, "rev-parse", "--show-toplevel")?.Trim();
		return string.IsNullOrEmpty(top) ? null : Path.GetFullPath(top);
	}

	// The repository's Markdown files, committed or new, but none that git ignores.
	public static List<string> Plans(string root) =>
		(ClaudeStatus.RunGit(root, "ls-files", "--cached", "--others", "--exclude-standard", "--", "*.md") ?? "")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct()
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();

	// The command the planner tab runs, or why it cannot be made. Values are kept free of the
	// characters that a shell would take for its own, since the command passes through one.
	public static string Command(string root, string source, string plan, int tasks, string shell, string mode, out string? error)
	{
		error = null;
		if (source == "jira")
		{
			// PROJ-123, or an issue's address, from which the key is taken.
			var key = JiraKey().Match(plan);
			if (!key.Success)
				error = "Give a Jira issue key such as PROJ-123, or the issue's address.";
			plan = key.Value;
		}
		else
		{
			var file = Path.GetFullPath(Path.Combine(root, plan));
			if (plan.Length == 0 || !file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
				error = "Pick a Markdown file of the repository.";
			plan = Path.GetRelativePath(root, file).Replace('\\', '/');
		}
		if (tasks is < 1 or > 8)
			error ??= "Run from 1 to 8 tasks at once.";
		if (!Modes.Contains(mode))
			error ??= "Unknown permission mode.";
		if (UnsafeText().IsMatch(plan) || UnsafeText().IsMatch(shell))
			error ??= "The plan's name has a character that cannot be passed on: ' \" ` $ % ! ^ & | < >";
		if (!OnPath("claude.exe") && !OnPath("claude.cmd"))
			error ??= "Claude Code was not found on the path.";
		var permission = mode.Length == 0 ? "" : $"--permission-mode {mode} ";
		var args = $"plan='{plan}' tasks={tasks} shell='{shell}'" + (mode.Length == 0 ? "" : $" mode={mode}");
		return $"claude {permission}\"/termhost-plan {args}\"";
	}

	static bool OnPath(string exe) =>
		(Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Any(dir =>
			{
				try { return File.Exists(Path.Combine(dir.Trim(), exe)); }
				catch (ArgumentException) { return false; }
			});

	[GeneratedRegex(@"\b[A-Z][A-Z0-9_]+-\d+\b")]
	private static partial Regex JiraKey();

	[GeneratedRegex(@"['""`$%!^&|<>]")]
	private static partial Regex UnsafeText();
}
