using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;
using Serilog;
using Serilog.Events;
using Thanos.Core;

namespace Thanos.Infrastructure;

public interface IRecycleBinFileOperation
{
    void Delete(string path);
}

public sealed class RecycleBinFileOperation : IRecycleBinFileOperation
{
    public void Delete(string path) => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
}

public sealed class WindowsPhotoDeletionService(IRecycleBinFileOperation? recycleBin = null) : IPhotoDeletionService
{
    private readonly IRecycleBinFileOperation _recycleBin = recycleBin ?? new RecycleBinFileOperation();

    public async Task<DeleteResult> DeleteAsync(IReadOnlyCollection<string> paths, DeleteMode mode, CancellationToken cancellationToken)
    {
        Log.Information("Delete requested. Mode={DeleteMode} Count={DeleteCount}", mode, paths.Count);
        var results = new List<DeletionItemResult>(paths.Count);
        foreach (var suppliedPath in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(suppliedPath);
            try
            {
                await Task.Run(() =>
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("The photo no longer exists.", path);
                    if (mode == DeleteMode.RecycleBin)
                        _recycleBin.Delete(path);
                    else
                        File.Delete(path);
                }, cancellationToken);
                results.Add(new(path, true));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                Log.Warning(exception, "Delete failed for {ImagePath}", path);
                results.Add(new(path, false, exception.Message));
            }
        }
        var result = new DeleteResult(results);
        Log.Information("Delete complete. Success={SuccessCount} Failed={FailedCount}", result.Succeeded, result.Failed);
        return result;
    }
}

public sealed class LogViewerService : ILogViewerService
{
    public async Task<string> ReadNewestAsync(CancellationToken cancellationToken)
    {
        AppPaths.EnsureCreated();
        var newest = Directory.EnumerateFiles(AppPaths.Logs, "thanos-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return newest is null ? "No logs yet." : await File.ReadAllTextAsync(newest, cancellationToken);
    }

    public void OpenFolder()
    {
        AppPaths.EnsureCreated();
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Logs) { UseShellExecute = true });
    }

    public int ClearOlderThan(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(AppPaths.Logs, "thanos-*.log"))
        {
            if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
            File.Delete(path);
            deleted++;
        }
        return deleted;
    }
}

public static class Logging
{
    public static void Configure()
    {
        AppPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(AppPaths.Logs, "thanos-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 21,
                retainedFileTimeLimit: TimeSpan.FromDays(21),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
