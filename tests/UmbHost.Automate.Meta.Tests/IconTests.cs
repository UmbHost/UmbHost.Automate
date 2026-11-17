using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;

namespace UmbHost.Automate.Meta.Tests;

/// <summary>
/// The instagram mark: Umbraco's icon set has none, so the package registers its own through Umbraco's `icons` extension type, shipped as
/// static web assets at /App_Plugins/UmbHost.Automate.Meta/. The connection type and its actions name that icon.
/// </summary>
public sealed class IconTests
{
    private const string Icon = "icon-umbhost-instagram";
    private const string Folder = "App_Plugins/UmbHost.Automate.Meta";

    [Theory]
    [InlineData(typeof(Actions.CreateInstagramPostAction))]
    [InlineData(typeof(Actions.GetInstagramPostMetricsAction))]
    public void The_connection_and_its_actions_use_the_packages_icon(Type type)
    {
        var icon = type.GetCustomAttribute<ConnectionTypeAttribute>()?.Icon ?? type.GetCustomAttribute<ActionAttribute>()?.Icon;

        icon.Should().Be(Icon);
    }

    [Fact]
    public void The_package_registers_the_icon_it_names()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(WebRoot("umbraco-package.json")));
        var extension = manifest.RootElement.GetProperty("extensions").EnumerateArray().Should().ContainSingle().Subject;
        extension.GetProperty("type").GetString().Should().Be("icons");
        extension.GetProperty("js").GetString().Should().Be("/" + Folder + "/icons.js");

        File.ReadAllText(WebRoot("icons.js")).Should().Contain("name: \"" + Icon + "\"").And.Contain("import(\"./instagram.js\")");
        File.ReadAllText(WebRoot("instagram.js")).Should().Contain("export default `<svg").And.Contain("CC0 1.0");
    }

    /// <summary>A file the package ships, from the project's wwwroot (the repo is found from the test's output folder).</summary>
    private static string WebRoot(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UmbHost.Automate.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests run inside the repo");
        return Path.Combine(dir!.FullName, "src", "UmbHost.Automate.Meta", "wwwroot", Folder, file);
    }
}
