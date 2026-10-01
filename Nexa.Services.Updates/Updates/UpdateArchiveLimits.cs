using System.Security.Cryptography;
using Nexa.Services.Files;

namespace Nexa.Services.Updates;

internal static class UpdateArchiveEntry
{
    internal static async Task<string> CopyAndHashAsync(Stream content, string destination, long declaredLength,
        long maximumFileBytes, ArchiveReadBudget budget, CancellationToken token)
    {
        bool created = false;
        using (content)
            try
            {
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                created = true;
                await ArchiveReadBudget.CopyAsync(content, output, declaredLength, maximumFileBytes, budget, token).ConfigureAwait(false);
                output.Position = 0;
                return Convert.ToHexStringLower(await SHA256.HashDataAsync(output, token).ConfigureAwait(false));
            }
            catch
            {
                if (created)
                    try { File.Delete(destination); } catch (IOException) { }
                throw;
            }
    }
}
