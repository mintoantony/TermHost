using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TermHost;

/// <summary>
/// Reports the Claude Code sessions running on this machine: their status, sub-agents,
/// recent activity and repositories. It reads Claude Code's own files under ~/.claude,
/// whose layout is undocumented. Ported from the Claude Mission Control dashboard.
/// Use from one thread only: the caches are not locked.
/// </summary>
public sealed partial class ClaudeStatus
{
	const int MaxAgentNodes = 300;
	const int ActivityTailBytes = 256 * 1024;
	const int ActivityLimit = 40;
	const int TextLimit = 60;
	const int SummaryLimit = 120;
	static readonly TimeSpan GitCacheTime = TimeSpan.FromSeconds(30);
	static readonly HashSet<string> QuestionTools = ["AskUserQuestion", "ExitPlanMode"];
	static readonly HashSet<string> AgentTools = ["Agent", "Task"];
	static readonly HashSet<string> FileTools = ["Read", "Edit", "Write"];
	static readonly HashSet<string> ShellTools = ["Bash", "PowerShell"];

	[GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
	private static partial Regex SessionIdPattern();

	readonly string _sessionsDir;
	readonly string _projectsDir;
	readonly Dictionary<string, Transcript> _transcripts = new();
	readonly Dictionary<string, string> _transcriptPaths = new();
	readonly Dictionary<string, (long Size, DateTime Modified, List<Event> Events)> _activity = new();
	readonly Dictionary<string, (DateTime Modified, AgentMeta Meta)> _metas = new();
	readonly Dictionary<string, (DateTime Expires, GitInfo? Info)> _git = new(StringComparer.OrdinalIgnoreCase);
	readonly GitDetails _details = new();
	readonly GitHubItems _hub = new();

	public ClaudeStatus()
	{
		var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
		if (string.IsNullOrWhiteSpace(home))
			home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
		_sessionsDir = Path.Combine(home, "sessions");
		_projectsDir = Path.Combine(home, "projects");
	}

	/// <summary>Everything the status panel shows, as the JSON message sent to the web view.</summary>
	/// <param name="shells">Process id of each terminal's shell in this window -> the terminal's id.</param>
	/// <param name="withGit">Also report the repositories of the sessions, for the Git panel.</param>
	/// <param name="withGitHub">Also report their open pull requests and issues, for the GitHub panel.</param>
	public string Snapshot(IReadOnlyDictionary<int, int> shells, bool withGit, bool withGitHub)
	{
		var now = DateTime.UtcNow;
		var sessions = new List<Session>();
		var parents = ParentPids();
		foreach (var file in RegistryFiles())
		{
			try
			{
				if (ReadSession(file, now) is { } session)
				{
					session.Terminal = TerminalOf(session.Pid, shells, parents);
					// A session started anywhere else names the application whose window it runs in.
					if (session.Terminal is null)
						session.Host = HostOf(session.Pid, parents)?.Name;
					sessions.Add(session);
				}
			}
			catch (Exception)
			{
				// One odd file must not hide every other session.
			}
		}
		sessions.Sort((a, b) =>
		{
			int order = StatusOrder(a.Status).CompareTo(StatusOrder(b.Status));
			return order != 0 ? order : (b.Last ?? 0).CompareTo(a.Last ?? 0);
		});

		return new JsonObject
		{
			["t"] = "status",
			["sessions"] = new JsonArray(sessions.Select(s => (JsonNode)s.ToJson()).ToArray()),
			["usage"] = UsageJson(),
			// Asking git about every worktree is the slow part: only while the Git panel is open.
			["git"] = withGit ? GitJson(sessions) : null,
			// GitHub is asked only while its panel is open.
			["github"] = withGitHub ? GitHubJson(sessions) : null,
		}.ToJsonString();
	}

	static int StatusOrder(string status) => status switch
	{
		"running" => 0,
		"waiting" => 1,
		"idle" => 2,
		"unknown" => 3,
		_ => 4,
	};

	// ---- Sessions: Claude Code's own registry, sessions/<pid>.json ----

	sealed class Session
	{
		public int Pid;
		public int? Terminal;
		public string? Id, Name, Title, Cwd, Model, WaitingFor, Version, Kind, Host, Remote;
		public string Status = "unknown";
		public long? Started, Last;
		public long Tokens;
		public GitInfo? Git;
		public List<Agent> Agents = [];
		public int AgentCount, AgentsRunning, AgentsOmitted;
		public List<Event> Activity = [];

		public JsonObject ToJson() => new()
		{
			["key"] = Pid.ToString(CultureInfo.InvariantCulture),
			["pid"] = Pid,
			["terminal"] = Terminal,
			["host"] = Host,
			["remote"] = Remote,
			["id"] = Id,
			["title"] = Title ?? Name,
			["cwd"] = Cwd,
			["status"] = Status,
			["waitingFor"] = WaitingFor,
			["started"] = Started,
			["last"] = Last,
			["model"] = Model,
			["tokens"] = Tokens,
			["version"] = Version,
			["kind"] = Kind,
			["repo"] = Git?.RepoName,
			["branch"] = Git?.Branch,
			["worktree"] = Git?.Top,
			["linked"] = Git?.Linked ?? false,
			["agents"] = new JsonArray(Agents.Select(a => (JsonNode)a.ToJson()).ToArray()),
			["agentCount"] = AgentCount,
			["agentsRunning"] = AgentsRunning,
			["agentsOmitted"] = AgentsOmitted,
			["activity"] = new JsonArray(Activity.Select(e => (JsonNode)new JsonObject { ["at"] = e.At, ["kind"] = e.Kind, ["text"] = e.Text }).ToArray()),
		};
	}

	IEnumerable<string> RegistryFiles()
	{
		try
		{
			return Directory.Exists(_sessionsDir) ? Directory.GetFiles(_sessionsDir, "*.json") : [];
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	Session? ReadSession(string file, DateTime now)
	{
		using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
		var raw = doc.RootElement;
		if (Element(raw, "pid", JsonValueKind.Number) is not { } pidValue || !pidValue.TryGetInt32(out int pid))
			return null;
		if (!IsLive(pid, raw))
			return null;

		var session = new Session
		{
			Pid = pid,
			Id = Text(raw, "sessionId"),
			Name = OneLine(Text(raw, "name"), 80),
			Cwd = Text(raw, "cwd"),
			WaitingFor = Text(raw, "waitingFor"),
			Version = Text(raw, "version"),
			Kind = Text(raw, "kind"),
			Started = Millis(raw, "startedAt"),
			Last = Millis(raw, "updatedAt"),
			Remote = RemoteUrl(Text(raw, "bridgeSessionId")),
		};

		Transcript? state = null;
		DateTime? modified = null;
		var transcript = session.Id is null ? null : TranscriptFor(session.Id, session.Cwd);
		if (transcript is not null)
		{
			state = Read(transcript);
			modified = state?.Modified;
		}
		if (state is not null)
		{
			session.Title = OneLine(state.CustomTitle ?? state.AiTitle ?? state.LastPrompt, 80);
			session.Model = state.Model;
			session.Tokens = state.Tokens;
			session.Last = ParseTime(state.LastTimestamp) ?? session.Last;
		}
		session.Status = DeriveStatus(Text(raw, "status"), state, modified, now);
		session.Git = session.Cwd is null ? null : DetectGit(session.Cwd, state?.GitBranch, now);

		if (transcript is not null)
		{
			BuildAgents(session, transcript, state, now);
			session.Activity = ReadActivity(transcript, session.Cwd);
		}
		return session;
	}

	// The address of a session that has Remote Control on, for opening it in a browser or sharing it.
	// Claude Code records the session's id there while Remote Control is on; the page takes it as "session_...".
	static string? RemoteUrl(string? id)
	{
		if (id is null || id.Length > 128 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
			return null;
		if (id.StartsWith("cse_", StringComparison.Ordinal))
			id = "session_" + id[4..];
		return "https://claude.ai/code/" + id;
	}

	// A PID is given out again once a process is gone, and its old file can still be there:
	// the process must be Claude Code and must have started when the entry says it did.
	static bool IsLive(int pid, JsonElement raw)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			// An update renames the running executable, to "claude.exe.old.<time>".
			var name = process.ProcessName.ToLowerInvariant();
			if (name != "node" && !name.StartsWith("claude", StringComparison.Ordinal))
				return false;
			var created = process.StartTime.ToUniversalTime();
			if (Element(raw, "startedAt", JsonValueKind.Number) is { } started && started.TryGetDouble(out double ms)
				&& created > DateTime.UnixEpoch.AddMilliseconds(ms).AddSeconds(10))
				return false;
			var procStart = Text(raw, "procStart");
			if (procStart is not null && long.TryParse(procStart, NumberStyles.None, CultureInfo.InvariantCulture, out long fileTime) && fileTime > 0)
			{
				try
				{
					return Math.Abs((DateTime.FromFileTimeUtc(fileTime) - created).TotalSeconds) <= 2;
				}
				catch (ArgumentOutOfRangeException)
				{
					return true;
				}
			}
			return true;
		}
		catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
		{
			return false; // no such process, or it ended while we looked
		}
	}

	// The terminal of this window a session runs in: the one whose shell is an ancestor of
	// the session's process. Null for a session started anywhere else.
	static int? TerminalOf(int pid, IReadOnlyDictionary<int, int> shells, Dictionary<int, int> parents)
	{
		var started = StartTime(pid);
		for (int depth = 0; depth < 32 && started is not null; depth++)
		{
			if (!parents.TryGetValue(pid, out int parent) || parent == pid)
				return null;
			// A parent that started after its child is another process that was given the PID later.
			var parentStarted = StartTime(parent);
			if (parentStarted is null || parentStarted > started)
				return null;
			if (shells.TryGetValue(parent, out int terminal))
				return terminal;
			(pid, started) = (parent, parentStarted);
		}
		return null;
	}

	// The window a session outside this window runs in: that of the nearest ancestor of the
	// session's process that has one, such as Windows Terminal or an editor. Null when none is
	// found, as for a classic console window, which belongs to a process that is not an ancestor.
	static (IntPtr Window, string Name)? HostOf(int pid, Dictionary<int, int> parents)
	{
		var started = StartTime(pid);
		for (int depth = 0; depth < 32 && started is not null; depth++)
		{
			if (!parents.TryGetValue(pid, out int parent) || parent == pid)
				return null;
			var parentStarted = StartTime(parent);
			if (parentStarted is null || parentStarted > started)
				return null;
			try
			{
				using var process = Process.GetProcessById(parent);
				// Explorer only started the shell: its windows are folders, not the session's.
				if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
					return null;
				if (process.MainWindowHandle != IntPtr.Zero)
					return (process.MainWindowHandle, AppName(process));
			}
			catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
			{
				return null;
			}
			(pid, started) = (parent, parentStarted);
		}
		return null;
	}

	static string AppName(Process process)
	{
		switch (process.ProcessName.ToLowerInvariant())
		{
			case "windowsterminal": return "Windows Terminal";
			case "code": return "Visual Studio Code";
			case "termhost": return "another TermHost window";
		}
		try
		{
			if (process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description)
				return description;
		}
		catch (Exception e) when (e is InvalidOperationException or Win32Exception)
		{
			// Not allowed to look at the executable: its name will do.
		}
		return process.ProcessName;
	}

	/// <summary>Brings the window a Claude session runs in to the front. Only ever acts on a Claude Code process.</summary>
	public static void FocusHost(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			var name = process.ProcessName.ToLowerInvariant();
			if (name != "node" && !name.StartsWith("claude", StringComparison.Ordinal))
				return;
		}
		catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
		{
			return;
		}
		if (HostOf(pid, ParentPids()) is not { } host)
			return;
		if (IsIconic(host.Window))
			ShowWindow(host.Window, SW_RESTORE);
		SetForegroundWindow(host.Window);
	}

	const int SW_RESTORE = 9;

	[DllImport("user32.dll")]
	static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll")]
	static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	static DateTime? StartTime(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return process.StartTime;
		}
		catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
		{
			return null;
		}
	}

	/// <summary>Every process id on the machine -> its parent's.</summary>
	static Dictionary<int, int> ParentPids()
	{
		var parents = new Dictionary<int, int>();
		var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
		if (snapshot == INVALID_HANDLE_VALUE)
			return parents;
		try
		{
			var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
			for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
				parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
		}
		finally
		{
			CloseHandle(snapshot);
		}
		return parents;
	}

	const uint TH32CS_SNAPPROCESS = 0x00000002;
	static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct PROCESSENTRY32W
	{
		public uint dwSize;
		public uint cntUsage;
		public uint th32ProcessID;
		public UIntPtr th32DefaultHeapID;
		public uint th32ModuleID;
		public uint cntThreads;
		public uint th32ParentProcessID;
		public int pcPriClassBase;
		public uint dwFlags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szExeFile;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

	[DllImport("kernel32.dll")]
	static extern bool CloseHandle(IntPtr hObject);

	static string DeriveStatus(string? rawStatus, Transcript? state, DateTime? modified, DateTime now)
	{
		if (rawStatus == "waiting" || state?.PendingQuestion == true)
			return "waiting";
		if (rawStatus is "busy" or "shell")
			return "running";
		if (rawStatus == "idle")
			return state?.LastAssistantError == true ? "failed" : "idle";
		if (rawStatus is null && modified is { } written)
			return (now - written).TotalSeconds <= 15 ? "running" : "idle";
		return "unknown";
	}

	// ---- Transcripts: projects/<encoded cwd>/<session id>.jsonl, read in steps ----

	sealed class Transcript
	{
		public long Offset;
		public long Size = -1;
		public DateTime Modified;
		// input, output, cache write, cache read
		public readonly long[] Usage = new long[4];
		// One API response is written as several records with the same id and a growing
		// output count: the newest replaces the earlier contribution.
		public readonly List<(string Id, long[] Counts)> RecentIds = [];
		public string? CustomTitle, AiTitle, LastPrompt, Model, GitBranch, FirstTimestamp, LastTimestamp, LastAssistantId, LastRecordType;
		public bool LastAssistantError;
		public List<(string Id, string Name)> LastAssistantTools = [];
		public readonly HashSet<string> AnsweredIds = [];
		public readonly HashSet<string> AgentCallIds = [];

		/// <summary>Cache reads are re-read every turn, so they are left out.</summary>
		public long Tokens => Usage[0] + Usage[1] + Usage[2];

		public bool PendingQuestion =>
			LastAssistantTools.Any(tool => QuestionTools.Contains(tool.Name) && !AnsweredIds.Contains(tool.Id));
	}

	string? TranscriptFor(string sessionId, string? cwd)
	{
		if (!SessionIdPattern().IsMatch(sessionId))
			return null;
		if (_transcriptPaths.TryGetValue(sessionId, out var known) && File.Exists(known))
			return known;
		string? found = null;
		try
		{
			if (cwd is not null)
			{
				var candidate = Path.Combine(_projectsDir, EncodeCwd(cwd), sessionId + ".jsonl");
				if (File.Exists(candidate))
					found = candidate;
			}
			// Long directory names are cut and hashed by Claude Code: fall back to a search.
			if (found is null && Directory.Exists(_projectsDir))
				found = Directory.EnumerateDirectories(_projectsDir)
					.Select(dir => Path.Combine(dir, sessionId + ".jsonl"))
					.FirstOrDefault(File.Exists);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
		if (found is not null)
			_transcriptPaths[sessionId] = found;
		return found;
	}

	static string EncodeCwd(string cwd)
	{
		var encoded = new StringBuilder(cwd.Length);
		foreach (char c in cwd)
			encoded.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
		return encoded.ToString();
	}

	/// <summary>What a transcript adds up to. Only the bytes added since the last call are read.</summary>
	Transcript? Read(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists)
				return null;
			_transcripts.TryGetValue(path, out var state);
			if (state is not null && state.Size == info.Length && state.Modified == info.LastWriteTimeUtc)
				return state;
			if (state is null || info.Length < state.Offset)
				state = _transcripts[path] = new Transcript();

			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
			stream.Seek(state.Offset, SeekOrigin.Begin);
			var buffer = new byte[1 << 20];
			int filled = 0;
			while (true)
			{
				int read = stream.Read(buffer, filled, buffer.Length - filled);
				if (read == 0)
					break;
				filled += read;
				int start = 0;
				while (Array.IndexOf(buffer, (byte)'\n', start, filled - start) is >= 0 and var newline)
				{
					FoldLine(state, buffer.AsMemory(start, newline - start));
					start = newline + 1;
				}
				// A last line without its newline is still being written: leave it for next time.
				state.Offset += start;
				filled -= start;
				Buffer.BlockCopy(buffer, start, buffer, 0, filled);
				if (filled == buffer.Length)
					Array.Resize(ref buffer, buffer.Length * 2);
			}
			state.Size = info.Length;
			state.Modified = info.LastWriteTimeUtc;
			return state;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return _transcripts.GetValueOrDefault(path);
		}
	}

	static void FoldLine(Transcript state, ReadOnlyMemory<byte> line)
	{
		if (line.Span.Trim((byte)' ').Trim((byte)'\r').IsEmpty)
			return;
		try
		{
			using var doc = JsonDocument.Parse(line);
			Fold(state, doc.RootElement);
		}
		catch (JsonException)
		{
			// Not a JSON line: skipped.
		}
	}

	static void Fold(Transcript state, JsonElement record)
	{
		if (record.ValueKind != JsonValueKind.Object)
			return;
		if (Text(record, "timestamp") is { } timestamp)
		{
			state.FirstTimestamp ??= timestamp;
			state.LastTimestamp = timestamp;
		}
		if (Text(record, "gitBranch") is { } branch)
			state.GitBranch = branch;

		switch (Text(record, "type"))
		{
			case "assistant":
				FoldAssistant(state, record);
				break;
			case "user":
				state.LastRecordType = "user";
				foreach (var block in Blocks(record))
					if (Text(block, "type") == "tool_result" && Text(block, "tool_use_id") is { } answered
						&& state.LastAssistantTools.Any(tool => tool.Id == answered))
						state.AnsweredIds.Add(answered);
				break;
			case "custom-title":
				state.CustomTitle = Text(record, "customTitle") ?? state.CustomTitle;
				break;
			case "ai-title":
				state.AiTitle = Text(record, "aiTitle") ?? state.AiTitle;
				break;
			case "last-prompt":
				if (Text(record, "lastPrompt") is { } prompt)
					state.LastPrompt = prompt.Length > 200 ? prompt[..200] : prompt;
				break;
		}
	}

	static void FoldAssistant(Transcript state, JsonElement record)
	{
		var message = Element(record, "message", JsonValueKind.Object) ?? default;
		if (Text(message, "model") is { } model && model != "<synthetic>")
			state.Model = model;
		var id = Text(message, "id");

		if (Element(message, "usage", JsonValueKind.Object) is { } usage)
		{
			long[] counts =
			[
				Count(usage, "input_tokens"),
				Count(usage, "output_tokens"),
				Count(usage, "cache_creation_input_tokens"),
				Count(usage, "cache_read_input_tokens"),
			];
			if (id is not null)
			{
				int earlier = state.RecentIds.FindIndex(entry => entry.Id == id);
				if (earlier >= 0)
				{
					for (int i = 0; i < 4; i++)
						state.Usage[i] -= state.RecentIds[earlier].Counts[i];
					state.RecentIds.RemoveAt(earlier);
				}
				state.RecentIds.Add((id, counts));
				if (state.RecentIds.Count > 64)
					state.RecentIds.RemoveAt(0);
			}
			for (int i = 0; i < 4; i++)
				state.Usage[i] += counts[i];
		}

		var tools = new List<(string Id, string Name)>();
		foreach (var block in Blocks(record))
			if (Text(block, "type") == "tool_use" && Text(block, "id") is { } toolId && Text(block, "name") is { } name)
				tools.Add((toolId, name));
		if (id is not null && id == state.LastAssistantId)
		{
			state.LastAssistantTools.AddRange(tools.Where(tool => state.LastAssistantTools.All(known => known.Id != tool.Id)));
		}
		else
		{
			state.LastAssistantId = id;
			state.LastAssistantTools = tools;
			state.AnsweredIds.Clear();
		}
		state.LastAssistantError = IsTrue(record, "isApiErrorMessage");
		state.LastRecordType = "assistant";
		foreach (var tool in tools)
			if (AgentTools.Contains(tool.Name))
				state.AgentCallIds.Add(tool.Id);
	}

	// ---- Sub-agents: <transcript>/subagents/**/agent-<id>.jsonl, each with a .meta.json ----

	sealed record AgentMeta(string? Label, string? Description, string? Parent, string? ToolUseId);

	sealed class Agent
	{
		public string Id = "", Label = "agent", Description = "", Status = "stopped";
		public long? Started, Last;
		public long Tokens;
		public bool IsGroup;
		public string? MetaParent, ToolUseId, Workflow;
		public List<Agent> Children = [];

		public JsonObject ToJson() => new()
		{
			["id"] = Id,
			["label"] = Label,
			["desc"] = Description,
			["status"] = Status,
			["started"] = Started,
			["last"] = Last,
			["tokens"] = Tokens,
			["children"] = new JsonArray(Children.Select(child => (JsonNode)child.ToJson()).ToArray()),
		};
	}

	void BuildAgents(Session session, string transcript, Transcript? sessionState, DateTime now)
	{
		var folder = Path.Combine(Path.GetDirectoryName(transcript)!, Path.GetFileNameWithoutExtension(transcript), "subagents");
		List<string> files;
		try
		{
			if (!Directory.Exists(folder))
				return;
			files = Directory.EnumerateFiles(folder, "agent-*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return;
		}

		var items = new Dictionary<string, Agent>();
		var callOwner = new Dictionary<string, string>(); // tool_use id of an Agent call -> the calling agent
		foreach (var path in files)
		{
			var agentId = Path.GetFileName(path)["agent-".Length..^".jsonl".Length];
			if (agentId.Length == 0 || items.ContainsKey(agentId))
				continue;
			var state = Read(path);
			items[agentId] = BuildAgent(agentId, path, state, now);
			if (state is not null)
				foreach (var call in state.AgentCallIds)
					callOwner.TryAdd(call, agentId);
		}
		if (items.Count == 0)
			return;
		session.AgentCount = items.Count;
		session.Tokens += items.Values.Sum(agent => agent.Tokens); // a session's total includes its agents
		session.AgentsRunning = items.Values.Count(agent => agent.Status == "running");

		// Parent of each agent; null is the session itself. A workflow's agents go under a group node.
		const string groupPrefix = "\0";
		var parents = new Dictionary<string, string?>();
		foreach (var (agentId, agent) in items)
		{
			if (agent.MetaParent is not null && items.ContainsKey(agent.MetaParent))
				parents[agentId] = agent.MetaParent;
			else if (agent.ToolUseId is not null && sessionState?.AgentCallIds.Contains(agent.ToolUseId) == true)
				parents[agentId] = null;
			else if (agent.ToolUseId is not null && callOwner.TryGetValue(agent.ToolUseId, out var owner) && owner != agentId)
				parents[agentId] = owner;
			else if (agent.Workflow is not null)
				parents[agentId] = groupPrefix + agent.Workflow;
			else
				parents[agentId] = null;
		}
		// A chain of parents that comes back to where it started is cut at that node.
		foreach (var key in parents.Keys.Order(StringComparer.Ordinal).ToList())
		{
			var seen = new HashSet<string> { key };
			var current = parents[key];
			while (current is not null && seen.Add(current))
				current = parents.GetValueOrDefault(current);
			if (current == key)
				parents[key] = null;
		}

		var top = new List<Agent>();
		foreach (var (agentId, parent) in parents)
		{
			if (parent is null)
				top.Add(items[agentId]);
			else if (items.TryGetValue(parent, out var parentAgent))
				parentAgent.Children.Add(items[agentId]);
			else
			{
				var group = new Agent { Id = "wf_" + parent[groupPrefix.Length..], Label = "Workflow " + parent[groupPrefix.Length..], IsGroup = true };
				items[parent] = group;
				group.Children.Add(items[agentId]);
				top.Add(group);
			}
		}
		foreach (var group in top.Where(agent => agent.IsGroup))
		{
			group.Tokens = group.Children.Sum(member => member.Tokens);
			group.Started = group.Children.Min(member => member.Started);
			group.Last = group.Children.Max(member => member.Last);
			group.Status = group.Children.Any(member => member.Status == "running") ? "running"
				: group.Children.All(member => member.Status == "done") ? "done" : "stopped";
		}

		// Display order: running first, then newest start first. Cut at the node limit.
		int kept = 0, keptAgents = 0;
		List<Agent> Trim(List<Agent> nodes)
		{
			var result = new List<Agent>();
			foreach (var node in nodes.OrderBy(n => n.Status != "running").ThenByDescending(n => n.Started ?? long.MinValue).ThenBy(n => n.Id, StringComparer.Ordinal))
			{
				if (kept >= MaxAgentNodes)
					break;
				kept++;
				if (!node.IsGroup)
					keptAgents++;
				node.Children = Trim(node.Children);
				result.Add(node);
			}
			return result;
		}
		session.Agents = Trim(top);
		session.AgentsOmitted = session.AgentCount - keptAgents;
	}

	Agent BuildAgent(string agentId, string path, Transcript? state, DateTime now)
	{
		var meta = ReadMeta(path[..^".jsonl".Length] + ".meta.json");
		var folder = Path.GetDirectoryName(path)!;
		var folderName = Path.GetFileName(folder);
		bool inWorkflow = folderName.StartsWith("wf_", StringComparison.Ordinal) && Path.GetFileName(Path.GetDirectoryName(folder)) == "workflows";
		return new Agent
		{
			Id = agentId,
			Label = meta.Label ?? "agent",
			Description = meta.Description ?? "",
			Status = AgentStatus(state, now),
			Started = ParseTime(state?.FirstTimestamp),
			Last = ParseTime(state?.LastTimestamp),
			Tokens = state?.Tokens ?? 0,
			MetaParent = meta.Parent,
			ToolUseId = meta.ToolUseId,
			Workflow = inWorkflow ? folderName["wf_".Length..] : null,
		};
	}

	static string AgentStatus(Transcript? state, DateTime now)
	{
		double? age = state is null ? null : (now - state.Modified).TotalSeconds;
		if (age <= 5)
			return "running"; // just written
		if (state is not null)
		{
			var tools = state.LastAssistantTools;
			if (state.LastRecordType == "assistant" && tools.Count == 0)
				return "done";
			if (tools.Count > 0 && tools[^1].Name == "SubagentHandback" && state.AnsweredIds.Contains(tools[^1].Id))
				return "done";
		}
		return age <= 600 ? "running" : "stopped";
	}

	AgentMeta ReadMeta(string path)
	{
		var empty = new AgentMeta(null, null, null, null);
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length > 64 * 1024)
				return empty;
			if (_metas.TryGetValue(path, out var cached) && cached.Modified == info.LastWriteTimeUtc)
				return cached.Meta;
			using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
			var root = doc.RootElement;
			var meta = new AgentMeta(
				OneLine(Text(root, "name"), 60) ?? OneLine(Text(root, "agentType"), 60),
				OneLine(Text(root, "description"), SummaryLimit),
				Text(root, "parentAgentId"),
				Text(root, "toolUseId"));
			if (_metas.Count >= 4096)
				_metas.Clear();
			_metas[path] = (info.LastWriteTimeUtc, meta);
			return meta;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
		{
			return empty;
		}
	}

	// ---- Activity: prompts, tool calls and errors from the end of a transcript ----

	sealed record Event(long At, string Kind, string Text);

	List<Event> ReadActivity(string transcript, string? cwd)
	{
		_activity.TryGetValue(transcript, out var cached);
		try
		{
			var info = new FileInfo(transcript);
			if (cached.Events is not null && cached.Size == info.Length && cached.Modified == info.LastWriteTimeUtc)
				return cached.Events;

			using var stream = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long start = Math.Max(0, stream.Length - ActivityTailBytes);
			stream.Seek(start, SeekOrigin.Begin);
			var buffer = new byte[stream.Length - start];
			int filled = 0;
			while (filled < buffer.Length && stream.Read(buffer, filled, buffer.Length - filled) is > 0 and var read)
				filled += read;

			var events = new List<Event>();
			var seenTools = new HashSet<string>();
			long? lastTime = null;
			// When the window starts mid-file its first line is, or may be, cut: drop it.
			int position = start > 0 ? Array.IndexOf(buffer, (byte)'\n', 0, filled) + 1 : 0;
			while (position < filled)
			{
				int newline = Array.IndexOf(buffer, (byte)'\n', position, filled - position);
				int end = newline < 0 ? filled : newline;
				try
				{
					using var doc = JsonDocument.Parse(buffer.AsMemory(position, end - position));
					AddEvents(doc.RootElement, cwd, seenTools, ref lastTime, events);
				}
				catch (JsonException)
				{
					// Blank, or a last line that is still being written.
				}
				position = end + 1;
			}
			if (events.Count > ActivityLimit)
				events.RemoveRange(0, events.Count - ActivityLimit);
			_activity[transcript] = (info.Length, info.LastWriteTimeUtc, events);
			return events;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return cached.Events ?? [];
		}
	}

	static void AddEvents(JsonElement record, string? cwd, HashSet<string> seenTools, ref long? lastTime, List<Event> events)
	{
		if (record.ValueKind != JsonValueKind.Object)
			return;
		lastTime = ParseTime(Text(record, "timestamp")) ?? lastTime;
		if (lastTime is not { } at)
			return;
		void Add(string kind, string text) => events.Add(new Event(at, kind, text.Length > SummaryLimit ? text[..SummaryLimit] : text));

		switch (Text(record, "type"))
		{
			case "assistant":
				if (IsTrue(record, "isApiErrorMessage"))
					Add("error", "API error");
				foreach (var block in Blocks(record))
				{
					if (Text(block, "type") != "tool_use" || Text(block, "name") is not { } name)
						continue;
					// One API response is written as several records; a block can come twice.
					if (Text(block, "id") is { } toolId && !seenTools.Add(toolId))
						continue;
					Add("tool", ToolSummary(name, Element(block, "input", JsonValueKind.Object) ?? default, cwd));
				}
				break;
			case "user":
				if (HumanPrompt(record) is { } prompt)
					Add("prompt", OneLine(prompt, TextLimit) ?? "Prompt");
				break;
			case "system":
				if (Text(record, "subtype") == "compact_boundary")
					Add("system", "Context compacted");
				break;
		}
	}

	static string ToolSummary(string name, JsonElement input, string? cwd)
	{
		if (FileTools.Contains(name))
			return Text(input, "file_path") is { } file ? $"{name} {RelativeTo(file, cwd)}" : name;
		if (ShellTools.Contains(name))
			return (OneLine(Text(input, "description"), SummaryLimit) ?? OneLine(Text(input, "command"), TextLimit)) is { } what ? $"{name}: {what}" : name;
		if (AgentTools.Contains(name))
			return OneLine(Text(input, "description"), SummaryLimit) is { } task ? $"Agent: {task}" : "Agent";
		return name;
	}

	static string RelativeTo(string file, string? cwd)
	{
		if (string.IsNullOrEmpty(cwd))
			return file;
		var root = cwd.Replace('\\', '/').TrimEnd('/') + "/";
		return file.Replace('\\', '/').StartsWith(root, StringComparison.OrdinalIgnoreCase) && file.Length > root.Length ? file[root.Length..] : file;
	}

	// The text of a user record a person typed, else null. Newer versions say so in
	// origin.kind; older ones are recognised by what injected text looks like.
	static string? HumanPrompt(JsonElement record)
	{
		string? text = null;
		var message = Element(record, "message", JsonValueKind.Object) ?? default;
		if (Text(message, "content") is { } plain)
			text = plain;
		else
			foreach (var block in Blocks(record))
			{
				if (Text(block, "type") == "tool_result")
					return null;
				if (text is null && Text(block, "type") == "text")
					text = Text(block, "text");
			}
		if (string.IsNullOrWhiteSpace(text))
			return null;
		if (Element(record, "origin", JsonValueKind.Object) is { } origin)
			return Text(origin, "kind") == "human" ? text : null;
		if (IsTrue(record, "isMeta") || IsTrue(record, "isCompactSummary"))
			return null;
		return text.StartsWith('<') || text.StartsWith("[Request interrupted", StringComparison.Ordinal) ? null : text;
	}

	// ---- Git: repository and branch of each session's directory ----

	sealed record GitInfo(string RepoName, string? Branch, string? RepoRoot, string? Top, bool Linked);

	GitInfo DetectGit(string cwd, string? transcriptBranch, DateTime now)
	{
		if (!_git.TryGetValue(cwd, out var entry) || now >= entry.Expires)
		{
			entry = (now + GitCacheTime, QueryGit(cwd));
			_git[cwd] = entry;
		}
		// Not a repository, or git missing: name the directory and keep the branch Claude recorded.
		return entry.Info ?? new GitInfo(Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : cwd, transcriptBranch, null, null, false);
	}

	static GitInfo? QueryGit(string cwd)
	{
		// --path-format=absolute matters: without it the directories come back relative to
		// different bases and a plain repository looks like a linked worktree.
		var lines = RunGit(cwd, "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir")
			?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines is not { Length: 3 })
			return null;
		string top = Path.GetFullPath(lines[0]), gitDir = Path.GetFullPath(lines[1]), commonDir = Path.GetFullPath(lines[2]);
		// A submodule or a bare repository has a common directory not named ".git".
		var root = Path.GetFileName(commonDir) == ".git" ? Path.GetDirectoryName(commonDir)! : top;
		var branch = RunGit(cwd, "symbolic-ref", "--short", "-q", "HEAD")?.Trim();
		if (string.IsNullOrEmpty(branch))
			branch = RunGit(cwd, "rev-parse", "--short", "HEAD")?.Trim() is { Length: > 0 } sha ? "@" + sha : null; // detached HEAD
		return new GitInfo(Path.GetFileName(root) is { Length: > 0 } name ? name : root, branch, root, top,
			!string.Equals(gitDir, commonDir, StringComparison.OrdinalIgnoreCase));
	}

	// The repositories that hold a session, each with its remotes and worktrees.
	JsonArray GitJson(List<Session> sessions)
	{
		var repos = new JsonArray();
		foreach (var repo in sessions.Where(s => s.Git?.RepoRoot is not null).GroupBy(s => s.Git!.RepoRoot!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
			repos.Add(_details.Read(repo.Key, repo.First().Git!.RepoName,
				repo.Select(s => (s.Git!.Top, s.Pid.ToString(CultureInfo.InvariantCulture))).ToList()));
		return repos;
	}

	// The same repositories, each with its open pull requests and issues.
	JsonArray GitHubJson(List<Session> sessions)
	{
		var repos = new JsonArray();
		foreach (var repo in sessions.Where(s => s.Git?.RepoRoot is not null).GroupBy(s => s.Git!.RepoRoot!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
			repos.Add(_hub.Read(repo.Key, repo.First().Git!.RepoName));
		return repos;
	}

	internal static string? RunGit(string cwd, params string[] arguments)
	{
		try
		{
			var start = new ProcessStartInfo("git")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			start.ArgumentList.Add("-C");
			start.ArgumentList.Add(cwd);
			foreach (var argument in arguments)
				start.ArgumentList.Add(argument);
			using var process = Process.Start(start);
			if (process is null)
				return null;
			var output = process.StandardOutput.ReadToEndAsync();
			_ = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(3000))
			{
				process.Kill(entireProcessTree: true);
				return null;
			}
			return process.ExitCode == 0 ? output.Result : null;
		}
		catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or AggregateException)
		{
			return null; // git missing, or it ended oddly
		}
	}

	// ---- Usage limits: the 5-hour and 7-day windows of the Claude plan ----
	//
	// Claude Code gives these figures only to its status line. The ClaudeCodeStatusLine
	// script (github.com/daniel3303/ClaudeCodeStatusLine) saves what it is given to a small
	// file, and that file is what is read here: no credentials, no network.

	static readonly (string Key, string Label)[] UsageWindows =
		[("five_hour", "5-hour"), ("seven_day", "7-day"), ("seven_day_opus", "7-day Opus"), ("seven_day_sonnet", "7-day Sonnet")];

	static readonly string UsageFile = Path.Combine(Path.GetTempPath(), "claude", "statusline-usage-cache.json");

	sealed record UsageWindow(string Label, double Percent, long? Resets);

	// The last good reading: the file is emptied for a moment each time it is rewritten.
	(long At, List<UsageWindow> Windows, double? Extra)? _usage;

	JsonObject? UsageJson()
	{
		try
		{
			var info = new FileInfo(UsageFile);
			if (info.Exists && info.Length > 0)
			{
				using var doc = JsonDocument.Parse(File.ReadAllBytes(UsageFile));
				var windows = new List<UsageWindow>();
				foreach (var (key, label) in UsageWindows)
					if (Element(doc.RootElement, key, JsonValueKind.Object) is { } window
						&& Element(window, "utilization", JsonValueKind.Number) is { } used && used.TryGetDouble(out double percent))
						windows.Add(new UsageWindow(label, percent, ParseTime(Text(window, "resets_at"))));
				double? extra = Element(doc.RootElement, "extra_usage", JsonValueKind.Object) is { } paid && IsTrue(paid, "is_enabled")
					&& Element(paid, "utilization", JsonValueKind.Number) is { } spent && spent.TryGetDouble(out double spentPercent) ? spentPercent : null;
				if (windows.Count > 0)
					_usage = (new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), windows, extra);
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
		{
			// Caught half-written: keep the last good reading.
		}
		if (_usage is not { } usage)
			return null;
		return new JsonObject
		{
			["at"] = usage.At,
			["windows"] = new JsonArray(usage.Windows.Select(window =>
				(JsonNode)new JsonObject { ["label"] = window.Label, ["percent"] = window.Percent, ["resets"] = window.Resets }).ToArray()),
			["extra"] = usage.Extra,
		};
	}

	// ---- Reading untrusted JSON: a wrong shape is "not there", never an error ----

	static JsonElement? Element(JsonElement parent, string name, JsonValueKind kind) =>
		parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == kind ? value : null;

	static string? Text(JsonElement parent, string name) =>
		Element(parent, name, JsonValueKind.String)?.GetString() is { Length: > 0 } text ? text : null;

	static bool IsTrue(JsonElement parent, string name) => Element(parent, name, JsonValueKind.True) is not null;

	static long Count(JsonElement parent, string name) =>
		Element(parent, name, JsonValueKind.Number) is { } value && value.TryGetInt64(out long count) && count > 0 ? count : 0;

	static long? Millis(JsonElement parent, string name) =>
		Element(parent, name, JsonValueKind.Number) is { } value && value.TryGetDouble(out double ms) && ms is > 946_684_800_000 and < 4_102_444_800_000 ? (long)ms : null;

	/// <summary>The content blocks of a record's message; none when the content is plain text.</summary>
	static IEnumerable<JsonElement> Blocks(JsonElement record)
	{
		var message = Element(record, "message", JsonValueKind.Object) ?? default;
		if (Element(message, "content", JsonValueKind.Array) is not { } content)
			yield break;
		foreach (var block in content.EnumerateArray())
			if (block.ValueKind == JsonValueKind.Object)
				yield return block;
	}

	static long? ParseTime(string? value) =>
		DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) && time.Year is >= 2000 and < 2100
			? time.ToUnixTimeMilliseconds()
			: null;

	// Titles and prompts are untrusted: control characters become spaces.
	static string? OneLine(string? text, int limit)
	{
		if (string.IsNullOrWhiteSpace(text))
			return null;
		var flat = string.Join(' ', new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
		return flat.Length == 0 ? null : flat.Length > limit ? flat[..limit] : flat;
	}
}
