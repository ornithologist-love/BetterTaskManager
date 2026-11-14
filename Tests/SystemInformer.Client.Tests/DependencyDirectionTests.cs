using System.Xml.Linq;
using Xunit;

namespace SystemInformer.Client.Tests;

/// <summary>
///     Dependencies point one way: Facade/Heron -> SystemInformer.PluginFacade -> Source. Source never depends on a
///     facade, so the pages and native scanner stay usable by both the plugin and the standalone windows.
/// </summary>
[Anchor("AGENTS.md")]
public sealed class DependencyDirectionTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Source_projects_reference_only_source_projects()
    {
        var projects = Projects("Source").ToList();
        Assert.NotEmpty(projects);
        foreach (var project in projects)
            foreach (var reference in References(project))
                Assert.True(IsUnder(reference, "Source"), $"{Relative(project)} references {Relative(reference)} outside Source");
    }

    [Fact]
    public void Plugin_facade_does_not_reference_host_or_standalone_wiring()
    {
        var facade = Projects(Path.Combine("Facade", "SystemInformer.PluginFacade")).Single();
        foreach (var reference in References(facade))
            Assert.True(IsUnder(reference, "Source"), $"{Relative(facade)} references {Relative(reference)}; only Source is allowed");
    }

    [Fact]
    public void Source_and_plugin_facade_do_not_reference_the_host_framework()
    {
        foreach (var project in Projects("Source").Append(Projects(Path.Combine("Facade", "SystemInformer.PluginFacade")).Single()))
        {
            var packages = XDocument.Load(project).Descendants("PackageReference")
                .Select(item => (string?)item.Attribute("Include") ?? "");
            Assert.DoesNotContain(packages, name => name.StartsWith("Heron.", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<string> Projects(string directory) =>
        Directory.EnumerateFiles(Path.Combine(Root, directory), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    private static IEnumerable<string> References(string project) =>
        XDocument.Load(project).Descendants("ProjectReference")
            .Select(item => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!,
                ((string?)item.Attribute("Include") ?? "").Replace('\\', Path.DirectorySeparatorChar))));

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(Path.Combine(Root, directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Relative(string path) => Path.GetRelativePath(Root, path);

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "build.py")) && Directory.Exists(Path.Combine(directory.FullName, "Source")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found above the test output directory.");
    }
}
