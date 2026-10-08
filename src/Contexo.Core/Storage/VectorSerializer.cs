using System.Runtime.InteropServices;

namespace Contexo.Core.Storage;

/// <summary>float32 little-endian BLOB format used by the embeddings table.</summary>
internal static class VectorSerializer
{
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        EnsureLittleEndian();
        return MemoryMarshal.AsBytes(vector).ToArray();
    }

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureLittleEndian();
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException($"Vector blob length {bytes.Length} is not a multiple of 4.");
        }

        var result = new float[bytes.Length / sizeof(float)];
        // Copy instead of Cast so the source does not need float alignment.
        bytes.CopyTo(MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    private static void EnsureLittleEndian()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Vectors are stored as little-endian float32; big-endian platforms are not supported.");
        }
    }
}
