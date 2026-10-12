using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Xml.Linq;

public sealed class CatalogPackage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "addin";

    [JsonPropertyName("repository")]
    public string Repository { get; set; } = "";

    [JsonPropertyName("smoke")]
    public string? Smoke { get; set; }

    [JsonPropertyName("runners")]
    public List<string>? Runners { get; set; }

    [JsonPropertyName("usings")]
    public List<string>? Usings { get; set; }

    [JsonPropertyName("exclusions")]
    public List<ExclusionRule>? Exclusions { get; set; }

    public bool IsModule => string.Equals(Kind, "module", StringComparison.OrdinalIgnoreCase);
}

public sealed class ExclusionRule
{
    [JsonPropertyName("runner")]
    public List<string>? Runner { get; set; }

    [JsonPropertyName("os")]
    public List<string>? Os { get; set; }

    [JsonPropertyName("framework")]
    public List<string>? Framework { get; set; }

    [JsonPropertyName("architecture")]
    public List<string>? Architecture { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "excluded";
}

public sealed class CheckResult
{
    public string Id { get; set; } = "";
    public string Check { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public string? Detail { get; set; }
    public string? PackageVersion { get; set; }
    public string? CakeCore { get; set; }
    public string? Tfms { get; set; }
    public string? Framework { get; set; }
}

public sealed class PackageInventory
{
    public string PackageVersion { get; set; } = "";
    public string CakeCore { get; set; } = "";
    public string Tfms { get; set; } = "";
}

public sealed record LoadWork(CatalogPackage Package, string Framework, string Version, PackageInventory? Inventory);

public sealed class CakeVersions
{
    public string Tool { get; set; } = "";
    public string Sdk { get; set; } = "";
    public string Frosting { get; set; } = "";
    public string Core { get; set; } = "";
    public string Common { get; set; } = "";
}

public static class Harness
{
    public const string CakeFeed = "https://pkgs.dev.azure.com/cake-build/Cake/_packaging/cake/nuget/v3/index.json";
    public const string CakeFlat = "https://pkgs.dev.azure.com/cake-build/af63183c-ac1f-4dbb-93bc-4fa862ea5809/_packaging/063b39ab-3813-4d0c-aad5-71f9a60e38df/nuget/v3/flat2";
    public const string NuGetFlat = "https://api.nuget.org/v3-flatcontainer";

    public static readonly string[] AllRunners = ["tool", "sdk", "frosting"];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string HostOs(ICakeContext context)
    {
        if (context.Environment.Platform.Family == PlatformFamily.Windows)
        {
            return "windows";
        }

        if (context.Environment.Platform.Family == PlatformFamily.OSX)
        {
            return "osx";
        }

        return "linux";
    }

    public static string HostArchitecture()
        => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static List<CatalogPackage> LoadCatalog(ICakeContext context, FilePath path)
    {
        var json = context.FileSystem.GetFile(path).ReadAllText();
        return JsonSerializer.Deserialize<List<CatalogPackage>>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to read catalog {path}");
    }

    public static void WriteSelectedPackages(IEnumerable<CatalogPackage> packages)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Selected packages[/]")
            .AddColumn("Package")
            .AddColumn("Kind")
            .AddColumn("Runners")
            .AddColumn("Repository");
        foreach (var package in packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var runners = package.Runners is { Count: > 0 } ? string.Join(", ", package.Runners) : string.Join(", ", AllRunners);
            table.AddRow(
                Markup.Escape(package.Id),
                Markup.Escape(package.Kind),
                Markup.Escape(runners),
                Markup.Escape(package.Repository));
        }

        AnsiConsole.Write(table);
    }

    public static void SaveCatalog(ICakeContext context, FilePath path, IEnumerable<CatalogPackage> packages)
    {
        var json = JsonSerializer.Serialize(packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList(), JsonOptions);
        context.FileSystem.GetFile(path).WriteAllText(json + Environment.NewLine);
    }

    public static IEnumerable<LoadWork> LoadWorks(BuildData data)
    {
        foreach (var package in data.Packages)
        {
            if (!data.ResolvedVersions.TryGetValue(package.Id, out var version))
            {
                continue;
            }

            data.Inventories.TryGetValue(package.Id, out var inventory);
            foreach (var framework in data.TargetFrameworks)
            {
                yield return new LoadWork(package, framework, version, inventory);
            }
        }
    }

    public static IReadOnlyList<string> ParseFrameworks(params IEnumerable<string>[] values)
    {
        var frameworks = values
            .SelectMany(list => list)
            .SelectMany(value => value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return frameworks.Count > 0 ? frameworks : ["net10.0"];
    }

    public static IEnumerable<CatalogPackage> FilterPackages(
        IEnumerable<CatalogPackage> packages,
        IEnumerable<string>? packageFilter,
        string? shard)
    {
        var selected = packages.ToList();
        var names = (packageFilter ?? [])
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        if (names.Count > 0)
        {
            selected = [.. selected.Where(p => names.Any(n => string.Equals(p.Id, n, StringComparison.OrdinalIgnoreCase)))];
        }

        if (string.IsNullOrWhiteSpace(shard))
        {
            return selected;
        }

        var parts = shard.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var index)
            || !int.TryParse(parts[1], out var count)
            || index < 1
            || count < 1
            || index > count)
        {
            throw new ArgumentException($"Shard must be i/n (1-based), got '{shard}'.");
        }

        return selected
            .Select((package, i) => (package, i))
            .Where(x => x.i % count == index - 1)
            .Select(x => x.package)
            .ToList();
    }

    public static bool RunnerIncluded(CatalogPackage package, string runner)
    {
        if (package.Runners is null || package.Runners.Count == 0)
        {
            return true;
        }

        return package.Runners.Any(r => string.Equals(r, runner, StringComparison.OrdinalIgnoreCase));
    }

    public static ExclusionRule? MatchExclusion(
        CatalogPackage package,
        string? runner,
        string os,
        string framework,
        string architecture)
    {
        if (package.Exclusions is null)
        {
            return null;
        }

        foreach (var rule in package.Exclusions)
        {
            if (!Matches(rule.Runner, runner)
                || !Matches(rule.Os, os)
                || !Matches(rule.Framework, framework)
                || !Matches(rule.Architecture, architecture))
            {
                continue;
            }

            return rule;
        }

        return null;
    }

    static bool Matches(List<string>? values, string? actual)
    {
        if (values is null || values.Count == 0)
        {
            return true;
        }

        if (actual is null)
        {
            return false;
        }

        return values.Any(v => string.Equals(v, actual, StringComparison.OrdinalIgnoreCase));
    }

    public static async System.Threading.Tasks.Task<CakeVersions> ResolveCakeVersionsAsync()
    {
        return new CakeVersions
        {
            Tool = await HighestAsync($"{CakeFlat}/cake.tool/index.json", prerelease: true),
            Sdk = await HighestAsync($"{CakeFlat}/cake.sdk/index.json", prerelease: true),
            Frosting = await HighestAsync($"{CakeFlat}/cake.frosting/index.json", prerelease: true),
            Core = await HighestAsync($"{CakeFlat}/cake.core/index.json", prerelease: true),
            Common = await HighestAsync($"{CakeFlat}/cake.common/index.json", prerelease: true)
        };
    }

    public static System.Threading.Tasks.Task<string> LatestStableAsync(string packageId)
        => HighestAsync($"{NuGetFlat}/{packageId.ToLowerInvariant()}/index.json", prerelease: false);

    public static async System.Threading.Tasks.Task<PackageInventory> InventoryAsync(string packageId, string version)
    {
        var id = packageId.ToLowerInvariant();
        var ver = version.ToLowerInvariant();
        var url = $"{NuGetFlat}/{id}/{ver}/{id}.{ver}.nupkg";
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("cake-contrib-integrationtest");
        var bytes = await client.GetByteArrayAsync(url);
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var tfms = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var cakeCores = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var libDlls = new List<ZipArchiveEntry>();
        var packageDlls = new List<ZipArchiveEntry>();

        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase))
            {
                tfms.Add(NormalizeTfm(parts[1]));
            }

            if (parts.Length >= 3
                && parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase)
                && parts[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                libDlls.Add(entry);
                if (parts[^1].Equals(packageId + ".dll", StringComparison.OrdinalIgnoreCase))
                {
                    packageDlls.Add(entry);
                }
            }
        }

        foreach (var entry in packageDlls.Count > 0 ? packageDlls : libDlls)
        {
            foreach (var cakeCore in ReadCakeCoreReferences(entry))
            {
                cakeCores.Add(cakeCore);
            }
        }

        var nuspec = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (nuspec is not null)
        {
            using var specStream = nuspec.Open();
            var document = XDocument.Load(specStream);
            XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
            foreach (var group in document.Descendants(ns + "group"))
            {
                var tfm = group.Attribute("targetFramework")?.Value;
                if (!string.IsNullOrWhiteSpace(tfm))
                {
                    tfms.Add(NormalizeTfm(tfm));
                }
            }

            foreach (var dependency in document.Descendants(ns + "dependency"))
            {
                var depId = dependency.Attribute("id")?.Value;
                if (!string.Equals(depId, "Cake.Core", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var depVersion = dependency.Attribute("version")?.Value;
                if (!string.IsNullOrWhiteSpace(depVersion))
                {
                    cakeCores.Add(depVersion);
                }
            }
        }

        return new PackageInventory
        {
            PackageVersion = version,
            CakeCore = cakeCores.Count == 0 ? "" : string.Join("; ", cakeCores),
            Tfms = tfms.Count == 0 ? "" : string.Join(", ", tfms)
        };
    }

    static IEnumerable<string> ReadCakeCoreReferences(ZipArchiveEntry entry)
    {
        try
        {
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            buffer.Position = 0;
            using var pe = new PEReader(buffer);
            if (!pe.HasMetadata)
            {
                return [];
            }

            var found = new List<string>();
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                if (!string.Equals(metadata.GetString(reference.Name), "Cake.Core", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var version = reference.Version;
                found.Add(version.Revision == 0 ? version.ToString(3) : version.ToString());
            }

            return found;
        }
        catch
        {
            return [];
        }
    }

    static string NormalizeTfm(string tfm)
    {
        tfm = tfm.Trim();
        if (tfm.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase))
        {
            return "netstandard" + tfm[".NETStandard".Length..];
        }

        if (tfm.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            return "netcoreapp" + tfm[".NETCoreApp".Length..];
        }

        if (tfm.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
        {
            var digits = new string([.. tfm.SkipWhile(c => !char.IsDigit(c))]).Replace(".", "");
            return "net" + digits;
        }

        return tfm;
    }

    public static async System.Threading.Tasks.Task<string> HighestAsync(string url, bool prerelease)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("cake-contrib-integrationtest");
        var json = await client.GetStringAsync(url);
        using var document = JsonDocument.Parse(json);
        var versions = document.RootElement.GetProperty("versions")
            .EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Where(v => prerelease || v.IndexOf('-') < 0)
            .ToList();
        if (versions.Count == 0)
        {
            throw new InvalidOperationException($"No versions at {url}");
        }

        return versions.OrderBy(ParseVersion).Last();
    }

    public static (Version Number, string Suffix) ParseVersion(string version)
    {
        var dash = version.IndexOf('-');
        var numeric = dash < 0 ? version : version[..dash];
        var suffix = dash < 0 ? "" : version[(dash + 1)..];
        var parts = numeric.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
        var build = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 0;
        var revision = parts.Length > 3 && int.TryParse(parts[3], out var d) ? d : 0;
        return (new Version(major, minor, build, revision), suffix);
    }

    public static FilePath SmokeFilePath(DirectoryPath root, CatalogPackage package)
        => root.Combine("smoke").Combine(package.Id).CombineWithFilePath(package.Id + ".cs");

    public static bool HasCustomSmokeFile(ICakeContext context, DirectoryPath root, CatalogPackage package)
        => context.FileSystem.GetFile(SmokeFilePath(root, package)).Exists;

    public static string SmokeTaskName(bool customSmoke)
        => customSmoke ? "Real-Smoke" : "Smoke";

    public static string ResolveSmokeBody(ICakeContext context, DirectoryPath root, CatalogPackage package)
    {
        var file = context.FileSystem.GetFile(SmokeFilePath(root, package));
        if (file.Exists)
        {
            return file.ReadAllText().Trim();
        }

        if (!string.IsNullOrWhiteSpace(package.Smoke))
        {
            return package.Smoke;
        }

        return """
            context.Log.Write(Cake.Core.Diagnostics.Verbosity.Normal, Cake.Core.Diagnostics.LogLevel.Information, "loaded");
            """;
    }

    public static string ToolScript(CatalogPackage package, string version, string body, bool customSmoke)
    {
        var directive = package.IsModule ? "#module" : "#addin";
        var task = SmokeTaskName(customSmoke);
        return $$"""
            {{directive}} nuget:?package={{package.Id}}&version={{version}}
            Task("{{task}}").Does(context =>
            {
            {{IndentSmokeBody(body)}}
            });
            RunTarget("{{task}}");
            """;
    }

    public static string SdkScript(CatalogPackage package, string version, string cakeSdk, string framework, string body, bool customSmoke)
    {
        var task = SmokeTaskName(customSmoke);
        return $$"""
            #:sdk Cake.Sdk@{{cakeSdk}}
            #:property TargetFramework={{framework}}
            #:package {{package.Id}}@{{version}}
            Task("{{task}}").Does(context =>
            {
            {{IndentSmokeBody(body)}}
            });
            RunTarget("{{task}}");
            """;
    }

    static string IndentSmokeBody(string body)
        => string.Join(
            Environment.NewLine,
            body.Replace("\r\n", "\n").Split('\n').Select(line => string.IsNullOrWhiteSpace(line) ? "" : "    " + line));

    public static string FrostingProject(string packageId, string packageVersion, string frostingVersion, string framework)
        => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{framework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Cake.Frosting" Version="{frostingVersion}" />
                <PackageReference Include="{packageId}" Version="{packageVersion}" />
              </ItemGroup>
            </Project>
            """;

    public static string FrostingProgram(CatalogPackage package, string body, bool customSmoke)
        => $$"""
            using System.Reflection;
            using Cake.Core;
            using Cake.Core.Composition;
            using Cake.Core.IO;
            using Cake.Frosting;
            {{FrostingAliasUsings(package, customSmoke)}}

            return new FrostingApp().Run(args);

            public sealed class FrostingApp
            {
                public int Run(string[] args)
                {
                    var host = new CakeHost().UseContext<BuildContext>();
                    var assembly = LoadPackageAssembly("{{package.Id}}", [{{FrostingAssemblyNames(package)}}]);
                    _ = assembly.GetTypes();
                    var useModule = FindUseModule();
                    foreach (var moduleType in assembly.GetTypes().Where(IsConcreteModule))
                    {
                        if (useModule is null)
                        {
                            Activator.CreateInstance(moduleType);
                            continue;
                        }

                        var invokeArgs = useModule.IsStatic ? new object[] { host } : Array.Empty<object>();
                        useModule.MakeGenericMethod(moduleType).Invoke(useModule.IsStatic ? null : host, invokeArgs);
                    }

                    return host.Run(args);
                }

                static MethodInfo? FindUseModule()
                {
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
                    return typeof(CakeHost).GetMethods(flags)
                        .Concat(typeof(CakeHost).Assembly.GetTypes().SelectMany(t => t.GetMethods(flags)))
                        .FirstOrDefault(m => m.Name == "UseModule" && m.IsGenericMethodDefinition);
                }

                static bool IsConcreteModule(Type type)
                    => type is { IsClass: true, IsAbstract: false }
                        && typeof(ICakeModule).IsAssignableFrom(type)
                        && type.GetConstructor(Type.EmptyTypes) is not null;

                static Assembly LoadPackageAssembly(string packageId, string[] aliases)
                {
                    foreach (var name in aliases.Prepend(packageId).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var existing = AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                        if (existing is not null)
                        {
                            return existing;
                        }

                        try
                        {
                            return Assembly.Load(new AssemblyName(name));
                        }
                        catch (FileNotFoundException)
                        {
                        }
                    }

                    throw new FileNotFoundException($"Could not load assembly for {packageId}.");
                }
            }

            public sealed class BuildContext : FrostingContext
            {
                public BuildContext(ICakeContext context) : base(context) { }
            }

            [TaskName("{{SmokeTaskName(customSmoke)}}")]
            public sealed class SmokeTask : FrostingTask<BuildContext>
            {
                public override void Run(BuildContext context)
                {
            {{IndentSmokeBody(body)}}
                }
            }
            """;

    static string FrostingAssemblyNames(CatalogPackage package)
        => string.Join(", ", (package.Usings ?? []).Select(name => $"\"{name}\""));

    static string FrostingAliasUsings(CatalogPackage package, bool customSmoke)
    {
        if (!customSmoke)
        {
            return "";
        }

        var names = package.Usings is { Count: > 0 } ? package.Usings : [package.Id];
        return string.Concat(names.Select(name => $"using {name};{Environment.NewLine}"));
    }

    public static void CopyNuGetConfig(ICakeContext context, DirectoryPath sourceRoot, DirectoryPath destination)
    {
        context.EnsureDirectoryExists(destination);
        context.CopyFile(sourceRoot.CombineWithFilePath("nuget.config"), destination.CombineWithFilePath("nuget.config"));
    }

    public static bool ReferencesCake(ICakeContext context, FilePath project)
    {
        var text = context.FileSystem.GetFile(project).ReadAllText();
        return text.Contains("Cake.Core", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Cake.Common", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTestProject(FilePath project)
    {
        var name = project.GetFilename().FullPath;
        return name.Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    public static void WriteResults(ICakeContext context, FilePath path, object payload)
    {
        context.EnsureDirectoryExists(path.GetDirectory());
        context.FileSystem.GetFile(path).WriteAllText(JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine);
    }

    public static string MarkdownSummary(IReadOnlyList<CheckResult> results, CakeVersions versions, string os, string architecture, IReadOnlyList<string> frameworks)
    {
        var passed = results.Count(r => r.Status == "passed");
        var failed = results.Count(r => r.Status == "failed");
        var skipped = results.Count(r => r.Status == "skipped");
        var lines = new List<string>
        {
            $"# Cake contrib integration tests",
            "",
            $"- OS: `{os}`",
            $"- Architecture: `{architecture}`",
            $"- TargetFrameworks: `{string.Join(", ", frameworks)}`",
            $"- Cake.Tool: `{versions.Tool}`",
            $"- Cake.Sdk: `{versions.Sdk}`",
            $"- Cake.Frosting: `{versions.Frosting}`",
            $"- Cake.Core: `{versions.Core}`",
            $"- Cake.Common: `{versions.Common}`",
            $"- Passed: {passed}",
            $"- Failed: {failed}",
            $"- Skipped: {skipped}",
            "",
            "| Package | Inventory | Tool | SDK | Frosting | Build | Warning | Cake.Core | TFMs |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"
        };
        foreach (var row in PackageResultRows(results))
        {
            lines.Add($"| {row.Id} | {row.Inventory} | {row.Tool} | {row.Sdk} | {row.Frosting} | {row.Build} | {row.Warning} | {row.CakeCore} | {row.Tfms} |");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static void WriteSpectreSummary(IReadOnlyList<CheckResult> results, CakeVersions versions, string os, string architecture, IReadOnlyList<string> frameworks)
    {
        var passed = results.Count(r => r.Status == "passed");
        var failed = results.Count(r => r.Status == "failed");
        var skipped = results.Count(r => r.Status == "skipped");

        var meta = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Cake contrib integration tests[/]")
            .AddColumn("Property")
            .AddColumn("Value");
        meta.AddRow("OS", Markup.Escape(os));
        meta.AddRow("Architecture", Markup.Escape(architecture));
        meta.AddRow("TargetFrameworks", Markup.Escape(string.Join(", ", frameworks)));
        meta.AddRow("Cake.Tool", Markup.Escape(versions.Tool));
        meta.AddRow("Cake.Sdk", Markup.Escape(versions.Sdk));
        meta.AddRow("Cake.Frosting", Markup.Escape(versions.Frosting));
        meta.AddRow("Cake.Core", Markup.Escape(versions.Core));
        meta.AddRow("Cake.Common", Markup.Escape(versions.Common));
        meta.AddRow("Passed", $"[green]{passed}[/]");
        meta.AddRow("Failed", failed == 0 ? "[green]0[/]" : $"[red]{failed}[/]");
        meta.AddRow("Skipped", $"[yellow]{skipped}[/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Check results[/]")
            .AddColumn("Package")
            .AddColumn("Inventory")
            .AddColumn("Tool")
            .AddColumn("SDK")
            .AddColumn("Frosting")
            .AddColumn("Build")
            .AddColumn("Warning")
            .AddColumn("Cake.Core")
            .AddColumn("TFMs");

        foreach (var row in PackageResultRows(results))
        {
            table.AddRow(
                Markup.Escape(row.Id),
                StatusMarkup(row.Inventory),
                StatusMarkup(row.Tool),
                StatusMarkup(row.Sdk),
                StatusMarkup(row.Frosting),
                StatusMarkup(row.Build),
                StatusMarkup(row.Warning),
                Markup.Escape(row.CakeCore),
                Markup.Escape(row.Tfms));
        }

        AnsiConsole.Write(meta);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
    }

    static IEnumerable<PackageResultRow> PackageResultRows(IEnumerable<CheckResult> results)
    {
        foreach (var group in results.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            var checks = group.ToList();
            var inventory = checks.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.CakeCore) || !string.IsNullOrWhiteSpace(r.Tfms));
            yield return new PackageResultRow(
                group.Key,
                Rollup(checks, "inventory", "resolve"),
                Rollup(checks, "load-tool"),
                Rollup(checks, "load-sdk"),
                Rollup(checks, "load-frosting"),
                Rollup(checks, "rebuild"),
                Rollup(checks, "rebuild-warning"),
                inventory?.CakeCore ?? "",
                inventory?.Tfms ?? "");
        }
    }

    static string Rollup(IReadOnlyList<CheckResult> checks, params string[] names)
    {
        var matched = checks
            .Where(r => names.Any(name => string.Equals(r.Check, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matched.Count == 0)
        {
            return "";
        }

        if (matched.Any(r => r.Status == "failed"))
        {
            return "failed";
        }

        if (matched.Any(r => r.Status == "passed"))
        {
            return "passed";
        }

        return matched[0].Status;
    }

    static string StatusMarkup(string status)
        => status switch
        {
            "" => "",
            "passed" => "[green]passed[/]",
            "failed" => "[red]failed[/]",
            "skipped" => "[yellow]skipped[/]",
            _ => Markup.Escape(status)
        };

    sealed record PackageResultRow(
        string Id,
        string Inventory,
        string Tool,
        string Sdk,
        string Frosting,
        string Build,
        string Warning,
        string CakeCore,
        string Tfms);

}
