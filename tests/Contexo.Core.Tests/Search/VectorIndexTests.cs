using System.Diagnostics;
using Contexo.Core.Search;
using Xunit.Abstractions;

namespace Contexo.Core.Tests.Search;

public sealed class VectorIndexTests(ITestOutputHelper output)
{
    private static (float[] Data, long[] Ids) RandomVectors(int count, int dimensions, int seed)
    {
        var random = new Random(seed);
        var data = new float[count * dimensions];
        for (var row = 0; row < count; row++)
        {
            var span = data.AsSpan(row * dimensions, dimensions);
            double norm = 0;
            for (var d = 0; d < dimensions; d++)
            {
                span[d] = (float)(random.NextDouble() * 2 - 1);
                norm += span[d] * span[d];
            }

            var scale = (float)(1 / Math.Sqrt(norm));
            for (var d = 0; d < dimensions; d++)
            {
                span[d] *= scale;
            }
        }

        return (data, Enumerable.Range(1, count).Select(i => (long)i).ToArray());
    }

    [Fact]
    public void Dot_MatchesScalarResultForAnyLength()
    {
        var random = new Random(1);
        foreach (var length in new[] { 1, 3, 7, 8, 9, 31, 512, 513 })
        {
            var a = Enumerable.Range(0, length).Select(_ => (float)random.NextDouble()).ToArray();
            var b = Enumerable.Range(0, length).Select(_ => (float)random.NextDouble()).ToArray();
            var expected = 0.0;
            for (var i = 0; i < length; i++)
            {
                expected += a[i] * b[i];
            }

            Assert.Equal(expected, VectorSnapshot.Dot(a, b), 3);
        }
    }

    [Fact]
    public void Search_ReturnsTopKInDescendingOrderAndMatchesBruteForce()
    {
        const int dims = 16;
        var (data, ids) = RandomVectors(500, dims, 7);
        var snapshot = new VectorSnapshot("m", 1, dims, data, ids);
        var query = data.AsSpan(42 * dims, dims).ToArray();

        var result = snapshot.Search(query, 10);

        var expected = Enumerable.Range(0, 500)
            .OrderByDescending(row => VectorSnapshot.Dot(query, data.AsSpan(row * dims, dims)))
            .Take(10)
            .Select(row => ids[row])
            .ToArray();
        Assert.Equal(expected, result);
        Assert.Equal(43, result[0]);
    }

    [Fact]
    public void Search_HandlesFewerVectorsThanKAndEmptyIndex()
    {
        var (data, ids) = RandomVectors(3, 8, 3);
        var snapshot = new VectorSnapshot("m", 1, 8, data, ids);
        Assert.Equal(3, snapshot.Search(data.AsSpan(0, 8), 30).Count);
        Assert.Empty(new VectorSnapshot("m", 1, 8, [], []).Search(data.AsSpan(0, 8), 5));
        Assert.Throws<ArgumentException>(() => snapshot.Search(new float[4], 5));
    }

    [Fact]
    public void Search_50kVectorsOf512Dimensions_FinishesUnder100Ms()
    {
        const int dims = 512;
        var (data, ids) = RandomVectors(50_000, dims, 11);
        var snapshot = new VectorSnapshot("m", 1, dims, data, ids);
        var query = data.AsSpan(1234 * dims, dims).ToArray();

        _ = snapshot.Search(query, 30); // warm up the JIT

        var timings = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = snapshot.Search(query, 30);
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Equal(1235, result[0]);
        }

        timings.Sort();
        output.WriteLine($"50k x 512 search: min {timings[0]:F1} ms, median {timings[3]:F1} ms, max {timings[^1]:F1} ms");
        Assert.True(timings[3] < 100, $"median search took {timings[3]:F1} ms");
    }
}
