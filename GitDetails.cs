using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TermHost;

/// <summary>
/// What the Git panel shows of a repository: its remotes and, for each worktree, the
/// branch, how far it is from its upstream, the changed files and the last commit.
/// Use from one thread only: the cache is not locked.
/// </summary>
sealed partial class GitDetails
{
	const int MaxFiles = 60;
	static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(10);

	sealed record Remote(string Name, string Display, string? Web);

	sealed record Change(string Code, string Path);

	sealed class Tree
	{
		public string Path = "";
		public string? Branch, Upstream, CommitHash, CommitSubject;
		public long? CommitAt;
		public int Ahead, Behind, Staged, Modified, Untracked, Conflicts;
		public List<Change> Files = [];
	}

	sealed record Repo(List<Remote> Remotes, List<Tree> Trees);

	readonly Dictionary<string, (DateTime Expires, Repo Repo)> _cache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>One repository as JSON.</summary>
	/// <param name="sessions">The worktree directory and the key of each Claude session in this repository.</param>
	public JsonObject Read(string root, string name, IReadOnlyCollection<(string? Top, string Key)> sessions)
	{
		var now = DateTime.UtcNow;
		if (!_cache.TryGetValue(root, out var entry) || now >= entry.Expires)
		{
			entry = (now + CacheTime, Query(root));
			_cache[root] = entry;
		}

		var trees = new JsonArray();
		foreach (var tree in entry.Repo.Trees)
			trees.Add(new JsonObject
			{
				["path"] = tree.Path,
				["branch"] = tree.Branch,
				["main"] = string.Equals(tree.Path, root, StringComparison.OrdinalIgnoreCase),
				["upstream"] = tree.Upstream,
				["ahead"] = tree.Ahead,
				["behind"] = tree.Behind,
				["staged"] = tree.Staged,
				["modified"] = tree.Modified,
				["untracked"] = tree.Untracked,
				["conflicts"] = tree.Conflicts,
				["files"] = new JsonArray(tree.Files.Select(file => (JsonNode)new JsonObject { ["code"] = file.Code, ["path"] = file.Path }).ToArray()),
				["commit"] = tree.CommitHash is null ? null : new JsonObject { ["hash"] = tree.CommitHash, ["subject"] = tree.CommitSubject, ["at"] = tree.CommitAt },
				["sessions"] = new JsonArray(sessions.Where(s => string.Equals(s.Top, tree.Path, StringComparison.OrdinalIgnoreCase))
					.Select(s => (JsonNode)s.Key).ToArray()),
			});
		return new JsonObject
		{
			["repo"] = name,
			["root"] = root,
			["remotes"] = new JsonArray(entry.Repo.Remotes.Select(remote =>
				(JsonNode)new JsonObject { ["name"] = remote.Name, ["url"] = remote.Display, ["web"] = remote.Web }).ToArray()),
			["trees"] = trees,
		};
	}

	static Repo Query(string root)
	{
		var remotes = new List<Remote>();
		foreach (var line in Lines(ClaudeStatus.RunGit(root, "remote", "-v")))
		{
			// origin<TAB>https://host/owner/repo.git (fetch)
			var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 3 && parts[2] == "(fetch)")
			{
				var (display, web) = Describe(parts[1]);
				remotes.Add(new Remote(parts[0], display, web));
			}
		}

		var trees = new List<Tree>();
		Tree? current = null;
		foreach (var line in Lines(ClaudeStatus.RunGit(root, "worktree", "list", "--porcelain")))
		{
			if (line.StartsWith("worktree ", StringComparison.Ordinal))
				trees.Add(current = new Tree { Path = Path.GetFullPath(line["worktree ".Length..]) });
			else if (current is not null && line.StartsWith("HEAD ", StringComparison.Ordinal))
				current.Branch = "@" + line["HEAD ".Length..][..Math.Min(7, line.Length - "HEAD ".Length)]; // until a branch line says otherwise
			else if (current is not null && line.StartsWith("branch ", StringComparison.Ordinal))
				current.Branch = line["branch ".Length..].Replace("refs/heads/", "");
		}
		foreach (var tree in trees)
		{
			ReadStatus(tree);
			ReadCommit(tree);
		}
		return new Repo(remotes, trees);
	}

	// `git status --porcelain=v2 --branch`: "# branch.*" header lines, then one line per path.
	static void ReadStatus(Tree tree)
	{
		// --no-optional-locks: looking must not get in the way of a git command the user is running.
		foreach (var line in Lines(ClaudeStatus.RunGit(tree.Path, "--no-optional-locks", "status", "--porcelain=v2", "--branch")))
		{
			if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
				tree.Upstream = line["# branch.upstream ".Length..];
			else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
			{
				var counts = line["# branch.ab ".Length..].Split(' ');
				if (counts.Length == 2)
				{
					int.TryParse(counts[0].TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture, out tree.Ahead);
					int.TryParse(counts[1].TrimStart('-'), NumberStyles.None, CultureInfo.InvariantCulture, out tree.Behind);
				}
			}
			else if (line.StartsWith("? ", StringComparison.Ordinal))
			{
				tree.Untracked++;
				Add(tree, "?", line[2..]);
			}
			else if (line.StartsWith("u ", StringComparison.Ordinal))
			{
				tree.Conflicts++;
				Add(tree, "U", Field(line, 10));
			}
			else if (line.StartsWith("1 ", StringComparison.Ordinal) || line.StartsWith("2 ", StringComparison.Ordinal))
			{
				// XY: X is the staged change, Y the unstaged one; "." means none.
				var xy = line.Length >= 4 ? line.Substring(2, 2) : "..";
				if (xy[0] != '.')
					tree.Staged++;
				if (xy[1] != '.')
					tree.Modified++;
				// A rename carries "<new path><TAB><old path>".
				var path = Field(line, line[0] == '1' ? 8 : 9).Split('\t')[0];
				Add(tree, xy, path);
			}
		}
	}

	static void Add(Tree tree, string code, string path)
	{
		if (tree.Files.Count < MaxFiles)
			tree.Files.Add(new Change(code, path));
	}

	/// <summary>The rest of a line after its first `index` space-separated fields.</summary>
	static string Field(string line, int index)
	{
		var parts = line.Split(' ', index + 1);
		return parts.Length > index ? parts[index] : "";
	}

	static void ReadCommit(Tree tree)
	{
		var parts = ClaudeStatus.RunGit(tree.Path, "log", "-1", "--format=%h%x1f%s%x1f%ct")?.Trim().Split('\x1f');
		if (parts is not { Length: 3 })
			return;
		tree.CommitHash = parts[0];
		tree.CommitSubject = parts[1].Length > 120 ? parts[1][..120] : parts[1];
		if (long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
			tree.CommitAt = seconds * 1000;
	}

	[GeneratedRegex(@"^(?:[^@/\\]+@)?([^:/\\]{2,}):(?!//)(.+)$")]
	private static partial Regex ScpLikeUrl();

	/// <summary>
	/// A remote URL as "host/owner/repo" and, when it has one, its web page. A URL can
	/// carry a user name or a token: neither is ever part of what is returned.
	/// </summary>
	static (string Display, string? Web) Describe(string url)
	{
		static string Clean(string path)
		{
			path = path.Trim('/');
			return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
		}

		// git@host:owner/repo.git
		if (ScpLikeUrl().Match(url) is { Success: true } scp)
		{
			var where = $"{scp.Groups[1].Value}/{Clean(scp.Groups[2].Value)}";
			return (where, "https://" + where);
		}
		if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" or "git")
		{
			var path = Clean(uri.AbsolutePath);
			bool web = uri.Scheme is "http" or "https";
			return ($"{uri.Host}/{path}", $"{(web ? uri.Scheme : "https")}://{(web ? uri.Authority : uri.Host)}/{path}");
		}
		return (uri is { IsFile: false, UserInfo.Length: > 0 } ? uri.Host + uri.AbsolutePath : url, null); // a local path, or a scheme we do not know
	}

	static IEnumerable<string> Lines(string? output) =>
		(output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'));
}
