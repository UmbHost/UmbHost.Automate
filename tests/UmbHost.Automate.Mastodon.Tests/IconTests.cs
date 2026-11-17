using System.Reflection;
using FluentAssertions;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;

namespace UmbHost.Automate.Mastodon.Tests;

/// <summary>The connection type and its post action use Umbraco's own icon-mastodon (in its icon set since 17.6), not a generic icon.</summary>
public sealed class IconTests
{
    [Theory]
    [InlineData(typeof(Connection.MastodonConnectionType))]
    [InlineData(typeof(Actions.CreateStatusAction))]
    public void The_connection_and_its_post_action_use_umbracos_icon(Type type)
    {
        var icon = type.GetCustomAttribute<ConnectionTypeAttribute>()?.Icon ?? type.GetCustomAttribute<ActionAttribute>()?.Icon;

        icon.Should().Be("icon-mastodon");
    }
}
