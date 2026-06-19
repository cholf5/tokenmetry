using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CCUsage.App.ViewModels;

namespace CCUsage.App.Services;

public sealed class ReportCsvFormatter
{
    private static readonly string[] Headers =
    {
        "Date",
        "Cost",
        "Total Tokens",
        "Input Tokens",
        "Output Tokens",
        "Cache Creation Tokens",
        "Cache Read Tokens",
        "Agents",
        "Models"
    };

    public string Format(IEnumerable<ReportsDailyRow> rows)
    {
        var builder = new StringBuilder();
        AppendRecord(builder, Headers);

        foreach (var row in rows)
        {
            AppendRecord(builder, new[]
            {
                row.Date,
                row.Cost.ToString(CultureInfo.InvariantCulture),
                row.TotalTokens.ToString(CultureInfo.InvariantCulture),
                row.InputTokens.ToString(CultureInfo.InvariantCulture),
                row.OutputTokens.ToString(CultureInfo.InvariantCulture),
                row.CacheCreationTokens.ToString(CultureInfo.InvariantCulture),
                row.CacheReadTokens.ToString(CultureInfo.InvariantCulture),
                row.AgentsText,
                row.ModelsText
            });
        }

        return builder.ToString();
    }

    private static void AppendRecord(StringBuilder builder, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Escape(values[i]));
        }

        builder.Append("\r\n");
    }

    private static string Escape(string value)
    {
        if (!NeedsQuoting(value))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static bool NeedsQuoting(string value)
    {
        return value.Any(character => character is ',' or '"' or '\r' or '\n');
    }
}

public interface IReportCsvExporter
{
    Task<ReportCsvExportResult> ExportAsync(IReadOnlyList<ReportsDailyRow> rows, CancellationToken ct = default);
}

public sealed record ReportCsvExportResult(bool Success, bool Canceled, int RowCount, string ErrorMessage)
{
    public static ReportCsvExportResult Exported(int rowCount) => new(true, false, rowCount, string.Empty);

    public static ReportCsvExportResult CanceledByUser() => new(false, true, 0, string.Empty);

    public static ReportCsvExportResult Failed(string message) => new(false, false, 0, message);
}

public sealed class ReportCsvExporter : IReportCsvExporter
{
    private readonly ReportCsvFormatter _formatter;

    public ReportCsvExporter(ReportCsvFormatter formatter)
    {
        _formatter = formatter;
    }

    public async Task<ReportCsvExportResult> ExportAsync(IReadOnlyList<ReportsDailyRow> rows, CancellationToken ct = default)
    {
        var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null)
        {
            return ReportCsvExportResult.Failed("main window is not available");
        }

        if (!mainWindow.StorageProvider.CanSave)
        {
            return ReportCsvExportResult.Failed("save file dialog is not available");
        }

        var file = await mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 Reports CSV",
            SuggestedFileName = $"ccusage-reports-{DateTime.Now:yyyyMMdd}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV 文件")
                {
                    Patterns = new[] { "*.csv" },
                    MimeTypes = new[] { "text/csv" }
                }
            }
        });

        if (file == null)
        {
            return ReportCsvExportResult.CanceledByUser();
        }

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }

        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(_formatter.Format(rows).AsMemory(), ct);
        await writer.FlushAsync(ct);

        return ReportCsvExportResult.Exported(rows.Count);
    }
}

public sealed class DisabledReportCsvExporter : IReportCsvExporter
{
    public Task<ReportCsvExportResult> ExportAsync(IReadOnlyList<ReportsDailyRow> rows, CancellationToken ct = default)
    {
        return Task.FromResult(ReportCsvExportResult.Failed("CSV export is not available"));
    }
}
