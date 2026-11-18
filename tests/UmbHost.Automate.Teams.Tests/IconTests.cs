using System.Reflection;
using FluentAssertions;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;

namespace UmbHost.Automate.Teams.Tests;

/// <summary>Umbraco's icon set has no Teams icon, so the connection type and action use its icon-microsoft.</summary>
public sealed class IconTests
{
    [Theory]
    [InlineData(typeof(Connection.TeamsConnectionType))]
    [InlineData(typeof(Actions.PostCardAction))]
    public void The_connection_and_its_action_use_umbracos_microsoft_icon(Type type)
    {
        var icon = type.GetCustomAttribute<ConnectionTypeAttribute>()?.Icon ?? type.GetCustomAttribute<ActionAttribute>()?.Icon;

        icon.Should().Be("icon-microsoft");
    }
}
