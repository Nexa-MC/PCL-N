using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nexa.Sidecar.Transport;

/// <summary>Checks the native image format and architecture, regardless of file extension.</summary>
public static class SidecarExecutable
{
    public const long MaximumImageBytes = 512L * 1024 * 1024;

    public static void Validate(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanSeek || image.Length is < 64 or > MaximumImageBytes)
            throw new InvalidDataException("Invalid Sidecar executable size.");
        image.Position = 0;
        Span<byte> header = stackalloc byte[64];
        image.ReadExactly(header);
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsWindows())
        {
            if (!header[..2].SequenceEqual("MZ"u8)) throw new InvalidDataException("Sidecar is not a PE executable.");
            int offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
            if (offset < 64 || offset > image.Length - 26) throw new InvalidDataException("Invalid PE header offset.");
            image.Position = offset;
            Span<byte> pe = stackalloc byte[26];
            image.ReadExactly(pe);
            ushort expected = architecture switch { Architecture.X64 => 0x8664, Architecture.Arm64 => 0xaa64, Architecture.X86 => 0x14c, _ => 0 };
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(pe[22..]);
            if (!pe[..4].SequenceEqual("PE\0\0"u8) || expected == 0 || BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != expected
                || (flags & 0x2000) != 0 || (flags & 2) == 0)
                throw new InvalidDataException("Sidecar PE image is not an executable for this architecture.");
        }
        else if (OperatingSystem.IsLinux())
        {
            ushort expected = architecture switch { Architecture.X64 => 62, Architecture.Arm64 => 183, Architecture.X86 => 3, Architecture.Arm => 40, _ => 0 };
            byte elfClass = architecture is Architecture.X64 or Architecture.Arm64 ? (byte)2 : (byte)1;
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
            if (header[0] != 0x7f) throw new InvalidDataException("Sidecar is not an ELF executable.");
            if (!header[1..4].SequenceEqual("ELF"u8) || header[4] != elfClass || header[5] != 1 || expected == 0
                || BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) != expected || type is not (2 or 3))
                throw new InvalidDataException("Sidecar ELF image is not an executable for this architecture.");
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Thin Mach-O; ship an architecture-specific Sidecar rather than an unvalidated fat image.
            uint cpu = architecture switch { Architecture.X64 => 0x01000007, Architecture.Arm64 => 0x0100000c, _ => 0 };
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xfeedfacf || cpu == 0
                || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != cpu
                || BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != 2)
                throw new InvalidDataException("Sidecar is not a native Mach-O executable for this architecture.");
        }
        else throw new PlatformNotSupportedException("Sidecar processes are unavailable on this platform.");
        image.Position = 0;
    }
}
