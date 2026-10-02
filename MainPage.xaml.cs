using System.Collections.Concurrent;
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
	// Terminals announced to the web view but not yet started: id -> command line.
	readonly Dictionary<int, string> _pending = new();
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

	void SendInit()
	{
		var init = new JsonObject
		{
			["t"] = "init",
			["shells"] = new JsonArray(_shells.Select(s => (JsonNode)s.Name).ToArray()),
			["settings"] = new JsonObject
			{
				["shell"] = DefaultShell.Name,
				["program"] = Preferences.Default.Get("program", DefaultProgram),
				["theme"] = Preferences.Default.Get("theme", "Catppuccin Mocha"),
				["layout"] = Preferences.Default.Get("layout", "tabs"),
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
	void NewTerminal()
	{
		var shell = DefaultShell;
		var program = Preferences.Default.Get("program", DefaultProgram).Trim();
		int id = _nextId++;
		_pending[id] = CommandLine(shell, program);
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
				SendInit();
				NewTerminal();
				WatchClaude();
				break;
			case "new":
				NewTerminal();
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
			case "settings":
				foreach (var key in new[] { "shell", "program", "theme", "layout" })
					Preferences.Default.Set(key, Text(key));
				break;
			case "chrome":
				ApplyChrome(Text("bg"), Text("fg"), msg.GetProperty("dark").GetBoolean());
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

	// Keeps the native window chrome in step with the theme chosen in the web view.
	void ApplyChrome(string background, string foreground, bool dark)
	{
		if (!Color.TryParse(background, out var bg) || !Color.TryParse(foreground, out var fg))
			return;
		BackgroundColor = bg;
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
		if (!_pending.Remove(id, out var command))
			return;

		ConPtySession session;
		try
		{
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			session = new ConPtySession(command, cols, rows, home);
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
