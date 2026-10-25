using Thanos.Core;
using Thanos.Infrastructure;

namespace Thanos.Infrastructure.Tests;

public sealed class DeletionTests
{
    [Fact]
    public async Task Permanent_delete_removes_file_and_reports_missing_file()
    {
        var directory = Directory.CreateTempSubdirectory("thanos-delete-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var existing = Path.Combine(directory.FullName, "photo.jpg");
            await File.WriteAllTextAsync(existing, "fixture", cancellationToken);
            var missing = Path.Combine(directory.FullName, "missing.jpg");
            var result = await new WindowsPhotoDeletionService().DeleteAsync([existing, missing], DeleteMode.Permanent, cancellationToken);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.False(File.Exists(existing));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Recycle_bin_uses_shell_abstraction()
    {
        var directory = Directory.CreateTempSubdirectory("thanos-recycle-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var path = Path.Combine(directory.FullName, "photo.jpg");
            await File.WriteAllTextAsync(path, "fixture", cancellationToken);
            var shell = new FakeRecycleBin();
            var result = await new WindowsPhotoDeletionService(shell).DeleteAsync([path], DeleteMode.RecycleBin, cancellationToken);
            Assert.Equal(path, shell.DeletedPath);
            Assert.Equal(1, result.Succeeded);
        }
        finally { directory.Delete(true); }
    }

    private sealed class FakeRecycleBin : IRecycleBinFileOperation
    {
        public string? DeletedPath { get; private set; }
        public void Delete(string path) => DeletedPath = path;
    }
}
