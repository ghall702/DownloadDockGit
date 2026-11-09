using System.Buffers.Binary;

namespace DownloadDock.Integration;

/// <summary>
/// Wire framing: a 4-byte little-endian unsigned length followed by that many bytes of UTF-8 JSON.
/// Lengths above the limit are rejected before any allocation.
/// </summary>
public static class MessageFraming
{
    /// <summary>Set in <see cref="Exception.Data"/> when an oversized message was skipped and the stream is still usable.</summary>
    public const string StreamIntactKey = "streamIntact";

    public static async Task WriteAsync(Stream stream, IntegrationMessage message, int maxBytes, CancellationToken ct)
    {
        var body = IntegrationJson.Serialize(message);
        if (body.Length > maxBytes)
            throw new IntegrationException(IntegrationErrorCodes.MessageTooLarge, $"Message of {body.Length} bytes exceeds the {maxBytes}-byte limit.");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one message; returns null at a clean end of stream.</summary>
    public static async Task<IntegrationMessage?> ReadAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
            throw new IntegrationException(IntegrationErrorCodes.InvalidMessage, "Empty message.");
        if (length > (uint)maxBytes)
        {
            var ex = new IntegrationException(IntegrationErrorCodes.MessageTooLarge, $"Incoming message of {length} bytes exceeds the {maxBytes}-byte limit.");
            if (length <= IntegrationProtocol.AbsoluteMaxMessageBytes)
            {
                // Skip the body (in small chunks, never buffering it) so the stream stays in sync.
                var scratch = new byte[8192];
                var remaining = (int)length;
                while (remaining > 0)
                {
                    var n = await stream.ReadAsync(scratch.AsMemory(0, Math.Min(scratch.Length, remaining)), ct).ConfigureAwait(false);
                    if (n == 0) throw new IntegrationException(IntegrationErrorCodes.Disconnected, "Connection closed in the middle of a message.");
                    remaining -= n;
                }
                ex.Data[StreamIntactKey] = true;
            }
            throw ex;
        }
        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, body, ct).ConfigureAwait(false))
            throw new IntegrationException(IntegrationErrorCodes.Disconnected, "Connection closed in the middle of a message.");
        return IntegrationJson.Deserialize(body);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new IntegrationException(IntegrationErrorCodes.Disconnected, "Connection closed in the middle of a message.");
            }
            read += n;
        }
        return true;
    }
}
