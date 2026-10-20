public static partial class Program
{
    public static string ResolveSdkVersion(string targetFramework)
    {
        var major = targetFramework.StartsWith("net11", StringComparison.OrdinalIgnoreCase) ? "11." : "10.";
        Command(
            new CommandSettings
            {
                ToolName = "dotnet",
                ToolExecutableNames = ["dotnet", "dotnet.exe"]
            },
            out var output,
            "--list-sdks");
        var match = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ')[0])
            .Where(version => version.StartsWith(major, StringComparison.Ordinal))
            .Order(Versions.Comparer)
            .LastOrDefault();
        return match ?? major + "0.100";
    }

    public static void WriteSdkGlobalJson(DirectoryPath destination, string targetFramework)
    {
        var sdkVersion = ResolveSdkVersion(targetFramework);
        var json =
            $$"""
            {
              "sdk": {
                "version": "{{sdkVersion}}",
                "rollForward": "latestFeature",
                "allowPrerelease": true
              }
            }
            """;
        foreach (var existing in GetFiles(destination.FullPath + "/**/global.json"))
        {
            Context.FileSystem.GetFile(existing).WriteAllText(json);
        }

        Context.FileSystem.GetFile(destination.CombineWithFilePath("global.json")).WriteAllText(json);
    }

    public static void CopySourceTree(DirectoryPath source, DirectoryPath destination)
    {
        EnsureDirectoryExists(destination);
        foreach (var directory in GetDirectories(source.FullPath + "/**/*"))
        {
            var relative = source.GetRelativePath(directory).FullPath.Replace('\\', '/');
            if (relative.Split('/').Any(part => part == ".git"))
            {
                continue;
            }

            EnsureDirectoryExists(destination.Combine(relative));
        }

        foreach (var file in GetFiles(source.FullPath + "/**/*"))
        {
            var relative = source.GetRelativePath(file).FullPath.Replace('\\', '/');
            if (relative.Split('/').Any(part => part == ".git"))
            {
                continue;
            }

            var target = destination.CombineWithFilePath(relative);
            EnsureDirectoryExists(target.GetDirectory());
            CopyFile(file, target);
        }
    }

    public static DirectoryPath WorkDir(BuildData data, CatalogPackage package, string leaf)
        => data.WorkRoot.Combine(package.Id).Combine(leaf);

    public static void Record(
        BuildData data,
        string id,
        string check,
        string status,
        string? reason = null,
        string? detail = null,
        PackageInventory? inventory = null)
        => data.Results.Add(new CheckResult
        {
            Id = id,
            Check = check,
            Status = status,
            Reason = reason,
            Detail = detail,
            PackageVersion = inventory?.PackageVersion,
            CakeCore = inventory?.CakeCore,
            Tfms = inventory?.Tfms,
            Framework = check is "resolve" or "inventory" ? null : data.Framework
        });

    public static void WriteSummary(BuildData data)
    {
        var payload = new
        {
            os = data.Os,
            architecture = data.Architecture,
            targetFrameworks = data.TargetFrameworks,
            cake = data.Versions,
            results = data.Results
        };
        var resultsPath = data.Artifacts.CombineWithFilePath("results.json");
        Harness.WriteResults(Context, resultsPath, payload);
        var markdown = Harness.MarkdownSummary(data.Results, data.Versions, data.Os, data.Architecture, data.TargetFrameworks);
        var summaryPath = data.Artifacts.CombineWithFilePath("summary.md");
        Context.FileSystem.GetFile(summaryPath).WriteAllText(markdown);
        var githubSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrWhiteSpace(githubSummary))
        {
            Context.FileSystem.GetFile(githubSummary).AppendAllText(markdown);
        }

        Harness.WriteSpectreSummary(data.Results, data.Versions, data.Os, data.Architecture, data.TargetFrameworks);
        Information("Wrote {0}", resultsPath);
    }
}
