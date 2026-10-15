using System.Diagnostics;
using TermHost;

// th: opens a terminal in TermHost from a script or another program.
// Exit codes: 0 opened, 2 bad options or refused by TermHost, 3 TermHost could not be reached or started.

LaunchRequest request;
try
{
	request = LaunchRequest.Parse(args);
}
catch (ArgumentException e)
{
	Console.Error.WriteLine($"th: {e.Message}");
	Console.Error.WriteLine(LaunchRequest.Usage);
	return 2;
}
if (request.Help)
{
	Console.WriteLine(LaunchRequest.Usage);
	return 0;
}
// Unlike TermHost.exe, th starts where it is run from.
request.Folder ??= Environment.CurrentDirectory;

if (!request.NewWindow && LaunchPipe.Running())
{
	try
	{
		if (LaunchPipe.Send(request, 5000) is not { } error)
			return 0;
		Console.Error.WriteLine($"th: {error}");
		return 2;
	}
	catch (Exception e) when (e is IOException or TimeoutException)
	{
		Console.Error.WriteLine($"th: TermHost is open but does not answer ({e.Message}).");
		return 3;
	}
}

// No TermHost open, or a window of its own asked for: start one with the same request.
var exe = new[]
{
	Path.Combine(AppContext.BaseDirectory, "TermHost.exe"),
	Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TermHost", "TermHost.exe"),
}.FirstOrDefault(File.Exists);
if (exe is null)
{
	Console.Error.WriteLine("th: TermHost.exe was not found beside th.exe or in the install folder.");
	return 3;
}
var start = new ProcessStartInfo(exe) { UseShellExecute = false };
foreach (var arg in request.ToArgs())
	start.ArgumentList.Add(arg);
Process.Start(start)?.Dispose();
return 0;
