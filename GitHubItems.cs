using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermHost;

/// <summary>
/// What the GitHub panel shows of a repository: its open pull requests and open issues,
/// read with the GitHub CLI (`gh`), which also holds the sign-in. `gh` is slow next to git,
/// so it runs in the background and <see cref="Read"/> answers at once with what is known.
/// </summary>
sealed class GitHubItems
{
	const int MaxItems = 50;
	const int TitleLimit = 200;
	static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(60);
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

	sealed record Item(int Number, string Title, string Url, string? Author, bool Draft, bool Mine, long? Updated);

	/// <summary>One list: its items, or why there are none to show.</summary>
	sealed record Listing(List<Item>? Items, string? Error);

	sealed class Entry
	{
		public DateTime Expires;
		public bool Loading;
		public Listing? Pulls, Issues;
	}

	readonly object _lock = new();
	readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
	// The signed-in user of each GitHub host, for "Only mine".
	readonly Dictionary<string, string?> _logins = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>One repository as JSON. The lists are null until the first answer from GitHub is in.</summary>
	public JsonObject Read(string root, string name)
	{
		Listing? pulls, issues;
		lock (_lock)
		{
			if (!_cache.TryGetValue(root, out var entry))
				_cache[root] = entry = new Entry();
			if (!entry.Loading && DateTime.UtcNow >= entry.Expires)
			{
				entry.Loading = true;
				Task.Run(() => Refresh(root, entry));
			}
			(pulls, issues) = (entry.Pulls, entry.Issues);
		}
		return new JsonObject
		{
			["repo"] = name,
			["root"] = root,
			["prs"] = ToJson(pulls),
			["issues"] = ToJson(issues),
		};
	}

	static JsonObject? ToJson(Listing? listing) => listing is null ? null : new JsonObject
	{
		["error"] = listing.Error,
		["items"] = new JsonArray((listing.Items ?? []).Select(item => (JsonNode)new JsonObject
		{
			["number"] = item.Number,
			["title"] = item.Title,
			["url"] = item.Url,
			["author"] = item.Author,
			["draft"] = item.Draft,
			["mine"] = item.Mine,
			["updated"] = item.Updated,
		}).ToArray()),
	};

	void Refresh(string root, Entry entry)
	{
		Listing pulls, issues;
		try
		{
			pulls = List(root, "pr", "number,title,url,author,assignees,updatedAt,isDraft");
			issues = List(root, "issue", "number,title,url,author,assignees,updatedAt");
		}
		catch (Exception)
		{
			pulls = issues = new Listing(null, "GitHub could not be read.");
		}
		lock (_lock)
		{
			(entry.Pulls, entry.Issues) = (pulls, issues);
			entry.Expires = DateTime.UtcNow + CacheTime;
			entry.Loading = false;
		}
	}

	// `gh pr list` or `gh issue list`: the open ones, newest first.
	Listing List(string root, string kind, string fields)
	{
		var (output, error) = RunGh(root, kind, "list", "--state", "open", "--limit", MaxItems.ToString(), "--json", fields);
		if (output is null)
			return new Listing(null, error);
		try
		{
			using var doc = JsonDocument.Parse(output);
			var items = new List<Item>();
			foreach (var raw in doc.RootElement.EnumerateArray())
			{
				// Only ever a web address: it is what a click opens.
				if (!Uri.TryCreate(raw.GetProperty("url").GetString(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
					continue;
				var me = Login(root, url.Host);
				var author = LoginOf(raw.GetProperty("author"));
				// Mine: assigned to me, and for a pull request also one I opened.
				bool mine = me is not null && (raw.GetProperty("assignees").EnumerateArray().Any(who => string.Equals(LoginOf(who), me, StringComparison.OrdinalIgnoreCase))
					|| kind == "pr" && string.Equals(author, me, StringComparison.OrdinalIgnoreCase));
				items.Add(new Item(
					raw.GetProperty("number").GetInt32(),
					OneLine(raw.GetProperty("title").GetString()),
					url.AbsoluteUri,
					author,
					raw.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
					mine,
					raw.GetProperty("updatedAt").TryGetDateTimeOffset(out var updated) ? updated.ToUnixTimeMilliseconds() : null));
			}
			return new Listing(items, null);
		}
		catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
		{
			return new Listing(null, "GitHub's answer was not understood.");
		}
	}

	static string? LoginOf(JsonElement user) =>
		user.ValueKind == JsonValueKind.Object && user.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String ? login.GetString() : null;

	// Asked once per host; a failure is asked again the next time.
	string? Login(string root, string host)
	{
		lock (_lock)
			if (_logins.TryGetValue(host, out var known))
				return known;
		var login = RunGh(root, "api", "user", "--hostname", host, "--jq", ".login").Output?.Trim();
		if (string.IsNullOrEmpty(login))
			return null;
		lock (_lock)
			_logins[host] = login;
		return login;
	}

	// Titles are untrusted: control characters become spaces.
	static string OneLine(string? text)
	{
		var flat = string.Join(' ', new string((text ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
		return flat.Length > TitleLimit ? flat[..TitleLimit] : flat;
	}

	/// <summary>The output of `gh`, or null and one line that says why there is none.</summary>
	static (string? Output, string? Error) RunGh(string cwd, params string[] arguments)
	{
		try
		{
			var start = new ProcessStartInfo("gh")
			{
				WorkingDirectory = cwd,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			// Never a question and never colour codes: nobody is there to answer or to see them.
			start.Environment["GH_PROMPT_DISABLED"] = "1";
			start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
			start.Environment["NO_COLOR"] = "1";
			foreach (var argument in arguments)
				start.ArgumentList.Add(argument);
			using var process = Process.Start(start);
			if (process is null)
				return (null, "The GitHub CLI could not be started.");
			var output = process.StandardOutput.ReadToEndAsync();
			var error = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(Timeout))
			{
				process.Kill(entireProcessTree: true);
				return (null, "GitHub did not answer in time.");
			}
			if (process.ExitCode == 0)
				return (output.Result, null);
			// What gh says is wrong, such as not being signed in or the remote not being on GitHub.
			var said = OneLine(error.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault());
			return (null, said.Length > 0 ? said : "The GitHub CLI failed.");
		}
		catch (Win32Exception)
		{
			return (null, "The GitHub CLI (gh) is not installed. Get it from cli.github.com, then run: gh auth login");
		}
		catch (Exception e) when (e is InvalidOperationException or IOException or AggregateException)
		{
			return (null, "The GitHub CLI failed.");
		}
	}
}
