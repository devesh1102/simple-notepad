using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace SimpleNotepad.Services;

public enum LinkedPowerShellTarget
{
    Normal,
    Admin
}

public sealed class LinkedPowerShellService : IDisposable
{
    private const int SwRestore = 9;
    private const int InputKeyboard = 1;
    private const ushort KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;
    private const ushort VirtualKeyReturn = 0x0D;
    private readonly Dictionary<LinkedPowerShellTarget, LinkedPowerShellSession> _sessions = [];

    public string GetSessionDescription(LinkedPowerShellTarget target)
    {
        var session = GetSession(target);
        var elevation = target == LinkedPowerShellTarget.Admin ? "Admin" : "Normal";
        return $"{elevation} ({(IsRunning(target) ? session.Id : "new session")})";
    }

    public bool IsRunning(LinkedPowerShellTarget target)
    {
        var session = GetSession(target);
        return session.Process is { HasExited: false };
    }

    public Task SendCommandAsync(LinkedPowerShellTarget target, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Cannot send an empty command to linked PowerShell.");
        }

        if (target == LinkedPowerShellTarget.Admin)
        {
            return RunOnStaThreadAsync(() => StartAdminCommand(command));
        }

        return RunOnStaThreadAsync(() =>
        {
            if (!IsRunning(target))
            {
                StartNormalWithInitialCommand(command);
                return;
            }

            var process = GetSession(target).Process
                ?? throw new InvalidOperationException("Linked PowerShell is not running.");
            SendCommandToInteractiveWindow(process, command);
        });
    }

    public void Restart(LinkedPowerShellTarget target)
    {
        Stop(target);
        EnsureStarted(target);
    }

    public void Stop(LinkedPowerShellTarget target)
    {
        var session = GetSession(target);
        if (session.Process is not { HasExited: false } process)
        {
            session.Process = null;
            return;
        }

        try
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(milliseconds: 1500))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            if (!process.HasExited)
            {
                throw new InvalidOperationException("Linked PowerShell did not stop. Close it from its own window and try again.", exception);
            }
        }

        session.Process = null;
    }

    public string GetStatus()
    {
        var normal = IsRunning(LinkedPowerShellTarget.Normal) ? "normal connected" : "normal disconnected";
        var admin = IsRunning(LinkedPowerShellTarget.Admin) ? "admin connected" : "admin disconnected";
        return $"PS: {normal}, {admin}";
    }

    public void Dispose()
    {
        foreach (var target in _sessions.Keys.ToList())
        {
            try
            {
                Stop(target);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void EnsureStarted(LinkedPowerShellTarget target)
    {
        if (IsRunning(target))
        {
            return;
        }

        var session = GetSession(target);
        var title = target == LinkedPowerShellTarget.Admin
            ? $"SimpleNotepad Linked PowerShell ADMIN - {session.Id}"
            : $"SimpleNotepad Linked PowerShell - {session.Id}";
        var startupCommand = $"$host.UI.RawUI.WindowTitle='{EscapePowerShellSingleQuotedString(title)}'; Write-Host 'Linked to SimpleNotepad. You can use this as a normal PowerShell window.' -ForegroundColor Cyan";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -NoProfile -ExecutionPolicy Bypass -Command \"{startupCommand}\"",
            UseShellExecute = target == LinkedPowerShellTarget.Admin,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (target == LinkedPowerShellTarget.Admin)
        {
            startInfo.Verb = "runas";
        }

        session.Process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Linked PowerShell did not start.");
    }

    private void StartNormalWithInitialCommand(string command)
    {
        var session = GetSession(LinkedPowerShellTarget.Normal);
        var script = CreateNormalStartupScript(session, command);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        session.Process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Linked PowerShell did not start.");
    }

    private void StartAdminCommand(string command)
    {
        var session = GetSession(LinkedPowerShellTarget.Admin);
        var script = CreateAdminCommandScript(session, command);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        session.Process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Admin PowerShell did not start.");
    }

    private static void SendCommandToInteractiveWindow(Process process, string command)
    {
        var windowHandle = WaitForMainWindowHandle(process);
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Linked PowerShell window is not ready.");
        }

        ShowWindow(windowHandle, SwRestore);
        if (!SetForegroundWindow(windowHandle))
        {
            throw new InvalidOperationException("Simple Notepad could not focus linked PowerShell.");
        }

        Thread.Sleep(millisecondsTimeout: 250);
        var previousClipboard = TryCreateClipboardSnapshot();
        try
        {
            Forms.Clipboard.SetText(command, Forms.TextDataFormat.UnicodeText);
            Thread.Sleep(millisecondsTimeout: 100);
            SendPasteAndEnterInput();
            Thread.Sleep(millisecondsTimeout: 250);
        }
        catch (ExternalException exception)
        {
            throw new InvalidOperationException("Simple Notepad could not access the clipboard to send the command.", exception);
        }
        finally
        {
            if (previousClipboard is not null)
            {
                TryRestoreClipboardData(previousClipboard);
            }
        }
    }

    private static IntPtr WaitForMainWindowHandle(Process process)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            process.Refresh();
            if (process.HasExited)
            {
                return IntPtr.Zero;
            }

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }

        process.Refresh();
        return process.MainWindowHandle;
    }

    private LinkedPowerShellSession GetSession(LinkedPowerShellTarget target)
    {
        if (_sessions.TryGetValue(target, out var session))
        {
            return session;
        }

        session = new LinkedPowerShellSession(Guid.NewGuid().ToString("N")[..8]);
        _sessions[target] = session;
        return session;
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static Forms.DataObject? TryCreateClipboardSnapshot()
    {
        try
        {
            var dataObject = Forms.Clipboard.GetDataObject();
            if (dataObject is null)
            {
                return null;
            }

            var snapshot = new Forms.DataObject();
            foreach (var format in dataObject.GetFormats(autoConvert: false))
            {
                try
                {
                    snapshot.SetData(format, dataObject.GetData(format, autoConvert: false));
                }
                catch (ExternalException)
                {
                }
            }

            return snapshot;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static void TryRestoreClipboardData(Forms.IDataObject data)
    {
        try
        {
            Forms.Clipboard.SetDataObject(data, copy: true);
        }
        catch (ExternalException)
        {
        }
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string CreateAdminCommandScript(LinkedPowerShellSession session, string command)
    {
        var escapedTitle = EscapePowerShellSingleQuotedString($"SimpleNotepad Admin PowerShell - {session.Id}");
        var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        return $$"""
$host.UI.RawUI.WindowTitle = '{{escapedTitle}}'
$command = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedCommand}}'))
Write-Host 'SimpleNotepad admin command' -ForegroundColor Yellow
Write-Host $command -ForegroundColor Yellow
try {
    Invoke-Expression $command
}
catch {
    Write-Error $_
}
""";
    }

    private static string CreateNormalStartupScript(LinkedPowerShellSession session, string command)
    {
        var escapedTitle = EscapePowerShellSingleQuotedString($"SimpleNotepad Linked PowerShell - {session.Id}");
        var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        return $$"""
$host.UI.RawUI.WindowTitle = '{{escapedTitle}}'
Write-Host 'Linked to SimpleNotepad. You can use this as a normal PowerShell window.' -ForegroundColor Cyan
$command = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedCommand}}'))
Write-Host ('SimpleNotepad> ' + $command) -ForegroundColor Yellow
try {
    Invoke-Expression $command
}
catch {
    Write-Error $_
}
""";
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, Input[] pInputs, int cbSize);

    private static void SendPasteAndEnterInput()
    {
        var inputs = new[]
        {
            CreateKeyInput(VirtualKeyControl, keyUp: false),
            CreateKeyInput(VirtualKeyV, keyUp: false),
            CreateKeyInput(VirtualKeyV, keyUp: true),
            CreateKeyInput(VirtualKeyControl, keyUp: true),
            CreateKeyInput(VirtualKeyReturn, keyUp: false),
            CreateKeyInput(VirtualKeyReturn, keyUp: true)
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException("Simple Notepad could not send input to linked PowerShell.");
        }
    }

    private static Input CreateKeyInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                KeyboardInput = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : (ushort)0
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput KeyboardInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private sealed class LinkedPowerShellSession(string id)
    {
        public string Id { get; } = id;
        public Process? Process { get; set; }
    }
}
