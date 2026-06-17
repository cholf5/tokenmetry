using System.Diagnostics;

namespace CCUsage.Infrastructure.CCUsageCli;

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
            FileName = _command,
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
}
