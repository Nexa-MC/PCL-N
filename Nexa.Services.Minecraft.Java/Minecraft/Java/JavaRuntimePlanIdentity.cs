using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexa.Services.Minecraft.Java;

public static class JavaRuntimePlanIdentity
{
    public static string Fingerprint(JavaRuntimeDownloadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(plan.ComponentName); Add(plan.VersionName); Add(plan.ManifestUrl); Add(plan.TargetDirectory);
        Add(plan.Files.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var file in plan.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            Add(file.RelativePath); Add(file.TargetPath); Add(file.Url); Add(file.Sha1);
            Add(file.Size.ToString(CultureInfo.InvariantCulture)); Add(file.Executable ? "true" : "false");
        }
        return Convert.ToHexString(hash.GetHashAndReset());
        void Add(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
        }
    }
}
