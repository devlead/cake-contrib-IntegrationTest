#:sdk Cake.Sdk@7.0.0-alpha0053
#:property IncludeAdditionalFiles=src/Versions.cs
#:property TargetFramework=net10.0

using System.Text.RegularExpressions;

var target = Argument("target", "Default");

Task("Default")
    .Does(async () =>
    {
        var versions = await Versions.ResolveCakeVersionsAsync(name => Argument($"cake-{name.ToLowerInvariant()}", ""));

        var table = new Table()
            .RoundedBorder()
            .AddColumn("Package")
            .AddColumn("Version");
        foreach (var (name, version) in versions.All())
        {
            table.AddRow($"Cake.{name}", Markup.Escape(version));
        }

        AnsiConsole.Write(table);

        var cakeScript = Context.FileSystem.GetFile(MakeAbsolute(File("cake.cs")));
        var text = cakeScript.ReadAllText();
        var patched = Regex.Replace(text, @"^#:sdk Cake\.Sdk@\S+", $"#:sdk Cake.Sdk@{versions.Sdk}", RegexOptions.Multiline);
        if (patched != text)
        {
            cakeScript.WriteAllText(patched);
            Information("Patched {0} to Cake.Sdk {1}", cakeScript.Path, versions.Sdk);
        }

        if (!GitHubActions.IsRunningOnGitHubActions)
        {
            return;
        }

        foreach (var (name, version) in versions.All())
        {
            GitHubActions.Commands.SetOutputParameter($"cake_{name.ToLowerInvariant()}", version);
            GitHubActions.Commands.SetEnvironmentVariable(Versions.EnvironmentVariableName(name), version);
        }
    });

RunTarget(target);
