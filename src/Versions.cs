public sealed class CakeVersions
{
    public string Tool { get; set; } = "";
    public string Sdk { get; set; } = "";
    public string Frosting { get; set; } = "";
    public string Core { get; set; } = "";
    public string Common { get; set; } = "";

    public IEnumerable<(string Name, string Version)> All()
    {
        yield return (nameof(Tool), Tool);
        yield return (nameof(Sdk), Sdk);
        yield return (nameof(Frosting), Frosting);
        yield return (nameof(Core), Core);
        yield return (nameof(Common), Common);
    }
}

public static class Versions
{
    public const string CakeFeed = "https://pkgs.dev.azure.com/cake-build/Cake/_packaging/cake/nuget/v3/index.json";
    public const string CakeFlat = "https://pkgs.dev.azure.com/cake-build/af63183c-ac1f-4dbb-93bc-4fa862ea5809/_packaging/063b39ab-3813-4d0c-aad5-71f9a60e38df/nuget/v3/flat2";
    public const string NuGetFlat = "https://api.nuget.org/v3-flatcontainer";

    public static readonly IComparer<string> Comparer = Comparer<string>.Create(CompareVersions);

    public static string EnvironmentVariableName(string name)
        => $"INTEGRATION_CAKE_{name.ToUpperInvariant()}_VERSION";

    public static async System.Threading.Tasks.Task<CakeVersions> ResolveCakeVersionsAsync(Func<string, string?> preset)
    {
        async System.Threading.Tasks.Task<string> Resolve(string name)
            => preset(name) is { Length: > 0 } version
                ? version
                : await HighestAsync($"{CakeFlat}/cake.{name.ToLowerInvariant()}/index.json", prerelease: true);

        return new CakeVersions
        {
            Tool = await Resolve(nameof(CakeVersions.Tool)),
            Sdk = await Resolve(nameof(CakeVersions.Sdk)),
            Frosting = await Resolve(nameof(CakeVersions.Frosting)),
            Core = await Resolve(nameof(CakeVersions.Core)),
            Common = await Resolve(nameof(CakeVersions.Common))
        };
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

        return versions.Max(Comparer)!;
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

    static int CompareVersions(string? x, string? y)
    {
        var (xNumber, xSuffix) = ParseVersion(x ?? "");
        var (yNumber, ySuffix) = ParseVersion(y ?? "");
        var number = xNumber.CompareTo(yNumber);
        if (number != 0)
        {
            return number;
        }

        // A release ranks above any of its pre-releases.
        return (xSuffix.Length, ySuffix.Length) switch
        {
            (0, 0) => 0,
            (0, _) => 1,
            (_, 0) => -1,
            _ => string.CompareOrdinal(xSuffix, ySuffix)
        };
    }
}
