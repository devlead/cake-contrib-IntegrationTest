#:sdk Cake.Sdk@7.0.0-alpha0055
#:property IncludeAdditionalFiles=src/**/*.cs
#:property TargetFramework=net10.0

var target = Argument("target", "Test");

Setup<BuildData>(context =>
{
    var targetFrameworks = Harness.ParseFrameworks(
        Arguments<string>("framework", Array.Empty<string>()),
        Arguments<string>("targetframeworks", Array.Empty<string>()));
    var packageFilter = Arguments<string>("package", Array.Empty<string>());
    var shard = Argument("shard", "");
    var root = MakeAbsolute(Directory("."));
    var artifacts = root.Combine("build");
    var data = new BuildData
    {
        TargetFrameworks = targetFrameworks,
        Framework = targetFrameworks[0],
        Root = root,
        CatalogPath = root.CombineWithFilePath("catalog/addins.json"),
        Artifacts = artifacts,
        TargetsPath = root.CombineWithFilePath("src/CakePrerelease.targets"),
        Os = Harness.HostOs(context),
        Architecture = Harness.HostArchitecture(),
        Shard = shard
    };

    EnsureDirectoryExists(data.Artifacts);
    data.Packages.AddRange(Harness.FilterPackages(Harness.LoadCatalog(context, data.CatalogPath), packageFilter, shard));
    AnsiConsole.Write(
        new Table()
            .RoundedBorder()
            .HideHeaders()
            .ShowRowSeparators()
            .AddColumn("Property")
            .AddColumn("Value")
            .AddRow("OS", Markup.Escape(data.Os))
            .AddRow("Architecture", Markup.Escape(data.Architecture))
            .AddRow("TargetFrameworks", Markup.Escape(string.Join(", ", data.TargetFrameworks)))
            .AddRow("Package filter", packageFilter.Count == 0 ? "[yellow]none[/]" : Markup.Escape(string.Join(", ", packageFilter)))
            .AddRow("Shard", string.IsNullOrWhiteSpace(shard) ? "[yellow]none[/]" : Markup.Escape(shard))
            .AddRow("Packages", data.Packages.Count.ToString())
            .AddRow("Cake.Generator", Markup.Escape(CakeGeneratorVersion))
            .AddRow("Cake.Generator Date", Markup.Escape(CakeGeneratorDate))
            .AddRow("Cake.Generator Informational", Markup.Escape(CakeGeneratorInformationalVersion))
            .AddRow("Cake.Generator NuGet", Markup.Escape(CakeGeneratorNuGetVersion)));
    return data;
});

Teardown<BuildData>((context, data) => UploadArtifactsAsync(data).GetAwaiter().GetResult());

Task("Discover")
    .Does<BuildData>(data =>
    {
        var catalog = Harness.LoadCatalog(Context, data.CatalogPath);
        Information("Catalog packages: {0}", catalog.Count);
        Information("Selected packages: {0}", data.Packages.Count);
        Harness.SaveCatalog(Context, data.Artifacts.CombineWithFilePath("catalog.json"), catalog);
        Harness.SaveCatalog(Context, data.Artifacts.CombineWithFilePath("selected.json"), data.Packages);
        Harness.WriteSelectedPackages(data.Packages);
    });

Task("Prepare")
    .Does<BuildData>(async data =>
    {
        data.Versions = await Versions.ResolveCakeVersionsAsync(name => EnvironmentVariable(Versions.EnvironmentVariableName(name)));
        Information(
            "Cake.Tool={0} Cake.Sdk={1} Cake.Frosting={2} Cake.Core={3} Cake.Common={4}",
            data.Versions.Tool,
            data.Versions.Sdk,
            data.Versions.Frosting,
            data.Versions.Core,
            data.Versions.Common);
        InstallTool($"dotnet:{Versions.CakeFeed}?package=Cake.Tool&version={data.Versions.Tool}");
    });

Task("Test-Inventory")
    .IsDependentOn("Prepare")
    .DoesForEach<BuildData, CatalogPackage>(
        data => data.Packages,
        async (data, package) => await InventoryPackageAsync(data, package));

Task("Test-Load-Tool")
    .IsDependentOn("Test-Inventory")
    .DoesForEach<BuildData, LoadWork>(
        data => Harness.LoadWorks(data),
        async (data, work) =>
        {
            data.Framework = work.Framework;
            await RunLoadAsync(data, work.Package, "tool", work.Version, () => RunTool(data, work.Package, work.Version), work.Inventory);
        });

Task("Test-Load-Sdk")
    .IsDependentOn("Test-Inventory")
    .DoesForEach<BuildData, LoadWork>(
        data => Harness.LoadWorks(data),
        async (data, work) =>
        {
            data.Framework = work.Framework;
            await RunLoadAsync(data, work.Package, "sdk", work.Version, () => RunSdk(data, work.Package, work.Version), work.Inventory);
        });

Task("Test-Load-Frosting")
    .IsDependentOn("Test-Inventory")
    .DoesForEach<BuildData, LoadWork>(
        data => Harness.LoadWorks(data),
        async (data, work) =>
        {
            data.Framework = work.Framework;
            await RunLoadAsync(data, work.Package, "frosting", work.Version, () => RunFrosting(data, work.Package, work.Version), work.Inventory);
        });

Task("Test-Load")
    .IsDependentOn("Test-Load-Tool")
    .IsDependentOn("Test-Load-Sdk")
    .IsDependentOn("Test-Load-Frosting");

Task("Test-Build")
    .IsDependentOn("Prepare")
    .Does<BuildData>(data =>
    {
        foreach (var framework in data.TargetFrameworks)
        {
            data.Framework = framework;
            foreach (var package in data.Packages)
            {
                RunRebuild(data, package);
            }
        }
    });

Task("Test")
    .IsDependentOn("Discover")
    .IsDependentOn("Test-Load")
    .IsDependentOn("Test-Build")
    .Does<BuildData>(data =>
    {
        WriteSummary(data);
        if (VerifySnapshots(data) > 0)
        {
            throw new CakeException("Integration results differ from snapshot. Review build/snapshots/*.received.json, run --target=Accept, and commit.");
        }
    });

Task("Accept")
    .Does<BuildData>(data => AcceptSnapshots(data, Argument("run", "")));

RunTarget(target);
