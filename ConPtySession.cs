using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TermHost;

/// <summary>A shell process attached to a Windows pseudo console (ConPTY).</summary>
public sealed class ConPtySession : IDisposable
{
	readonly IntPtr _pty;
	readonly IntPtr _process;

	/// <summary>Process id of the shell.</summary>
	public int ProcessId { get; private set; }
	readonly FileStream _input;
	readonly FileStream _output;
	int _disposed;

	/// <summary>Raw VT output from the shell. Raised on a background thread.</summary>
	public event Action<byte[]>? Output;

	/// <summary>The shell process ended. Raised on a background thread.</summary>
	public event Action? Exited;

	public ConPtySession(string commandLine, int cols, int rows, string? workingDirectory = null)
	{
		if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) ||
			!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
			throw new Win32Exception();

		int hr = CreatePseudoConsole(Size(cols, rows), inRead, outWrite, 0, out _pty);
		// The pseudo console holds its own copies of these ends.
		inRead.Dispose();
		outWrite.Dispose();
		if (hr != 0)
			throw new Win32Exception(hr);

		_input = new FileStream(inWrite, FileAccess.Write);
		_output = new FileStream(outRead, FileAccess.Read);
		_process = StartProcess(commandLine, workingDirectory);

		new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY read" }.Start();
		new Thread(WaitForExit) { IsBackground = true, Name = "ConPTY wait" }.Start();
	}

	public void Write(string text)
	{
		if (_disposed != 0)
			return;
		try
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			_input.Write(bytes, 0, bytes.Length);
			_input.Flush();
		}
		catch (IOException) { }
		catch (ObjectDisposedException) { }
	}

	public void Resize(int cols, int rows)
	{
		if (_disposed == 0 && cols > 0 && rows > 0)
			ResizePseudoConsole(_pty, Size(cols, rows));
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		// Closing the pseudo console terminates the attached shell and ends the read loop.
		ClosePseudoConsole(_pty);
		_input.Dispose();
		CloseHandle(_process);
	}

	void ReadLoop()
	{
		var buffer = new byte[16 * 1024];
		try
		{
			int n;
			while ((n = _output.Read(buffer, 0, buffer.Length)) > 0)
				Output?.Invoke(buffer.AsSpan(0, n).ToArray());
		}
		catch (IOException) { }
		catch (ObjectDisposedException) { }
		finally
		{
			_output.Dispose();
		}
	}

	void WaitForExit()
	{
		WaitForSingleObject(_process, INFINITE);
		Exited?.Invoke();
	}

	IntPtr StartProcess(string commandLine, string? workingDirectory)
	{
		var size = IntPtr.Zero;
		InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
		var attributes = Marshal.AllocHGlobal(size);
		try
		{
			if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
				throw new Win32Exception();
			try
			{
				if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
						_pty, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
					throw new Win32Exception();

				var startup = new STARTUPINFOEX { lpAttributeList = attributes };
				startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
				// Null std handles stop the child inheriting redirected ones from us
				// (e.g. under "dotnet run"), which would bypass the pseudo console.
				startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

				if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
						EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory, ref startup, out var info))
					throw new Win32Exception();

				CloseHandle(info.hThread);
				ProcessId = info.dwProcessId;
				return info.hProcess;
			}
			finally
			{
				DeleteProcThreadAttributeList(attributes);
			}
		}
		catch
		{
			ClosePseudoConsole(_pty);
			_input.Dispose();
			_output.Dispose();
			throw;
		}
		finally
		{
			Marshal.FreeHGlobal(attributes);
		}
	}

	static COORD Size(int cols, int rows) =>
		new() { X = (short)Math.Clamp(cols, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };

	const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
	const int STARTF_USESTDHANDLES = 0x00000100;
	const uint INFINITE = 0xFFFFFFFF;
	static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

	[StructLayout(LayoutKind.Sequential)]
	struct COORD
	{
		public short X;
		public short Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct STARTUPINFO
	{
		public int cb;
		public IntPtr lpReserved;
		public IntPtr lpDesktop;
		public IntPtr lpTitle;
		public int dwX;
		public int dwY;
		public int dwXSize;
		public int dwYSize;
		public int dwXCountChars;
		public int dwYCountChars;
		public int dwFillAttribute;
		public int dwFlags;
		public short wShowWindow;
		public short cbReserved2;
		public IntPtr lpReserved2;
		public IntPtr hStdInput;
		public IntPtr hStdOutput;
		public IntPtr hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct STARTUPINFOEX
	{
		public STARTUPINFO StartupInfo;
		public IntPtr lpAttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct PROCESS_INFORMATION
	{
		public IntPtr hProcess;
		public IntPtr hThread;
		public int dwProcessId;
		public int dwThreadId;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

	[DllImport("kernel32.dll")]
	static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

	[DllImport("kernel32.dll")]
	static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

	[DllImport("kernel32.dll")]
	static extern void ClosePseudoConsole(IntPtr hPC);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

	[DllImport("kernel32.dll")]
	static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

	[DllImport("kernel32.dll")]
	static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll")]
	static extern bool CloseHandle(IntPtr hObject);
}
