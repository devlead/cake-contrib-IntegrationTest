public sealed record SnapshotRow(string Id, string Check, string? Framework, string Status, string? Reason);

public static partial class Program
{
    static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NewLine = "\n"
    };

    public static string SnapshotName(BuildData data, string framework)
        => $"{data.Os}-{data.Architecture}-{framework}";

    public static FilePath SnapshotPath(BuildData data, string framework)
        => data.SnapshotsDir.CombineWithFilePath($"{SnapshotName(data, framework)}.verified.json");

    public static FilePath ReceivedPath(BuildData data, string framework)
        => data.ReceivedSnapshotsDir.CombineWithFilePath($"{SnapshotName(data, framework)}.received.json");

    public static int VerifySnapshots(BuildData data)
    {
        var selected = data.Packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var markdown = new List<string>();
        var total = 0;
        foreach (var framework in data.TargetFrameworks)
        {
            var verifiedPath = SnapshotPath(data, framework);
            var receivedPath = ReceivedPath(data, framework);
            var verified = ReadSnapshot(verifiedPath);
            var current = data.Results
                .Where(r => r.Framework is null || r.Framework == framework)
                .Select(r => new SnapshotRow(r.Id, r.Check, r.Framework, r.Status, r.Reason))
                .ToList();
            var received = SortSnapshot(verified.Where(r => !selected.Contains(r.Id)).Concat(current));

            var verifiedByKey = verified.Where(r => selected.Contains(r.Id)).ToDictionary(SnapshotKey);
            var currentByKey = current.ToDictionary(SnapshotKey);
            var diffs = verifiedByKey.Keys
                .Union(currentByKey.Keys)
                .Select(key => (
                    Verified: verifiedByKey.GetValueOrDefault(key),
                    Received: currentByKey.GetValueOrDefault(key)))
                .Where(pair => pair.Verified != pair.Received)
                .OrderBy(pair => SnapshotKey(pair.Received ?? pair.Verified!), StringComparer.Ordinal)
                .ToList();

            if (diffs.Count == 0)
            {
                if (Context.FileSystem.Exist(receivedPath))
                {
                    DeleteFile(receivedPath);
                }

                Information("Snapshot {0} matches", verifiedPath);
                continue;
            }

            total += diffs.Count;
            EnsureDirectoryExists(receivedPath.GetDirectory());
            Context.FileSystem.GetFile(receivedPath).WriteAllText(JsonSerializer.Serialize(received, SnapshotJsonOptions) + "\n");

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold]Snapshot diff {Markup.Escape(SnapshotName(data, framework))}[/]")
                .AddColumn("Package")
                .AddColumn("Check")
                .AddColumn("Framework")
                .AddColumn("Verified")
                .AddColumn("Received");
            markdown.Add("");
            markdown.Add($"## Snapshot diff `{SnapshotName(data, framework)}`");
            markdown.Add("");
            markdown.Add("| Package | Check | Framework | Verified | Received |");
            markdown.Add("| --- | --- | --- | --- | --- |");
            foreach (var (verifiedRow, receivedRow) in diffs)
            {
                var row = receivedRow ?? verifiedRow!;
                table.AddRow(
                    Markup.Escape(row.Id),
                    Markup.Escape(row.Check),
                    Markup.Escape(row.Framework ?? ""),
                    Markup.Escape(SnapshotState(verifiedRow)),
                    Markup.Escape(SnapshotState(receivedRow)));
                markdown.Add($"| {row.Id} | {row.Check} | {row.Framework} | {SnapshotState(verifiedRow)} | {SnapshotState(receivedRow)} |");
            }

            AnsiConsole.Write(table);
            Warning("Snapshot {0} differs ({1} rows), wrote {2}", verifiedPath, diffs.Count, receivedPath);
        }

        var githubSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (markdown.Count > 0 && !string.IsNullOrWhiteSpace(githubSummary))
        {
            Context.FileSystem.GetFile(githubSummary).AppendAllText(string.Join(Environment.NewLine, markdown) + Environment.NewLine);
        }

        return total;
    }

    public static void AcceptSnapshots(BuildData data, string run)
    {
        var source = data.ReceivedSnapshotsDir;
        if (!string.IsNullOrWhiteSpace(run))
        {
            source = data.Artifacts.Combine("ci-snapshots").Combine(run);
            if (Context.FileSystem.Exist(source))
            {
                DeleteDirectory(source, new DeleteDirectorySettings { Recursive = true, Force = true });
            }

            EnsureDirectoryExists(source);
            var exitCode = StartProcess(
                "gh",
                new ProcessSettings
                {
                    Arguments = new ProcessArgumentBuilder()
                        .Append("run")
                        .Append("download")
                        .Append(run)
                        .Append("--pattern")
                        .AppendQuoted("snapshots-*")
                        .Append("--dir")
                        .AppendQuoted(source.FullPath)
                });
            if (exitCode != 0)
            {
                throw new CakeException($"gh run download {run} failed with exit code {exitCode}.");
            }
        }

        var files = GetFiles(source.FullPath + "/**/*.received.json");
        if (files.Count == 0)
        {
            Warning("No received snapshots found in {0}", source);
            return;
        }

        EnsureDirectoryExists(data.SnapshotsDir);
        foreach (var file in files)
        {
            var name = file.GetFilename().FullPath.Replace(".received.json", ".verified.json");
            var target = data.SnapshotsDir.CombineWithFilePath(name);
            CopyFile(file, target);
            DeleteFile(file);
            Information("Accepted {0}", target);
        }
    }

    public static async Task UploadArtifactsAsync(BuildData? data)
    {
        if (data is null || !GitHubActions.IsRunningOnGitHubActions)
        {
            return;
        }

        if (!GitHubActions.Environment.Runtime.IsRuntimeAvailable)
        {
            Warning("GitHub Actions runtime not available, skipping artifact upload");
            return;
        }

        var suffix = string.Join("-", new[] { data.Os, data.Architecture }
            .Concat(data.TargetFrameworks)
            .Append(data.Shard.Replace('/', '-'))
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (Context.FileSystem.Exist(data.ReceivedSnapshotsDir) && GetFiles(data.ReceivedSnapshotsDir.FullPath + "/*.received.json").Count > 0)
        {
            await GitHubActions.Commands.UploadArtifact(data.ReceivedSnapshotsDir, $"snapshots-{suffix}");
        }

        var results = data.Artifacts.CombineWithFilePath("results.json");
        if (Context.FileSystem.Exist(results))
        {
            await GitHubActions.Commands.UploadArtifact(results, $"results-{suffix}");
        }
    }

    static List<SnapshotRow> ReadSnapshot(FilePath path)
        => Context.FileSystem.Exist(path)
            ? JsonSerializer.Deserialize<List<SnapshotRow>>(Context.FileSystem.GetFile(path).ReadAllText(), SnapshotJsonOptions) ?? []
            : [];

    static List<SnapshotRow> SortSnapshot(IEnumerable<SnapshotRow> rows)
        => rows.OrderBy(SnapshotKey, StringComparer.Ordinal).ToList();

    static string SnapshotKey(SnapshotRow row)
        => $"{row.Id}|{row.Check}|{row.Framework}";

    static string SnapshotState(SnapshotRow? row)
        => row is null ? "(missing)" : string.IsNullOrWhiteSpace(row.Reason) ? row.Status : $"{row.Status} ({row.Reason})";
}
