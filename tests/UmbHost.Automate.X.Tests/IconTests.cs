using System.Reflection;
using FluentAssertions;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;

namespace UmbHost.Automate.X.Tests;

/// <summary>The connection type and its post action use Umbraco's own icon-twitter-x (in its icon set since 17.6), not a generic icon.</summary>
public sealed class IconTests
{
    [Theory]
    [InlineData(typeof(Connection.XConnectionType))]
    [InlineData(typeof(Actions.CreatePostAction))]
    public void The_connection_and_its_post_action_use_umbracos_icon(Type type)
    {
        var icon = type.GetCustomAttribute<ConnectionTypeAttribute>()?.Icon ?? type.GetCustomAttribute<ActionAttribute>()?.Icon;

        icon.Should().Be("icon-twitter-x");
    }
}
