using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace TermHost;

// A terminal asked for from outside: by th.exe, by a script, or by "Open in TermHost".
// TermHost.exe and th.exe take the same options.
sealed class LaunchRequest
{
	// Null means the usual: the start folder, the default shell, the startup program.
	public string? Folder, Shell, Command, Title;
	// A window of its own instead of a tab in the TermHost that is already open.
	public bool NewWindow;
	public bool Help;

	public const string Usage = """
		Usage: th [folder] [--command <command>] [--shell <name>] [--title <text>] [--new-window]

		Opens a terminal as a new tab in the TermHost window that is open, or starts TermHost.

		  folder           Where the terminal starts. th: the current folder.
		  --command, -c    What runs in the terminal. Default: the startup program from the
		                   settings. "" for a plain shell.
		  --shell, -s      The terminal, as named in TermHost: "PowerShell 7", "Git Bash", ...
		  --title, -t      The tab's name.
		  --new-window     Open a new TermHost window instead of a tab.

		Example: th D:\repo -c "claude --resume"
		""";

	// Throws ArgumentException, with a message for the user, for options it does not know.
	public static LaunchRequest Parse(IReadOnlyList<string> args)
	{
		var request = new LaunchRequest();
		for (int i = 0; i < args.Count; i++)
		{
			string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
			switch (args[i])
			{
				case "-h" or "--help" or "/?":
					request.Help = true;
					break;
				case "--new-window":
					request.NewWindow = true;
					break;
				case "-c" or "--command":
					request.Command = Value();
					break;
				case "-s" or "--shell":
					request.Shell = Value();
					break;
				case "-t" or "--title":
					request.Title = Value();
					break;
				case var option when option.StartsWith('-') && option.Length > 1:
					throw new ArgumentException($"Unknown option {option}.");
				case var folder:
					if (request.Folder is not null)
						throw new ArgumentException($"Only one folder can be given, not also {folder}.");
					request.Folder = FullFolder(folder);
					break;
			}
		}
		return request;
	}

	// A folder relative to the caller's current folder. Only ever an existing directory.
	static string FullFolder(string folder)
	{
		// A drive from File Explorer arrives as "D:\" in quotes, which command-line parsing turns into D:" .
		folder = folder.Trim('"');
		if (folder.EndsWith(':'))
			folder += Path.DirectorySeparatorChar;
		try
		{
			if (Directory.Exists(folder))
				return Path.GetFullPath(folder);
		}
		catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
		{
		}
		throw new ArgumentException($"The folder {folder} does not exist.");
	}

	// The same request as arguments, for starting TermHost.exe with it.
	public IEnumerable<string> ToArgs()
	{
		if (Folder is not null)
			yield return Folder;
		foreach (var (option, value) in new[] { ("--command", Command), ("--shell", Shell), ("--title", Title) })
		{
			if (value is null)
				continue;
			yield return option;
			yield return value;
		}
		if (NewWindow)
			yield return "--new-window";
	}

	public string ToJson() => new JsonObject
	{
		["v"] = 1,
		["folder"] = Folder,
		["shell"] = Shell,
		["command"] = Command,
		["title"] = Title,
	}.ToJsonString();

	public static LaunchRequest FromJson(string json)
	{
		var node = JsonNode.Parse(json) ?? throw new FormatException("Empty request.");
		string? Text(string name) => node[name]?.GetValue<string>();
		return new LaunchRequest { Folder = Text("folder"), Shell = Text("shell"), Command = Text("command"), Title = Text("title") };
	}
}

// How a launch reaches the TermHost window that is already open: a named pipe that only
// this user can open, served by the first window, which also holds a mutex to say so.
static class LaunchPipe
{
	static readonly string User = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
	static readonly string PipeName = $"TermHost-{User}";
	static readonly string MutexName = $@"Local\TermHost-{User}";
	static Mutex? _held;

	// Whether a TermHost window serves the pipe.
	public static bool Running()
	{
		if (!Mutex.TryOpenExisting(MutexName, out var mutex))
			return false;
		mutex.Dispose();
		return true;
	}

	// Sends the request and returns the window's answer: null when it opened the terminal, else why not.
	// Throws IOException or TimeoutException when the window does not answer.
	public static string? Send(LaunchRequest request, int timeoutMs)
	{
		// Lets the window come to the front, which Windows allows only a process that has been allowed.
		AllowSetForegroundWindow(ASFW_ANY);
		using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
		pipe.Connect(timeoutMs);
		using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
		writer.WriteLine(request.ToJson());
		using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
		var reply = JsonNode.Parse(reader.ReadLine() ?? throw new IOException("TermHost closed the connection."));
		return reply?["ok"]?.GetValue<bool>() == true ? null : reply?["error"]?.GetValue<string>() ?? "TermHost refused the request.";
	}

	// Serves the pipe if no other window does. Each request is handed to open, which returns null or why it was refused.
	public static bool Serve(Func<LaunchRequest, Task<string?>> open, CancellationToken stop)
	{
		var mutex = new Mutex(true, MutexName, out bool first);
		if (!first)
		{
			mutex.Dispose();
			return false;
		}
		_held = mutex;
		Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
					PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
				try
				{
					await pipe.WaitForConnectionAsync(stop);
				}
				catch (Exception e) when (e is OperationCanceledException or IOException)
				{
					pipe.Dispose();
					break;
				}
				_ = Answer(pipe, open);
			}
		});
		return true;
	}

	static async Task Answer(NamedPipeServerStream pipe, Func<LaunchRequest, Task<string?>> open)
	{
		using (pipe)
		{
			try
			{
				using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				var line = await reader.ReadLineAsync(timeout.Token);
				string? error;
				try
				{
					error = line is null ? "Empty request." : await open(LaunchRequest.FromJson(line));
				}
				catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
				{
					error = "The request could not be read.";
				}
				using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
				await writer.WriteLineAsync(new JsonObject { ["ok"] = error is null, ["error"] = error }.ToJsonString());
			}
			catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
			{
				// The caller went away.
			}
		}
	}

	const int ASFW_ANY = -1;

	[DllImport("user32.dll")]
	static extern bool AllowSetForegroundWindow(int dwProcessId);
}
