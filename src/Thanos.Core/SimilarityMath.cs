namespace Thanos.Core;

public static class SimilarityMath
{
    public static float Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length == 0 || left.Length != right.Length)
            throw new ArgumentException("Vectors must be non-empty and have equal dimensions.");

        double dot = 0, leftLength = 0, rightLength = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftLength += left[i] * left[i];
            rightLength += right[i] * right[i];
        }

        if (leftLength == 0 || rightLength == 0) return 0;
        return (float)(dot / Math.Sqrt(leftLength * rightLength));
    }

    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += value * value;
        if (sum == 0) throw new ArgumentException("A zero vector cannot be normalized.", nameof(vector));

        var result = vector.ToArray();
        var length = Math.Sqrt(sum);
        for (var i = 0; i < result.Length; i++) result[i] = (float)(result[i] / length);
        return result;
    }
}
