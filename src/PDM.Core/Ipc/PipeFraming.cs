using System.Buffers.Binary;

namespace PDM.Core.Ipc;

/// <summary>[uint32 little-endian length][UTF-8 JSON] frames, the same layout browsers use for native messaging.</summary>
public static class PipeFraming
{
    public const int MaxFrame = 8 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrame) throw new InvalidDataException("Message too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns null on a clean end of stream.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct, int maxLength = MaxFrame)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > (uint)maxLength) throw new InvalidDataException("Message too large.");
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false)) throw new EndOfStreamException();
        return body;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
    }
}
