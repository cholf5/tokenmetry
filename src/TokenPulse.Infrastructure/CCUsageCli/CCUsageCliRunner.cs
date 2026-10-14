using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace TokenPulse.Infrastructure.CCUsageCli;

public class CCUsageCliRunner
{
    private readonly string _command;
    private readonly string _arguments;
    private readonly string? _workingDirectory;

    public CCUsageCliRunner(string command, string arguments, string? workingDirectory = null)
    {
        _command = command;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
    }

    public async Task<string> GetOutputAsync(CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommand(_command),
            Arguments = _arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(_workingDirectory))
        {
            startInfo.WorkingDirectory = _workingDirectory;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process exited with code {process.ExitCode}: {error}");
        }

        return output;
    }

    private static string ResolveCommand(string command)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            !string.IsNullOrEmpty(Path.GetExtension(command)) ||
            File.Exists(command))
        {
            return command;
        }

        var pathExtensions = GetWindowsExecutableExtensions();

        if (HasDirectoryPart(command))
        {
            var directCandidate = ResolveCommandWithExtensions(command, pathExtensions);
            return directCandidate ?? command;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = ResolveCommandWithExtensions(Path.Combine(directory, command), pathExtensions);
            if (candidate != null)
            {
                return candidate;
            }
        }

        return command;
    }

    private static string[] GetWindowsExecutableExtensions()
    {
        return (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToArray();
    }

    private static string? ResolveCommandWithExtensions(string command, string[] extensions)
    {
        foreach (var extension in extensions)
        {
            var candidate = command + extension;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasDirectoryPart(string command)
    {
        return Path.IsPathRooted(command) ||
            command.Contains(Path.DirectorySeparatorChar) ||
            command.Contains(Path.AltDirectorySeparatorChar);
    }
}
