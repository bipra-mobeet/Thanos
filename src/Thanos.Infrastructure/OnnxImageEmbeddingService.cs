using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using Thanos.Core;

namespace Thanos.Infrastructure;

public sealed class OnnxImageEmbeddingService : IImageEmbeddingService
{
    private readonly string _modelPath;
    private readonly SemaphoreSlim _fallbackGate = new(1, 1);
    private InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _width;
    private readonly int _height;

    public int Dimension { get; private set; }
    public string ModelVersion { get; }
    public string Provider { get; private set; }

    public OnnxImageEmbeddingService(string modelPath, bool preferGpu)
    {
        _modelPath = Path.GetFullPath(modelPath);
        if (!File.Exists(_modelPath)) throw new FileNotFoundException("The local ONNX image model is missing.", _modelPath);
        using var modelStream = File.OpenRead(_modelPath);
        ModelVersion = "dinov2-small:wic-v1:" + Convert.ToHexString(SHA256.HashData(modelStream)).ToLowerInvariant()[..16];
        (_session, Provider) = CreateSession(preferGpu);
        _inputName = _session.InputMetadata.Keys.Single();
        _outputName = _session.OutputMetadata.Keys.First();
        var dimensions = _session.InputMetadata[_inputName].Dimensions;
        if (dimensions.Length != 4) throw new InvalidDataException("The model must expose a four-dimensional NCHW image input.");
        _height = dimensions[2] > 0 ? dimensions[2] : 224;
        _width = dimensions[3] > 0 ? dimensions[3] : 224;
        Dimension = InferOutputDimension(_session.OutputMetadata[_outputName].Dimensions);
        Log.Information("Model loaded. Version={ModelVersion} Provider={Provider}", ModelVersion, Provider);
    }

    public async Task<float[]> EmbedAsync(string imagePath, CancellationToken cancellationToken)
    {
        var tensor = await PreprocessAsync(imagePath, cancellationToken);
        try { return Run(tensor); }
        catch (OnnxRuntimeException) when (Provider == "DirectML")
        {
            await _fallbackGate.WaitAsync(cancellationToken);
            try
            {
                if (Provider == "DirectML")
                {
                    _session.Dispose();
                    (_session, Provider) = CreateSession(false);
                    Log.Warning("DirectML inference failed. Falling back to CPU");
                }
            }
            finally { _fallbackGate.Release(); }
            return Run(tensor);
        }
    }

    private float[] Run(DenseTensor<float> input)
    {
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var results = _session.Run(inputs);
        var tensor = results.First(x => x.Name == _outputName).AsTensor<float>();
        float[] embedding;
        if (tensor.Dimensions.Length == 3)
        {
            var hidden = tensor.Dimensions[2];
            embedding = new float[hidden];
            for (var i = 0; i < hidden; i++) embedding[i] = tensor[0, 0, i];
        }
        else
        {
            embedding = tensor.ToArray();
            if (tensor.Dimensions.Length > 1 && tensor.Dimensions[0] == 1)
                Array.Resize(ref embedding, embedding.Length / tensor.Dimensions[0]);
        }
        Dimension = embedding.Length;
        return SimilarityMath.Normalize(embedding);
    }

    private async Task<DenseTensor<float>> PreprocessAsync(string imagePath, CancellationToken cancellationToken)
    {
        var image = await WindowsImageLoader.LoadAsync(imagePath, cancellationToken);
        var shortSide = (int)Math.Round(Math.Max(_width, _height) * (256d / 224d));
        var scale = shortSide / (double)Math.Min(image.Width, image.Height);
        var resizedWidth = Math.Max(_width, (int)Math.Round(image.Width * scale));
        var resizedHeight = Math.Max(_height, (int)Math.Round(image.Height * scale));
        var cropX = (resizedWidth - _width) / 2d;
        var cropY = (resizedHeight - _height) / 2d;
        var tensor = new DenseTensor<float>([1, 3, _height, _width]);
        var means = new[] { .485f, .456f, .406f };
        var standardDeviations = new[] { .229f, .224f, .225f };
        for (var y = 0; y < _height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = (cropY + y + .5d) / scale - .5d;
            for (var x = 0; x < _width; x++)
            {
                var sourceX = (cropX + x + .5d) / scale - .5d;
                var (red, green, blue) = SampleBicubic(image, sourceX, sourceY);
                tensor[0, 0, y, x] = (red / 255f - means[0]) / standardDeviations[0];
                tensor[0, 1, y, x] = (green / 255f - means[1]) / standardDeviations[1];
                tensor[0, 2, y, x] = (blue / 255f - means[2]) / standardDeviations[2];
            }
        }
        return tensor;
    }

    private static (float Red, float Green, float Blue) SampleBicubic(DecodedImage image, double x, double y)
    {
        var baseX = (int)Math.Floor(x);
        var baseY = (int)Math.Floor(y);
        double red = 0, green = 0, blue = 0, weightTotal = 0;
        for (var offsetY = -1; offsetY <= 2; offsetY++)
        {
            var sourceY = Math.Clamp(baseY + offsetY, 0, image.Height - 1);
            var weightY = CubicWeight(y - (baseY + offsetY));
            for (var offsetX = -1; offsetX <= 2; offsetX++)
            {
                var sourceX = Math.Clamp(baseX + offsetX, 0, image.Width - 1);
                var weight = weightY * CubicWeight(x - (baseX + offsetX));
                var index = (sourceY * image.Width + sourceX) * 4;
                red += image.RgbaPixels[index] * weight;
                green += image.RgbaPixels[index + 1] * weight;
                blue += image.RgbaPixels[index + 2] * weight;
                weightTotal += weight;
            }
        }

        return (
            (float)Math.Clamp(red / weightTotal, 0, 255),
            (float)Math.Clamp(green / weightTotal, 0, 255),
            (float)Math.Clamp(blue / weightTotal, 0, 255));
    }

    private static double CubicWeight(double value)
    {
        var distance = Math.Abs(value);
        if (distance <= 1) return 1.5 * distance * distance * distance - 2.5 * distance * distance + 1;
        if (distance < 2) return -.5 * distance * distance * distance + 2.5 * distance * distance - 4 * distance + 2;
        return 0;
    }

    private (InferenceSession Session, string Provider) CreateSession(bool preferGpu)
    {
        if (preferGpu)
        {
            try
            {
                var gpuOptions = NewOptions();
                gpuOptions.AppendExecutionProvider_DML(0);
                return (new InferenceSession(_modelPath, gpuOptions), "DirectML");
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "DirectML unavailable. Falling back to CPU");
            }
        }
        return (new InferenceSession(_modelPath, NewOptions()), "CPU");
    }

    private static SessionOptions NewOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        EnableMemoryPattern = false
    };

    private static int InferOutputDimension(IReadOnlyList<int> dimensions) => dimensions.Count switch
    {
        >= 3 when dimensions[^1] > 0 => dimensions[^1],
        >= 2 when dimensions[1] > 0 => dimensions[1],
        _ => 0
    };

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        _fallbackGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
