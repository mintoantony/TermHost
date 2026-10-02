using System.Text.Json;

namespace TermHost;

public partial class MainPage : ContentPage
{
	static readonly Color ActiveColor = Color.FromArgb("#0E639C");
	static readonly Color IdleColor = Color.FromArgb("#444444");

	readonly List<(string Name, string Command)> _shells = FindShells();
	// Terminals announced to the web view but not yet started: id -> command line.
	readonly Dictionary<int, string> _pending = new();
	readonly Dictionary<int, ConPtySession> _sessions = new();
	int _nextId = 1;

	public MainPage()
	{
		InitializeComponent();
		ShellPicker.ItemsSource = _shells.Select(s => s.Name).ToList();
		ShellPicker.SelectedIndex = 0;
		SetLayout("tabs");
	}

	public void CloseAll()
	{
		foreach (var session in _sessions.Values)
			session.Dispose();
		_sessions.Clear();
	}

	void OnNewClicked(object? sender, EventArgs e) => NewTerminal();

	void OnTabsClicked(object? sender, EventArgs e) => SetLayout("tabs");

	void OnTileClicked(object? sender, EventArgs e) => SetLayout("grid");

	void SetLayout(string mode)
	{
		TabsButton.BackgroundColor = mode == "tabs" ? ActiveColor : IdleColor;
		TileButton.BackgroundColor = mode == "grid" ? ActiveColor : IdleColor;
		Web.SendRawMessage($"{{\"t\":\"layout\",\"mode\":\"{mode}\"}}");
	}

	// The web view sizes the terminal first, then asks us to start the shell at that size.
	void NewTerminal()
	{
		var shell = _shells[Math.Max(ShellPicker.SelectedIndex, 0)];
		int id = _nextId++;
		_pending[id] = shell.Command;
		var title = JsonEncodedText.Encode($"{shell.Name} {id}");
		Web.SendRawMessage($"{{\"t\":\"new\",\"id\":{id},\"title\":\"{title}\"}}");
	}

	void OnRawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Message))
			return;
		using var doc = JsonDocument.Parse(e.Message);
		var msg = doc.RootElement;
		var type = msg.GetProperty("t").GetString();

		if (type == "ready")
		{
			NewTerminal();
			return;
		}

		int id = msg.GetProperty("id").GetInt32();
		switch (type)
		{
			case "start":
				Start(id, msg.GetProperty("cols").GetInt32(), msg.GetProperty("rows").GetInt32());
				break;
			case "in":
				if (_sessions.TryGetValue(id, out var target))
					target.Write(msg.GetProperty("d").GetString() ?? "");
				break;
			case "resize":
				if (_sessions.TryGetValue(id, out var resized))
					resized.Resize(msg.GetProperty("cols").GetInt32(), msg.GetProperty("rows").GetInt32());
				break;
			case "close":
				_pending.Remove(id);
				if (_sessions.Remove(id, out var closed))
					closed.Dispose();
				break;
		}
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
			SendOutput(id, System.Text.Encoding.UTF8.GetBytes($"Failed to start '{command}': {ex.Message}\r\n"));
			return;
		}

		_sessions[id] = session;
		// Block the reader until the UI thread has taken each chunk, so a noisy
		// shell cannot queue output faster than the web view can draw it.
		session.Output += data => Dispatcher.DispatchAsync(() => SendOutput(id, data)).Wait();
		session.Exited += () => Dispatcher.Dispatch(() =>
		{
			if (_sessions.Remove(id, out var ended))
				ended.Dispose();
			Web.SendRawMessage($"{{\"t\":\"exit\",\"id\":{id}}}");
		});
	}

	void SendOutput(int id, byte[] data) =>
		Web.SendRawMessage($"{{\"t\":\"out\",\"id\":{id},\"d\":\"{Convert.ToBase64String(data)}\"}}");

	static List<(string Name, string Command)> FindShells()
	{
		var shells = new List<(string, string)>();
		if (OnPath("pwsh.exe"))
			shells.Add(("PowerShell 7", "pwsh.exe -NoLogo"));
		shells.Add(("Windows PowerShell", "powershell.exe -NoLogo"));
		shells.Add(("Command Prompt", "cmd.exe"));
		if (OnPath("wsl.exe"))
			shells.Add(("WSL", "wsl.exe"));
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
