public sealed class BuildData
{
    public required IReadOnlyList<string> TargetFrameworks { get; init; }
    public string Framework { get; set; } = "";
    public required DirectoryPath Root { get; init; }
    public required FilePath CatalogPath { get; init; }
    public required DirectoryPath Artifacts { get; init; }
    public required FilePath TargetsPath { get; init; }
    public required string Os { get; init; }
    public required string Architecture { get; init; }
    public string Shard { get; init; } = "";
    public DirectoryPath WorkRoot => Artifacts.Combine(Framework);
    public DirectoryPath SnapshotsDir => Root.Combine("snapshots");
    public DirectoryPath ReceivedSnapshotsDir => Artifacts.Combine("snapshots");
    public List<CatalogPackage> Packages { get; } = [];
    public List<CheckResult> Results { get; } = [];
    public Dictionary<string, PackageInventory> Inventories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResolvedVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public CakeVersions Versions { get; set; } = null!;
}
