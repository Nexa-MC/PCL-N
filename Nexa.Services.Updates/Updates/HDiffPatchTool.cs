

namespace Nexa.Services.Updates;

/// <summary>
/// HDiffPatch command-line integration: applies one patch payload over a source file to
/// produce the target file (`hpatchz source patch output`). Any nonzero exit is a hard
/// failure — the updater falls back to the full package rather than keeping a dubious file.
/// </summary>
public sealed class HDiffPatchTool
{
    public const string ToolName = "hpatchz";

    private readonly IProcessRunner _runner;
    private readonly string _executablePath;

    public HDiffPatchTool(IProcessRunner runner, string executablePath = ToolName)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    public async Task ApplyAsync(
        string sourceFile,
        string patchFile,
        string outputFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        int exit = await _runner.RunAsync(
            _executablePath,
            [sourceFile, patchFile, outputFile],
            cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidDataException($"HDiffPatch 应用补丁失败（exit {exit}）。");
        }
    }
}
