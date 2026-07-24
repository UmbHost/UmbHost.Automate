using System.Reflection;
using System.Runtime.CompilerServices;
using Moq;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using Umbraco.Automate.OpenIddict.Credentials;

namespace UmbHost.Automate.GitHub.Tests;

/// <summary>
/// Builds an <see cref="ActionContext"/> and a stubbed <see cref="IOAuthCredentialsService"/> for
/// GitHub action tests. <see cref="ConfiguredConnection"/> has an internal constructor, so it is
/// materialised uninitialised and only its <c>Settings</c> backing field is set.
/// </summary>
public static class GitHubActionTestHarness
{
    public static ActionInfrastructure Infrastructure => new(Mock.Of<IEditableModelResolver>());

    public static IOAuthCredentialsService Credentials(string? token) =>
        Mock.Of<IOAuthCredentialsService>(s =>
            s.GetValidAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult(token));

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
