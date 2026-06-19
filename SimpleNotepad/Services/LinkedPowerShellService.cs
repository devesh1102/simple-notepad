using System.Diagnostics;
using System.IO;
using System.Text;

namespace SimpleNotepad.Services;

public enum LinkedPowerShellTarget
{
    Normal,
    Admin
}

public sealed class LinkedPowerShellService : IDisposable
{
    private const string AppFolderName = "SimpleNotepad";
    private const string LinkedShellFolderName = "linked-powershell";

    private readonly string _linkedShellFolder;
    private readonly Dictionary<LinkedPowerShellTarget, LinkedPowerShellSession> _sessions = [];

    public LinkedPowerShellService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _linkedShellFolder = Path.Combine(localAppData, AppFolderName, LinkedShellFolderName);
    }

    public string GetSessionDescription(LinkedPowerShellTarget target)
    {
        var session = GetSession(target);
        if (target == LinkedPowerShellTarget.Admin)
        {
            return IsRunning(target) ? $"Admin window ({session.Id})" : "Admin (new elevated command window)";
        }

        var elevation = "Normal";
        return $"{elevation} ({(IsRunning(target) ? session.Id : "new session")})";
    }

    public bool IsRunning(LinkedPowerShellTarget target)
    {
        var session = GetSession(target);
        return session.Process is { HasExited: false };
    }

    public async Task SendCommandAsync(LinkedPowerShellTarget target, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Cannot send an empty command to linked PowerShell.");
        }

        if (target == LinkedPowerShellTarget.Admin)
        {
            StartAdminCommand(command);
            return;
        }

        EnsureStarted(target);
        var session = GetSession(target);
        var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        await File.AppendAllTextAsync(session.QueuePath, encodedCommand + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
    }

    public void Restart(LinkedPowerShellTarget target)
    {
        if (target == LinkedPowerShellTarget.Admin)
        {
            throw new InvalidOperationException("Admin PowerShell restart is not available. Close the elevated PowerShell window and send the command again.");
        }

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

        if (target == LinkedPowerShellTarget.Admin)
        {
            throw new InvalidOperationException("Admin PowerShell must be closed from its own elevated window.");
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
            if (process.HasExited)
            {
                session.Process = null;
                return;
            }

            throw new InvalidOperationException("Linked PowerShell did not stop.", exception);
        }

        if (!process.HasExited)
        {
            process.WaitForExit(milliseconds: 500);
        }

        if (!process.HasExited)
        {
            throw new InvalidOperationException("Linked PowerShell did not stop.");
        }

        session.Process = null;
    }

    public string GetStatus()
    {
        var normal = IsRunning(LinkedPowerShellTarget.Normal) ? "normal connected" : "normal disconnected";
        var admin = IsRunning(LinkedPowerShellTarget.Admin) ? "admin window open" : "admin disconnected";
        return $"PS: {normal}, {admin}";
    }

    public void Dispose()
    {
        try
        {
            Stop(LinkedPowerShellTarget.Normal);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void EnsureStarted(LinkedPowerShellTarget target)
    {
        if (target == LinkedPowerShellTarget.Admin)
        {
            throw new InvalidOperationException("Admin PowerShell uses a new elevated command window for each confirmed command.");
        }

        if (IsRunning(target))
        {
            return;
        }

        Directory.CreateDirectory(_linkedShellFolder);
        var session = GetSession(target);
        File.WriteAllText(session.QueuePath, string.Empty, new UTF8Encoding(false));

        var encodedStartupScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(CreateStartupScript(session, target)));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -EncodedCommand {encodedStartupScript}",
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

    private void StartAdminCommand(string command)
    {
        var session = GetSession(LinkedPowerShellTarget.Admin);
        var script = CreateAdminCommandScript(session, command);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        session.Process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Admin PowerShell did not start.");
    }

    private LinkedPowerShellSession GetSession(LinkedPowerShellTarget target)
    {
        if (_sessions.TryGetValue(target, out var session))
        {
            return session;
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var queuePath = Path.Combine(_linkedShellFolder, $"{target.ToString().ToLowerInvariant()}-{id}.commands");
        session = new LinkedPowerShellSession(id, queuePath);
        _sessions[target] = session;
        return session;
    }

    private static string CreateStartupScript(LinkedPowerShellSession session, LinkedPowerShellTarget target)
    {
        var title = target == LinkedPowerShellTarget.Admin
            ? $"SimpleNotepad Linked PowerShell ADMIN - {session.Id}"
            : $"SimpleNotepad Linked PowerShell - {session.Id}";
        var escapedTitle = EscapePowerShellSingleQuotedString(title);
        var escapedQueuePath = EscapePowerShellSingleQuotedString(session.QueuePath);

        return $$"""
$queuePath = '{{escapedQueuePath}}'
$host.UI.RawUI.WindowTitle = '{{escapedTitle}}'
Write-Host 'Linked to SimpleNotepad. Waiting for selected commands...' -ForegroundColor Cyan
if (!(Test-Path -LiteralPath $queuePath)) {
    New-Item -ItemType File -Path $queuePath -Force | Out-Null
}
$position = 0L
while ($true) {
    Start-Sleep -Milliseconds 350
    if (!(Test-Path -LiteralPath $queuePath)) {
        continue
    }

    $stream = [System.IO.File]::Open($queuePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -lt $position) {
            $position = 0L
        }

        $stream.Seek($position, [System.IO.SeekOrigin]::Begin) | Out-Null
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $false, 4096, $true)
        try {
            while (($line = $reader.ReadLine()) -ne $null) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }

                try {
                    $command = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($line))
                }
                catch {
                    Write-Warning 'SimpleNotepad sent a command that could not be decoded.'
                    continue
                }

                Write-Host ''
                Write-Host ('SimpleNotepad> ' + $command) -ForegroundColor Yellow
                try {
                    Invoke-Expression $command
                }
                catch {
                    Write-Error $_
                }
            }
        }
        finally {
            $reader.Dispose()
        }

        $position = $stream.Position
    }
    finally {
        $stream.Dispose()
    }
}
""";
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

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private sealed class LinkedPowerShellSession(string id, string queuePath)
    {
        public string Id { get; } = id;
        public string QueuePath { get; } = queuePath;
        public Process? Process { get; set; }
    }
}
