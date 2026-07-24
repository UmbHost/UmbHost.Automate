using System.Reflection;
using System.Runtime.CompilerServices;
using Moq;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Teamwork.Tests;

/// <summary>
/// Builds an <see cref="ActionContext"/> for unit tests. <see cref="ConfiguredConnection"/> has an
/// internal constructor, so a connection is materialised uninitialised and only its <c>Settings</c>
/// backing field is set — which is all <c>context.Connection.GetSettings&lt;T&gt;()</c> reads.
/// </summary>
public static class ActionTestHarness
{
    public static ActionInfrastructure Infrastructure => new(Mock.Of<IEditableModelResolver>());

    public static ActionContext Context(object settings, object? connectionSettings = null)
    {
        ConfiguredConnection? connection = null;
        if (connectionSettings is not null)
        {
            connection = (ConfiguredConnection)RuntimeHelpers.GetUninitializedObject(typeof(ConfiguredConnection));
            var field = typeof(ConfiguredConnection)
                .GetField("<Settings>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(connection, connectionSettings);
        }

        return new ActionContext
        {
            AutomationId = Guid.NewGuid(),
            RunId = Guid.NewGuid(),
            StepId = Guid.NewGuid(),
            ActionAlias = "test",
            Settings = settings,
            Connection = connection,
        };
    }
}
