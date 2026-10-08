using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstanceOfflineReadinessChecksInheritanceNativesAssetsAndCompatibleJava()
    {
        string root = WorkspaceTestDirectory();
        try
        {
            var fixture = await CreateOfflineFixtureAsync(root);
            int javaChecks = 0;
            var service = new InstanceOfflineReadinessService((_, requirement, _, token) =>
            {
                token.ThrowIfCancellationRequested(); javaChecks++; AssertEqual(21, requirement.ManifestJavaMajorVersion);
                return Task.FromResult(new JavaSelectionResult { Success = true, Requirement = JavaRequirementResolution.Valid(JavaVersionRange.ForMajor(21)), SelectedJava = fixture.Java });
            });
            var report = await service.ReadAsync(new(fixture.Instance));
            AssertTrue(report.Ready); AssertTrue(report.Complete); AssertEqual(1, javaChecks);
            foreach (string category in new[] { "manifest", "client", "library", "native", "asset-index", "asset", "java" }) AssertTrue(report.Artifacts.Any(x => x.Category == category && x.State == OfflineArtifactState.Verified));
            string localClient = Path.Combine(fixture.Instance, "1.20.6.jar");
            await File.WriteAllTextAsync(localClient, "broken!"); report = await service.ReadAsync(new(fixture.Instance));
            AssertFalse(report.Ready); AssertTrue(report.Artifacts.Any(x => x.Category == "client" && x.RelativePath == "versions/child/1.20.6.jar" && x.State == OfflineArtifactState.Corrupt));
            File.Delete(localClient); AssertTrue((await service.ReadAsync(new(fixture.Instance))).Ready);
            var changedSelection = new InstanceOfflineReadinessService(async (_, _, _, token) =>
            {
                await File.WriteAllTextAsync(localClient, "changed", token);
                return new JavaSelectionResult { Success = true, Requirement = JavaRequirementResolution.Valid(JavaVersionRange.ForMajor(21)), SelectedJava = fixture.Java };
            });
            report = await changedSelection.ReadAsync(new(fixture.Instance)); AssertFalse(report.Ready); AssertFalse(report.Complete);
            AssertTrue(report.Artifacts.Any(x => x.ReasonCode == "client-selection-changed")); File.Delete(localClient);
            string basePath = Path.Combine(root, "versions", "1.20.6", "1.20.6.json"), baseText = await File.ReadAllTextAsync(basePath);
            var invalidNative = JsonNode.Parse(baseText)!.AsObject();
            foreach (var (_, value) in invalidNative["libraries"]![0]!["downloads"]!["classifiers"]!.AsObject()) value!["path"] = "../../escape.jar";
            await File.WriteAllTextAsync(basePath, invalidNative.ToJsonString()); report = await service.ReadAsync(new(fixture.Instance));
            AssertFalse(report.Ready); AssertFalse(report.Complete);
            AssertTrue(report.Artifacts.Any(x => x.ReasonCode == "omitted-invalid-library"));
            await File.WriteAllTextAsync(basePath, baseText);
            await File.WriteAllTextAsync(fixture.Asset, "broken"); report = await service.ReadAsync(new(fixture.Instance));
            AssertFalse(report.Ready); AssertTrue(report.Artifacts.Any(x => x.Category == "asset" && x.State == OfflineArtifactState.Corrupt));
            File.Delete(fixture.Asset); report = await service.ReadAsync(new(fixture.Instance));
            AssertFalse(report.Ready); AssertTrue(report.Artifacts.Any(x => x.Category == "asset" && x.State == OfflineArtifactState.Missing));
            AssertFalse(File.Exists(fixture.Asset));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask InstanceOfflineReadinessPreservesUnknownIntegrityRejectsCyclesAndCancels()
    {
        string root = WorkspaceTestDirectory();
        try
        {
            var fixture = await CreateOfflineFixtureAsync(root);
            var service = new InstanceOfflineReadinessService((_, _, _, _) => Task.FromResult(new JavaSelectionResult
            { Success = true, Requirement = JavaRequirementResolution.Valid(JavaVersionRange.ForMajor(21)), SelectedJava = fixture.Java }));
            string manifestPath = Path.Combine(fixture.Instance, "child.json");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var artifact = json["libraries"]![0]!["downloads"]!["artifact"]!.AsObject(); artifact.Remove("sha1");
            await File.WriteAllTextAsync(manifestPath, json.ToJsonString()); var report = await service.ReadAsync(new(fixture.Instance));
            AssertFalse(report.Ready); AssertTrue(report.Artifacts.Any(x => x.State == OfflineArtifactState.PresentUnverified));
            json["inheritsFrom"] = "child"; await File.WriteAllTextAsync(manifestPath, json.ToJsonString()); report = await service.ReadAsync(new(fixture.Instance));
            AssertFalse(report.Ready); AssertFalse(report.Complete);
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
            try { await service.ReadAsync(new(fixture.Instance), stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled);
            await WorkspaceFailureAsync(async () => { await service.ReadAsync(new(root)); });
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(string Instance, string Asset, JavaRuntimeCandidate Java)> CreateOfflineFixtureAsync(string root)
    {
        string child = Path.Combine(root, "versions", "child"), vanilla = Path.Combine(root, "versions", "1.20.6"); Directory.CreateDirectory(child); Directory.CreateDirectory(vanilla);
        byte[] payload = "fixture"u8.ToArray(); string sha1 = Convert.ToHexString(SHA1.HashData(payload));
        var downloads = new JsonObject { ["client"] = new JsonObject { ["sha1"] = sha1, ["size"] = payload.Length } };
        await File.WriteAllBytesAsync(Path.Combine(vanilla, "1.20.6.jar"), payload);
        string libraryPath = "fixture/common/1/common-1.jar", nativePath = "fixture/native/1/current.jar", childPath = "fixture/child/1/child-1.jar";
        foreach (string relative in new[] { libraryPath, nativePath, childPath }) { string path = Path.Combine(root, "libraries", relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, payload); }
        JsonObject Artifact(string path) => new() { ["path"] = path, ["sha1"] = sha1, ["size"] = payload.Length };
        var common = new JsonObject
        {
            ["name"] = "fixture:common:1",
            ["natives"] = new JsonObject { ["linux"] = "natives-linux", ["windows"] = "natives-windows", ["osx"] = "natives-osx" },
            ["downloads"] = new JsonObject { ["artifact"] = Artifact(libraryPath), ["classifiers"] = new JsonObject { ["natives-linux"] = Artifact(nativePath), ["natives-windows"] = Artifact(nativePath), ["natives-osx"] = Artifact(nativePath) } }
        };
        string asset = Path.Combine(root, "assets", "objects", sha1[..2], sha1); Directory.CreateDirectory(Path.GetDirectoryName(asset)!); await File.WriteAllBytesAsync(asset, payload);
        var index = new JsonObject { ["objects"] = new JsonObject { ["fixture/sound"] = new JsonObject { ["hash"] = sha1, ["size"] = payload.Length } } };
        byte[] indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString()); string indexDirectory = Path.Combine(root, "assets", "indexes"); Directory.CreateDirectory(indexDirectory); await File.WriteAllBytesAsync(Path.Combine(indexDirectory, "offline.json"), indexBytes);
        var baseJson = new JsonObject
        {
            ["id"] = "1.20.6",
            ["mainClass"] = "net.minecraft.client.Main",
            ["javaVersion"] = new JsonObject { ["majorVersion"] = 21 },
            ["downloads"] = downloads,
            ["libraries"] = new JsonArray(common),
            ["assetIndex"] = new JsonObject { ["id"] = "offline", ["sha1"] = Convert.ToHexString(SHA1.HashData(indexBytes)), ["size"] = indexBytes.Length }
        };
        await File.WriteAllTextAsync(Path.Combine(vanilla, "1.20.6.json"), baseJson.ToJsonString());
        var childJson = new JsonObject { ["id"] = "child", ["inheritsFrom"] = "1.20.6", ["mainClass"] = "fixture.Main", ["libraries"] = new JsonArray(new JsonObject { ["name"] = "fixture:child:1", ["downloads"] = new JsonObject { ["artifact"] = Artifact(childPath) } }) };
        await File.WriteAllTextAsync(Path.Combine(child, "child.json"), childJson.ToJsonString());
        string javaHome = Path.Combine(root, "java"), javaPath = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"); Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!); await File.WriteAllBytesAsync(javaPath, payload);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(javaPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (child, asset, new(new JavaInstallation(javaHome, javaPath, null, new Version(21, 0), JavaBrand.OpenJdk, JavaArchitecture.X64, true, false)));
    }
}
