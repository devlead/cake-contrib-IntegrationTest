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
                var existing = GetDirectories(data.Artifacts.FullPath + "/*/" + package.Id + "/src")
                    .FirstOrDefault(d => DirectoryExists(d) && GetFiles(d.FullPath + "/**/*.csproj").Any());
                if (existing is not null)
                {
                    CopySourceTree(existing, work);
                }
                else
                {
                    var exit = StartProcess("git", new ProcessSettings
                    {
                        WorkingDirectory = parent,
                        Arguments = $"clone --depth 1 https://github.com/{package.Repository}.git \"{work.GetDirectoryName()}\""
                    });
                    if (exit != 0)
                    {
                        throw new CakeException($"git clone failed with exit code {exit}");
                    }
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
            BuildProjects(data, package, work, projects, check: "rebuild", warningsAsErrors: false);
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

    static void BuildProjects(
        BuildData data,
        CatalogPackage package,
        DirectoryPath work,
        IReadOnlyList<FilePath> projects,
        string check,
        bool warningsAsErrors)
    {
        var binlogDir = data.Artifacts.Combine("binlog").Combine(package.Id).Combine(data.Framework).Combine(check);
        EnsureDirectoryExists(binlogDir);
        foreach (var project in projects)
        {
            var relative = work.GetRelativePath(project).FullPath.Replace('\\', '/').Replace('/', '_');
            var binlog = binlogDir.CombineWithFilePath(relative + ".binlog");
            DotNetBuild(project.FullPath, new DotNetBuildSettings
            {
                Configuration = "Release",
                WorkingDirectory = work,
                MSBuildSettings = new DotNetMSBuildSettings()
                    .WithProperty("TargetFrameworks", data.Framework)
                    .WithProperty("TargetFramework", data.Framework)
                    .WithProperty("TreatWarningsAsErrors", warningsAsErrors ? "true" : "false")
                    .WithProperty("CustomAfterMicrosoftCommonTargets", data.TargetsPath.FullPath)
                    .WithProperty("CakePrereleaseCoreVersion", data.Versions.Core)
                    .WithProperty("CakePrereleaseCommonVersion", data.Versions.Common)
                    .WithProperty("EnableSourceLink", "false")
                    .WithProperty("PublishRepositoryUrl", "false")
                    .WithProperty("EmbedUntrackedSources", "false")
                    .WithProperty("DeterministicSourcePaths", "false")
                    .EnableBinaryLogger(binlog.FullPath)
            });
        }
    }
}
