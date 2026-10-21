public static partial class Program
{
    public static void RunRebuild(BuildData data, CatalogPackage package)
    {
        data.Inventories.TryGetValue(package.Id, out var inventory);
        var exclusion = Harness.MatchExclusion(package, runner: null, data.Os, data.Framework, data.Architecture);
        if (exclusion is not null)
        {
            Record(data, package.Id, "rebuild", "skipped", reason: exclusion.Reason, inventory: inventory);
            Record(data, package.Id, "rebuild-warning", "skipped", reason: exclusion.Reason, inventory: inventory);
            return;
        }

        if (string.IsNullOrWhiteSpace(package.Repository))
        {
            Record(data, package.Id, "rebuild", "skipped", reason: "no repository", inventory: inventory);
            Record(data, package.Id, "rebuild-warning", "skipped", reason: "no repository", inventory: inventory);
            return;
        }

        var work = WorkDir(data, package, "src");
        var parent = data.WorkRoot.Combine(package.Id);
        try
        {
            Information("{0} rebuild {1}", package.Id, package.Repository);
            if (!DirectoryExists(work))
            {
                EnsureDirectoryExists(parent);
                // Reuse a checkout of the same repository from any framework or sibling package (e.g. the Cake.Issues monorepo).
                var existing = data.Packages
                    .Where(p => string.Equals(p.Repository, package.Repository, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => GetDirectories(data.Artifacts.FullPath + "/*/" + p.Id + "/src"))
                    .FirstOrDefault(d => DirectoryExists(d) && GetFiles(d.FullPath + "/**/*.csproj").Any());
                if (existing is not null)
                {
                    Information("{0} reusing {1}", package.Id, existing);
                    CopySourceTree(existing, work);
                }
                else
                {
                    CloneRepository(package.Repository, parent, work);
                }
            }

            Harness.CopyNuGetConfig(Context, data.Root, work);
            WriteSdkGlobalJson(work, data.Framework);

            var projects = GetFiles(work.FullPath + "/**/*.csproj")
                .Where(p => !Harness.IsTestProject(p) && Harness.ReferencesCake(Context, p))
                .ToList();
            if (projects.Count == 0)
            {
                Record(data, package.Id, "rebuild", "skipped", reason: "no Cake.Core/Cake.Common project", inventory: inventory);
                Record(data, package.Id, "rebuild-warning", "skipped", reason: "no Cake.Core/Cake.Common project", inventory: inventory);
                return;
            }

            var names = string.Join(", ", projects.Select(p => p.GetFilename()));
            // Only the restore is retried: a second build in the same tree can pass on outputs of the first (e.g. Cake.Warp's Fody resources).
            RestoreProjects(data, package, work, projects);
            BuildProjects(data, package, work, projects, check: "rebuild", warningsAsErrors: false, noRestore: true);
            Record(data, package.Id, "rebuild", "passed", detail: names, inventory: inventory);

            try
            {
                Information("{0} rebuild-warning {1}", package.Id, package.Repository);
                BuildProjects(data, package, work, projects, check: "rebuild-warning", warningsAsErrors: true);
                Record(data, package.Id, "rebuild-warning", "passed", detail: names, inventory: inventory);
            }
            catch (Exception ex)
            {
                Error("{0} rebuild-warning: {1}", package.Id, ex.Message);
                Record(data, package.Id, "rebuild-warning", "failed", detail: ex.Message, inventory: inventory);
            }
        }
        catch (Exception ex)
        {
            Error("{0} rebuild: {1}", package.Id, ex.Message);
            Record(data, package.Id, "rebuild", "failed", detail: ex.Message, inventory: inventory);
            Record(data, package.Id, "rebuild-warning", "skipped", reason: "rebuild failed", inventory: inventory);
        }
    }

    static void CloneRepository(string repository, DirectoryPath parent, DirectoryPath work)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var exit = StartProcess("git", new ProcessSettings
            {
                WorkingDirectory = parent,
                // core.longpaths: deep test snapshot paths (e.g. Cake.Issues) exceed MAX_PATH on Windows.
                Arguments = $"-c core.longpaths=true clone --depth 1 https://github.com/{repository}.git \"{work.GetDirectoryName()}\"",
                // LFS content (docs images etc.) is not needed to build and is one more network round trip;
                // never prompt for credentials when a repository is missing or private.
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["GIT_LFS_SKIP_SMUDGE"] = "1",
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GCM_INTERACTIVE"] = "never"
                }
            });
            if (exit == 0)
            {
                return;
            }

            if (DirectoryExists(work))
            {
                DeleteDirectory(work, new DeleteDirectorySettings { Recursive = true, Force = true });
            }

            if (attempt == attempts)
            {
                throw new CakeException($"git clone failed with exit code {exit}");
            }

            var delay = TimeSpan.FromSeconds(10 * attempt);
            Warning("git clone of {0} failed with exit code {1}, retrying in {2}s ({3}/{4})", repository, exit, delay.TotalSeconds, attempt, attempts);
            Thread.Sleep(delay);
        }
    }

    static void RestoreProjects(
        BuildData data,
        CatalogPackage package,
        DirectoryPath work,
        IReadOnlyList<FilePath> projects)
    {
        var binlogDir = BinlogDir(data, package, "rebuild");
        TimeSpan[] delays = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];
        foreach (var project in projects)
        {
            var binlog = binlogDir.CombineWithFilePath(BinlogName(work, project) + ".restore.binlog");
            for (var attempt = 0; ; attempt++)
            {
                var output = new List<string>();
                try
                {
                    DotNetRestore(project.FullPath, new DotNetRestoreSettings
                    {
                        WorkingDirectory = work,
                        MSBuildSettings = CakeMSBuildSettings(data, warningsAsErrors: false).EnableBinaryLogger(binlog.FullPath),
                        SetupProcessSettings = settings => settings.RedirectStandardOutput = true,
                        PostAction = process => output.AddRange(process.GetStandardOutput())
                    });
                    output.ForEach(line => Information(line));
                    break;
                }
                catch (CakeException ex)
                {
                    output.ForEach(line => Information(line));
                    // NU1301: a source could not be reached (DNS, connection); anything else is a real restore failure.
                    if (attempt == delays.Length || !output.Any(line => line.Contains("NU1301", StringComparison.Ordinal)))
                    {
                        throw;
                    }

                    Warning("{0} restore of {1} failed with NU1301 ({2}), retrying in {3}s ({4}/{5})", package.Id, project.GetFilename(), ex.Message, delays[attempt].TotalSeconds, attempt + 1, delays.Length);
                    Thread.Sleep(delays[attempt]);
                }
            }
        }
    }

    static void BuildProjects(
        BuildData data,
        CatalogPackage package,
        DirectoryPath work,
        IReadOnlyList<FilePath> projects,
        string check,
        bool warningsAsErrors,
        bool noRestore = false)
    {
        var binlogDir = BinlogDir(data, package, check);
        foreach (var project in projects)
        {
            var binlog = binlogDir.CombineWithFilePath(BinlogName(work, project) + ".binlog");
            DotNetBuild(project.FullPath, new DotNetBuildSettings
            {
                Configuration = "Release",
                WorkingDirectory = work,
                NoRestore = noRestore,
                MSBuildSettings = CakeMSBuildSettings(data, warningsAsErrors).EnableBinaryLogger(binlog.FullPath)
            });
        }
    }

    // Restore and build must see the same properties: the targets file rewrites Cake package versions.
    static DotNetMSBuildSettings CakeMSBuildSettings(BuildData data, bool warningsAsErrors)
        => new DotNetMSBuildSettings()
            .WithProperty("TargetFrameworks", data.Framework)
            .WithProperty("TargetFramework", data.Framework)
            .WithProperty("TreatWarningsAsErrors", warningsAsErrors ? "true" : "false")
            .WithProperty("CustomAfterMicrosoftCommonTargets", data.TargetsPath.FullPath)
            .WithProperty("CakePrereleaseCoreVersion", data.Versions.Core)
            .WithProperty("CakePrereleaseCommonVersion", data.Versions.Common)
            .WithProperty("CakePrereleaseFrostingVersion", data.Versions.Frosting)
            .WithProperty("CakePrereleaseSdkVersion", data.Versions.Sdk)
            .WithProperty("EnableSourceLink", "false")
            .WithProperty("PublishRepositoryUrl", "false")
            .WithProperty("EmbedUntrackedSources", "false")
            .WithProperty("DeterministicSourcePaths", "false");

    static DirectoryPath BinlogDir(BuildData data, CatalogPackage package, string check)
    {
        var binlogDir = data.Artifacts.Combine("binlog").Combine(package.Id).Combine(data.Framework).Combine(check);
        EnsureDirectoryExists(binlogDir);
        return binlogDir;
    }

    static string BinlogName(DirectoryPath work, FilePath project)
        => work.GetRelativePath(project).FullPath.Replace('\\', '/').Replace('/', '_');
}
