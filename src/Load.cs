public static partial class Program
{
    public static async System.Threading.Tasks.Task InventoryPackageAsync(BuildData data, CatalogPackage package)
    {
        string? version;
        try
        {
            version = await Harness.LatestStableAsync(package.Id);
            data.ResolvedVersions[package.Id] = version;
        }
        catch (Exception ex)
        {
            Record(data, package.Id, "resolve", "failed", detail: ex.Message);
            return;
        }

        try
        {
            var inventory = await Harness.InventoryAsync(package.Id, version);
            data.Inventories[package.Id] = inventory;
            Record(
                data,
                package.Id,
                "inventory",
                "passed",
                detail: $"v{inventory.PackageVersion}",
                inventory: inventory);
        }
        catch (Exception ex)
        {
            Record(data, package.Id, "inventory", "failed", detail: ex.Message);
        }
    }

    public static System.Threading.Tasks.Task RunLoadAsync(
        BuildData data,
        CatalogPackage package,
        string runner,
        string version,
        Action run,
        PackageInventory? inventory = null)
    {
        if (!Harness.RunnerIncluded(package, runner))
        {
            Record(data, package.Id, $"load-{runner}", "skipped", reason: "runner not included", inventory: inventory);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        var exclusion = Harness.MatchExclusion(package, runner, data.Os, data.Framework, data.Architecture);
        if (exclusion is not null)
        {
            Record(data, package.Id, $"load-{runner}", "skipped", reason: exclusion.Reason, inventory: inventory);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        try
        {
            Information("{0} load-{1} {2}", package.Id, runner, version);
            run();
            Record(data, package.Id, $"load-{runner}", "passed", inventory: inventory);
        }
        catch (Exception ex)
        {
            Error("{0} load-{1}: {2}", package.Id, runner, ex.Message);
            Record(data, package.Id, $"load-{runner}", "failed", detail: ex.Message, inventory: inventory);
        }

        return System.Threading.Tasks.Task.CompletedTask;
    }

    public static void RunTool(BuildData data, CatalogPackage package, string version)
    {
        var work = WorkDir(data, package, "tool");
        CleanDirectory(work, new CleanDirectorySettings { Force = true });
        Harness.CopyNuGetConfig(Context, data.Root, work);
        var script = work.CombineWithFilePath("smoke.cake");
        var customSmoke = Harness.HasCustomSmokeFile(Context, data.Root, package);
        var body = Harness.ResolveSmokeBody(Context, data.Root, package);
        Context.FileSystem.GetFile(script).WriteAllText(Harness.ToolScript(package, version, body, customSmoke));
        CakeExecuteScript(script, new CakeSettings
        {
            WorkingDirectory = work,
            Verbosity = Verbosity.Verbose
        });
    }

    public static void RunSdk(BuildData data, CatalogPackage package, string version)
    {
        var work = WorkDir(data, package, "sdk");
        CleanDirectory(work, new CleanDirectorySettings { Force = true });
        Harness.CopyNuGetConfig(Context, data.Root, work);
        var customSmoke = Harness.HasCustomSmokeFile(Context, data.Root, package);
        var body = Harness.ResolveSmokeBody(Context, data.Root, package);
        var code = Harness.SdkScript(package, version, data.Versions.Sdk, data.Framework, body, customSmoke);
        DotNetRunCode(code, new DotNetRunSettings { WorkingDirectory = work });
    }

    public static void RunFrosting(BuildData data, CatalogPackage package, string version)
    {
        var work = WorkDir(data, package, "frosting");
        CleanDirectory(work, new CleanDirectorySettings { Force = true });
        Harness.CopyNuGetConfig(Context, data.Root, work);
        var customSmoke = Harness.HasCustomSmokeFile(Context, data.Root, package);
        Context.FileSystem.GetFile(work.CombineWithFilePath("Build.csproj"))
            .WriteAllText(Harness.FrostingProject(package.Id, version, data.Versions.Frosting, data.Framework));
        Context.FileSystem.GetFile(work.CombineWithFilePath("Program.cs"))
            .WriteAllText(Harness.FrostingProgram(
                package,
                Harness.ResolveSmokeBody(Context, data.Root, package),
                customSmoke));
        DotNetRun(
            work.CombineWithFilePath("Build.csproj").FullPath,
            $"--target={Harness.SmokeTaskName(customSmoke)}",
            new DotNetRunSettings { WorkingDirectory = work });
    }
}
