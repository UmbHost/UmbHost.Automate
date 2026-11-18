using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Teams.Connection;

/// <summary>
/// Settings for a Teams connection: the address of a Workflows webhook. The address is a secret,
/// because anyone holding it can post to the channel or chat.
/// </summary>
public sealed class TeamsConnectionSettings
{
    [Field(Label = "Workflow address",
        Description = "The address of a Teams Workflow made from \"Post to a channel when a webhook request is received\" (or the chat version). Keep it secret: anyone with it can post. Pressing Test posts a test card.",
        IsSensitive = true,
        SortOrder = 0)]
    public string WebhookUrl { get; set; } = "$Umbraco:Automate:Secrets:TeamsWebhookUrl";
}
