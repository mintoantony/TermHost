using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermHost;

public partial class MainPage : ContentPage
{
	enum ShellKind { PowerShell, Cmd, Wsl }

	record Shell(string Name, string Command, ShellKind Kind);

	// Runs in every new terminal until changed in settings.
	const string DefaultProgram = "claude";

	readonly List<Shell> _shells = FindShells();
	// Terminals announced to the web view but not yet started: id -> command line and starting folder.
	readonly Dictionary<int, (string Command, string Folder)> _pending = new();
	// Concurrent: the Claude status reader looks up the shells from its own thread.
	readonly ConcurrentDictionary<int, ConPtySession> _sessions = new();
	int _nextId = 1;
	readonly CancellationTokenSource _closing = new();
	// The last Claude status sent to the web view, so that only changes are sent.
	string? _status;
	bool _watching;
	// Whether the Git panel is open: the reader asks git about the repositories only then.
	volatile bool _gitOpen;
	readonly SemaphoreSlim _refresh = new(0);
	// What the latest release on GitHub is, once known: sent to the web view for its About dialog.
	const string Repository = "https://github.com/mintoantony/TermHost";
	string? _update;
	// The version the build was given. Not AppInfo's: without a package that one is always 1.0.
	static readonly Version AppVersion = Three(typeof(MainPage).Assembly.GetName().Version ?? new Version(0, 0));
	// 1.2 and 1.2.0 are the same version.
	static Version Three(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
	bool _updateChecked;
	// Closing the window: asked of the web view, which knows what is still running, and then allowed.
	bool _closeHooked, _closeAsked, _closeAllowed;

	public MainPage()
	{
		InitializeComponent();
	}

	public void CloseAll()
	{
		_closing.Cancel();
		foreach (var session in _sessions.Values)
			session.Dispose();
		_sessions.Clear();
	}

	Shell DefaultShell =>
		_shells.FirstOrDefault(s => s.Name == Preferences.Default.Get("shell", "")) ?? _shells[0];

	// Where new terminals start: the folder TermHost was opened in from File Explorer, otherwise the home folder.
	static readonly string StartFolder = LaunchFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	static readonly bool Launched = LaunchFolder() is not null;

	// The folder given on the command line by "Open in TermHost". Only ever an existing directory.
	static string? LaunchFolder()
	{
		var args = Environment.GetCommandLineArgs();
		if (args.Length < 2)
			return null;
		// A drive arrives as "D:\" in quotes, which command-line parsing turns into D:" .
		var folder = args[1].Trim('"');
		if (folder.EndsWith(':'))
			folder += Path.DirectorySeparatorChar;
		try
		{
			return Directory.Exists(folder) ? Path.GetFullPath(folder) : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	void SendInit()
	{
		var init = new JsonObject
		{
			["t"] = "init",
			["shells"] = new JsonArray(_shells.Select(s => (JsonNode)s.Name).ToArray()),
			["version"] = AppVersion.ToString(),
			// Where the "new terminal with options" dialog starts: the folder it used last.
			["folder"] = Launched ? StartFolder : Preferences.Default.Get("folder", StartFolder),
			["settings"] = new JsonObject
			{
				["shell"] = DefaultShell.Name,
				["program"] = Preferences.Default.Get("program", DefaultProgram),
				["theme"] = Preferences.Default.Get("theme", "Catppuccin Mocha"),
				["layout"] = Preferences.Default.Get("layout", "tabs"),
				["glass"] = Preferences.Default.Get("glass", "0"),
			},
		};
		Web.SendRawMessage(init.ToJsonString());
	}

	// Reads the Claude sessions off the UI thread every few seconds for the status panel.
	void WatchClaude()
	{
		if (_status is not null)
			Web.SendRawMessage(_status); // the page was reloaded: give it what we have
		if (_watching)
			return;
		_watching = true;
		var stop = _closing.Token;
		Task.Run(async () =>
		{
			var claude = new ClaudeStatus();
			while (!stop.IsCancellationRequested)
			{
				var status = claude.Snapshot(_sessions.ToDictionary(shell => shell.Value.ProcessId, shell => shell.Key), _gitOpen);
				Dispatcher.Dispatch(() =>
				{
					if (status == _status || stop.IsCancellationRequested)
						return;
					_status = status;
					Web.SendRawMessage(status);
				});
				try
				{
					// Sooner when a panel asks for a refresh.
					await _refresh.WaitAsync(TimeSpan.FromSeconds(3), stop);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		});
	}

	// The web view sizes the terminal first, then asks us to start the shell at that size.
	void NewTerminal() =>
		NewTerminal(DefaultShell, Preferences.Default.Get("program", DefaultProgram).Trim(), StartFolder);

	void NewTerminal(Shell shell, string program, string folder)
	{
		int id = _nextId++;
		_pending[id] = (CommandLine(shell, program), folder);
		var name = program.Length == 0 ? shell.Name : program.Length > 24 ? program[..24] + "…" : program;
		var title = JsonEncodedText.Encode($"{name} {id}");
		Web.SendRawMessage($"{{\"t\":\"new\",\"id\":{id},\"title\":\"{title}\"}}");
	}

	// Runs the startup program inside the shell and leaves the shell open when it ends.
	static string CommandLine(Shell shell, string program)
	{
		if (program.Length == 0)
			return shell.Command;
		return shell.Kind switch
		{
			// Encoded so quotes and spaces in the program survive command-line parsing.
			ShellKind.PowerShell => $"{shell.Command} -NoExit -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(program))}",
			ShellKind.Cmd => $"{shell.Command} /K \"{program}\"",
			_ => $"{shell.Command} -e bash -lic \"{program.Replace("\"", "\\\"")}; exec bash\"",
		};
	}

	void OnRawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Message))
			return;
		using var doc = JsonDocument.Parse(e.Message);
		var msg = doc.RootElement;
		string Text(string name) => msg.GetProperty(name).GetString() ?? "";
		int Number(string name) => msg.GetProperty(name).GetInt32();

		switch (Text("t"))
		{
			case "ready":
				_gitOpen = false; // a fresh page starts with its panels closed
				_closeAsked = false;
				ConfirmClosing();
				CheckForUpdate();
				SendInit();
				NewTerminal();
				WatchClaude();
				break;
			case "new":
				NewTerminal();
				break;
			case "launch":
				Launch(Text("shell"), Text("program").Trim(), Text("folder").Trim());
				break;
			case "browse":
				Browse();
				break;
			case "git":
				_gitOpen = msg.GetProperty("open").GetBoolean();
				if (_gitOpen)
					_refresh.Release();
				break;
			case "open":
				// A remote's web page. Only ever a web address: nothing else is handed to the system.
				if (Uri.TryCreate(Text("url"), UriKind.Absolute, out var page) && page.Scheme is "http" or "https")
					_ = Launcher.Default.OpenAsync(page);
				break;
			case "folder":
				// A session's directory, shown in File Explorer. Only ever an existing directory.
				if (Directory.Exists(Text("path")))
					Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(Text("path")) } })?.Dispose();
				break;
			case "focus":
				// The window of a session that runs outside this one, brought to the front.
				ClaudeStatus.FocusHost(Number("pid"));
				break;
			case "stay":
				_closeAsked = false;
				break;
			case "quit":
				_closeAllowed = true;
				Application.Current?.CloseWindow(Window);
				break;
			case "settings":
				foreach (var key in new[] { "shell", "program", "theme", "layout", "glass" })
					Preferences.Default.Set(key, Text(key));
				break;
			case "chrome":
				ApplyChrome(Text("bg"), Text("fg"), msg.GetProperty("dark").GetBoolean(), msg.GetProperty("glass").GetDouble());
				break;
			case "start":
				Start(Number("id"), Number("cols"), Number("rows"));
				break;
			case "in":
				if (_sessions.TryGetValue(Number("id"), out var target))
					target.Write(Text("d"));
				break;
			case "resize":
				if (_sessions.TryGetValue(Number("id"), out var resized))
					resized.Resize(Number("cols"), Number("rows"));
				break;
			case "close":
				_pending.Remove(Number("id"));
				if (_sessions.TryRemove(Number("id"), out var closed))
					closed.Dispose();
				break;
		}
	}

	// A terminal from the "new terminal with options" dialog: the chosen shell, program and folder, for this one only.
	void Launch(string shellName, string program, string folder)
	{
		// Quotes come along when a path is pasted from "Copy as path".
		folder = folder.Trim('"');
		if (folder.Length == 0)
			folder = StartFolder;
		if (!Directory.Exists(folder))
		{
			Web.SendRawMessage("{\"t\":\"launch-error\",\"error\":\"That folder does not exist.\"}");
			return;
		}
		folder = Path.GetFullPath(folder);
		Preferences.Default.Set("folder", folder);
		Web.SendRawMessage(new JsonObject { ["t"] = "launched", ["folder"] = folder }.ToJsonString());
		NewTerminal(_shells.FirstOrDefault(s => s.Name == shellName) ?? DefaultShell, program, folder);
	}

	// The native folder picker, for the dialog's Browse button.
	async void Browse()
	{
#if WINDOWS
		if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
			return;
		var picker = new Windows.Storage.Pickers.FolderPicker();
		picker.FileTypeFilter.Add("*");
		WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
		try
		{
			if (await picker.PickSingleFolderAsync() is { } picked)
				Web.SendRawMessage(new JsonObject { ["t"] = "picked", ["folder"] = picked.Path }.ToJsonString());
		}
		catch (Exception)
		{
			// No picker (it cannot open from an elevated process): the folder can still be typed.
		}
#else
		await Task.CompletedTask;
#endif
	}

	// Asks GitHub once for the latest release and tells the web view whether it is newer than this copy.
	// Any failure (offline, rate limit, no release) is silent: the About dialog then says nothing about updates.
	async void CheckForUpdate()
	{
		if (_update is not null)
			Web.SendRawMessage(_update); // the page was reloaded: give it what we have
		if (_updateChecked)
			return;
		_updateChecked = true;
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("TermHost");
			http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
			using var doc = JsonDocument.Parse(await http.GetStringAsync(
				Repository.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest", _closing.Token));
			var release = doc.RootElement;
			var tag = release.GetProperty("tag_name").GetString() ?? "";
			if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
				return;
			// The installer when the release has one, otherwise the release's page. Only ever this repository's.
			var url = release.GetProperty("html_url").GetString();
			if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
				foreach (var asset in assets.EnumerateArray())
					if (asset.GetProperty("browser_download_url").GetString() is { } download && download.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
						url = download;
			if (url is null || !url.StartsWith(Repository + "/", StringComparison.OrdinalIgnoreCase))
				url = Repository + "/releases/latest";
			_update = new JsonObject
			{
				["t"] = "update",
				["latest"] = Three(latest).ToString(),
				["newer"] = Three(latest) > AppVersion,
				["url"] = url,
			}.ToJsonString();
			if (!_closing.IsCancellationRequested)
				Web.SendRawMessage(_update);
		}
		catch (Exception)
		{
		}
	}

	// Holds the window open when its close button is used, and asks the web view: it closes at once
	// when no Claude session is running in a terminal here, and otherwise asks the user first.
	void ConfirmClosing()
	{
#if WINDOWS
		if (_closeHooked || Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
			return;
		_closeHooked = true;
		native.AppWindow.Closing += (_, e) =>
		{
			// A second close while the question is open goes through, so the window cannot get stuck.
			if (_closeAllowed || _closeAsked)
				return;
			e.Cancel = true;
			_closeAsked = true;
			Web.SendRawMessage("{\"t\":\"closing\"}");
		};
#endif
	}

	// Keeps the native window chrome in step with the theme chosen in the web view.
	// Glass is how see-through the window's background is, 0 (solid) to 1: the desktop then shows
	// through it blurred, under the same tint the web view paints over its own area.
	void ApplyChrome(string background, string foreground, bool dark, double glass)
	{
		if (!Color.TryParse(background, out var bg) || !Color.TryParse(foreground, out var fg))
			return;
		glass = Math.Clamp(glass, 0, 1);
		BackgroundColor = glass > 0 ? Colors.Transparent : bg;
		bg = bg.WithAlpha((float)(1 - glass));
		if (Application.Current is { } app)
			app.UserAppTheme = dark ? AppTheme.Dark : AppTheme.Light;
		if (Window?.TitleBar is TitleBar bar)
		{
			bar.BackgroundColor = bg;
			bar.ForegroundColor = fg;
		}
#if WINDOWS
		// The minimise/maximise/close glyphs do not follow the title bar's foreground on their own.
		if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
		{
			if (glass > 0)
				native.SystemBackdrop ??= new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
			else
				native.SystemBackdrop = null;
			var glyph = Windows.UI.Color.FromArgb(255, (byte)(fg.Red * 255), (byte)(fg.Green * 255), (byte)(fg.Blue * 255));
			var caption = native.AppWindow.TitleBar;
			caption.ButtonForegroundColor = glyph;
			caption.ButtonInactiveForegroundColor = glyph;
			caption.ButtonHoverForegroundColor = glyph;
		}
#endif
	}

	void Start(int id, int cols, int rows)
	{
		if (!_pending.Remove(id, out var pending))
			return;
		var (command, folder) = pending;

		ConPtySession session;
		try
		{
			session = new ConPtySession(command, cols, rows, folder);
		}
		catch (Exception ex)
		{
			SendOutput(id, Encoding.UTF8.GetBytes($"Failed to start '{command}': {ex.Message}\r\n"));
			return;
		}

		_sessions[id] = session;
		// Block the reader until the UI thread has taken each chunk, so a noisy
		// shell cannot queue output faster than the web view can draw it.
		session.Output += data => Dispatcher.DispatchAsync(() => SendOutput(id, data)).Wait();
		session.Exited += () => Dispatcher.Dispatch(() =>
		{
			if (_sessions.TryRemove(id, out var ended))
				ended.Dispose();
			Web.SendRawMessage($"{{\"t\":\"exit\",\"id\":{id}}}");
		});
	}

	void SendOutput(int id, byte[] data) =>
		Web.SendRawMessage($"{{\"t\":\"out\",\"id\":{id},\"d\":\"{Convert.ToBase64String(data)}\"}}");

	static List<Shell> FindShells()
	{
		var shells = new List<Shell>();
		if (OnPath("pwsh.exe"))
			shells.Add(new("PowerShell 7", "pwsh.exe -NoLogo", ShellKind.PowerShell));
		shells.Add(new("Windows PowerShell", "powershell.exe -NoLogo", ShellKind.PowerShell));
		shells.Add(new("Command Prompt", "cmd.exe", ShellKind.Cmd));
		if (OnPath("wsl.exe"))
			shells.Add(new("WSL", "wsl.exe", ShellKind.Wsl));
		return shells;
	}

	static bool OnPath(string exe) =>
		(Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Any(dir =>
			{
				try { return File.Exists(Path.Combine(dir.Trim(), exe)); }
				catch (ArgumentException) { return false; }
			});
}
