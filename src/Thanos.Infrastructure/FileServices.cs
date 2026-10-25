using System.Security.Cryptography;
using System.Text;
using Thanos.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Thanos.Infrastructure;

public static class ImageFileTypes
{
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".webp", ".bmp", ".dib", ".gif",
        ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".hdp",
        ".jp2", ".j2k", ".jpf", ".jpx", ".dng", ".3fr", ".ari", ".arw", ".bay",
        ".cap", ".cr2", ".cr3", ".crw", ".dcr", ".dcs", ".drf", ".eip", ".erf",
        ".fff", ".gpr", ".iiq", ".k25", ".kdc", ".mdc", ".mef", ".mos", ".mrw",
        ".nef", ".nrw", ".orf", ".pef", ".ptx", ".pxn", ".r3d", ".raf", ".raw",
        ".rw2", ".rwl", ".rwz", ".sr2", ".srf", ".srw", ".x3f"
    };

    public static bool IsSupportedExtension(string path) => Extensions.Contains(Path.GetExtension(path));
}

internal static class WindowsImageLoader
{
    public static async Task<DecodedImage> LoadAsync(string imagePath, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(cancellationToken);
        using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Ignore,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken);
        var byteCount = checked(bitmap.PixelWidth * bitmap.PixelHeight * 4);
        var buffer = new Windows.Storage.Streams.Buffer(checked((uint)byteCount));
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[checked((int)buffer.Length)];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(pixels);
        return new(checked((int)bitmap.PixelWidth), checked((int)bitmap.PixelHeight), pixels);
    }

    public static async Task<(int Width, int Height)> IdentifyAsync(string imagePath, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(cancellationToken);
        using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        return (checked((int)decoder.OrientedPixelWidth), checked((int)decoder.OrientedPixelHeight));
    }

    public static async Task SaveThumbnailAsync(string imagePath, string targetPath, int size, CancellationToken cancellationToken)
    {
        var sourceFile = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(cancellationToken);
        using var sourceStream = await sourceFile.OpenReadAsync().AsTask(cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(sourceStream).AsTask(cancellationToken);
        var sourceWidth = decoder.OrientedPixelWidth;
        var sourceHeight = decoder.OrientedPixelHeight;
        var scale = size / (double)Math.Max(sourceWidth, sourceHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = checked((uint)Math.Max(1, Math.Round(sourceWidth * scale))),
            ScaledHeight = checked((uint)Math.Max(1, Math.Round(sourceHeight * scale))),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken);

        await File.WriteAllBytesAsync(targetPath, [], cancellationToken);
        var targetFile = await StorageFile.GetFileFromPathAsync(targetPath).AsTask(cancellationToken);
        using var targetStream = await targetFile.OpenAsync(FileAccessMode.ReadWrite).AsTask(cancellationToken);
        targetStream.Size = 0;
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, targetStream).AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);
    }
}

internal sealed record DecodedImage(int Width, int Height, byte[] RgbaPixels);

public sealed class FileEnumerationService : IFileEnumerationService
{
    public async IAsyncEnumerable<FileCandidate> EnumerateAsync(
        IReadOnlyCollection<ScanFolder> folders,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var appRoot = Path.GetFullPath(AppPaths.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yielded = 0;
        foreach (var folder in folders.Where(x => x.IsEnabled))
        {
            var root = Path.GetFullPath(folder.FullPath);
            var comparableRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Directory.Exists(root) || comparableRoot.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase)) continue;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] files;
                try { files = Directory.GetFiles(current); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ImageFileTypes.IsSupportedExtension(file) || !seen.Add(Path.GetFullPath(file))) continue;
                    FileInfo info;
                    try { info = new FileInfo(file); if (!info.Exists) continue; }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
                    yield return new FileCandidate(folder.Id, info.FullName, info.Length, info.LastWriteTimeUtc);
                    if (++yielded % 128 == 0) await Task.Yield();
                }

                if (!folder.IncludeSubfolders) continue;
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(current))
                    {
                        var comparableDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (comparableDirectory.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase)) continue;
                        var attributes = File.GetAttributes(directory);
                        if ((attributes & System.IO.FileAttributes.ReparsePoint) == 0) pending.Push(directory);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            }
        }
    }
}

public sealed class ImageMetadataService : IImageMetadataService
{
    public Task<(int Width, int Height)> ReadAsync(string imagePath, CancellationToken cancellationToken) =>
        WindowsImageLoader.IdentifyAsync(imagePath, cancellationToken);
}

public sealed class ThumbnailService : IThumbnailService
{
    public Task<string> GetThumbnailAsync(string imagePath, int size, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        AppPaths.EnsureCreated();
        var identity = $"{Path.GetFullPath(imagePath)}|{File.GetLastWriteTimeUtc(imagePath).Ticks}|{size}";
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".jpg";
        var target = Path.Combine(AppPaths.Thumbnails, name);
        if (File.Exists(target)) return target;
        var temporary = target + ".tmp";
        await WindowsImageLoader.SaveThumbnailAsync(imagePath, temporary, size, cancellationToken);
        File.Move(temporary, target, true);
        return target;
    }, cancellationToken);
}
