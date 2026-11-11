namespace UmbHost.Automate.Meta.Configuration;

/// <summary>
/// The Facebook and Instagram insights switch, <c>Umbraco:Automate:Providers:Meta:Insights</c> (bool, default false). Both networks'
/// metrics go through the same Meta App Review, so one switch covers both.
/// </summary>
internal static class MetaInsights
{
    public const string Setting = "Umbraco:Automate:Providers:Meta:Insights";
}
