using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nexa.Services.Updates;

internal static class UpdateHighWaterJournal
{
    internal const int MaximumBytes = 1024 * 1024;
    private const int HeaderBytes = 8, DigestBytes = 32, SuffixBytes = 4;
    internal readonly record struct State(string? Version, long CommittedLength);

    internal static State Read(Stream stream)
    {
        long length = stream.Length;
        if (length is < 0 or > MaximumBytes) throw new InvalidDataException("防回退日志超过容量限制。");
        stream.Position = 0;
        var state = new State(null, 0);
        byte[] header = new byte[HeaderBytes];
        Span<byte> expected = stackalloc byte[DigestBytes];
        while (stream.Position < length)
        {
            int availableHeader = (int)Math.Min(HeaderBytes, length - stream.Position);
            stream.ReadExactly(header.AsSpan(0, availableHeader));
            if (!header.AsSpan(0, Math.Min(4, availableHeader)).SequenceEqual("NXH1"u8[..Math.Min(4, availableHeader)]))
                throw new InvalidDataException("防回退日志帧标记无效。");
            if (availableHeader < HeaderBytes)
            {
                if (availableHeader > 4 && header[4] is 0 or > 128
                    || (availableHeader > 5 && header.AsSpan(5, availableHeader - 5).ContainsAnyExcept((byte)0)))
                    throw new InvalidDataException("防回退日志帧长度无效。");
                return state;
            }
            uint versionLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (versionLength is 0 or > 128) throw new InvalidDataException("防回退日志版本长度无效。");
            int recordLength = HeaderBytes + (int)versionLength + DigestBytes + SuffixBytes;
            byte[] frame = new byte[recordLength];
            header.CopyTo(frame, 0);
            int remainder = (int)Math.Min(recordLength - HeaderBytes, length - stream.Position);
            stream.ReadExactly(frame.AsSpan(HeaderBytes, remainder));
            if (remainder < versionLength) return state;
            string version = Encoding.ASCII.GetString(frame, HeaderBytes, (int)versionLength);
            UpdateVersion candidate = ParseVersion(version);
            if (state.Version is not null && candidate <= ParseVersion(state.Version))
                throw new InvalidDataException("防回退日志包含未递增版本。");
            int digestOffset = HeaderBytes + (int)versionLength;
            if (remainder >= versionLength + DigestBytes)
            {
                SHA256.HashData(frame.AsSpan(0, digestOffset), expected);
                if (!CryptographicOperations.FixedTimeEquals(expected, frame.AsSpan(digestOffset, DigestBytes)))
                    throw new InvalidDataException("防回退日志校验失败。");
                int suffixLength = remainder - (int)versionLength - DigestBytes;
                if (!frame.AsSpan(digestOffset + DigestBytes, suffixLength).SequenceEqual("DONE"u8[..suffixLength]))
                    throw new InvalidDataException("防回退日志提交标记无效。");
            }
            if (remainder < recordLength - HeaderBytes) return state;
            state = new(version, stream.Position);
        }
        return state;
    }

    internal static void Append(Stream stream, string version)
    {
        UpdateVersion candidate = ParseVersion(version);
        State state = Read(stream);
        if (state.Version is not null && candidate <= ParseVersion(state.Version))
            throw new InvalidOperationException("防回退版本只能递增。");
        byte[] frame = Encode(version);
        if (state.CommittedLength + frame.Length > MaximumBytes)
            throw new InvalidDataException("防回退日志已满，需要受保护的维护操作。");
        stream.SetLength(state.CommittedLength);
        stream.Position = state.CommittedLength;
        stream.Write(frame);
    }

    internal static byte[] Encode(string version)
    {
        ParseVersion(version);
        byte[] payload = Encoding.ASCII.GetBytes(version);
        byte[] frame = new byte[HeaderBytes + payload.Length + DigestBytes + SuffixBytes];
        "NXH1"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, HeaderBytes);
        SHA256.HashData(frame.AsSpan(0, HeaderBytes + payload.Length), frame.AsSpan(HeaderBytes + payload.Length, DigestBytes));
        "DONE"u8.CopyTo(frame.AsSpan(frame.Length - SuffixBytes));
        return frame;
    }

    internal static UpdateVersion ParseVersion(string value)
    {
        if (value is not { Length: > 0 and <= 128 } || !UpdateVersion.TryParse(value, out UpdateVersion parsed)
            || parsed.ToString() != value || parsed.Stage == UpdateVersionStage.Ci
            || ((parsed.Stage is UpdateVersionStage.Alpha or UpdateVersionStage.Beta) && parsed.Sequence <= 0))
            throw new InvalidDataException("防回退版本不是规范公开版本。");
        return parsed;
    }
}
