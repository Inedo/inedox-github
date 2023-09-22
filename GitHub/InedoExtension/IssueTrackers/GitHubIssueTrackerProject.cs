using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Inedo.Documentation;
using Inedo.ExecutionEngine.Variables;
using Inedo.Extensibility.Credentials;
using Inedo.Extensibility.IssueTrackers;
using Inedo.Extensions.GitHub.Clients;
using Inedo.Serialization;
using Inedo.Web;

#nullable enable

namespace Inedo.Extensions.GitHub.IssueTrackers;

[DisplayName("GitHub Issue Tracker")]
[Description("Work with issues on a GitHub Repository")]
public sealed  class GitHubIssueTrackerProject : IssueTrackerProject<GitHubAccount>
{
    [Persistent]
    [DisplayName("Labels")]
    [PlaceholderText("Any")]
    [Description("A list of comma separated label names. Example: bug,ui,@high, $ReleaseNumber")]
    public string? Labels { get; set; }

    [Persistent]
    [FieldEditMode(FieldEditMode.Multiline)]
    [DisplayName("Custom filter query")]
    [PlaceholderText("Use above fields")]
    [Description("If a custom filter query string is set, the above filters are ignored. See "
        + "<a href=\"https://developer.github.com/v3/issues/#list-issues-for-a-repository\" target=\"_blank\">GitHub API List Issues for a Repository</a> "
        + "for more information.<br /><br />"
        + "For example, to filter by all issues assigned to 'BuildMasterUser' without a set milestone:<br /><br />"
        + "<pre>milestone=none&amp;assignee=BuildMasterUser&amp;state=all&amp;milestone=$ReleaseNumber</pre>")]
    public string? CustomFilterQueryString { get; set; }

    private GitHubProjectId ProjectId => new(this.Namespace, this.ProjectName);
    private static readonly HashSet<string> validStates = new (StringComparer.OrdinalIgnoreCase) { "Open", "Closed" };


    public override async Task<IssuesQueryFilter> CreateQueryFilterAsync(IVariableEvaluationContext context)
    {
        if (!string.IsNullOrEmpty(this.CustomFilterQueryString))
        {
            string? query;
            try
            {
                query = (await ProcessedString.Parse(this.CustomFilterQueryString).EvaluateValueAsync(context).ConfigureAwait(false)).AsString();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not parse the Issue mapping query \"{this.CustomFilterQueryString}\": {ex.Message}");
            }

            if (string.IsNullOrEmpty(query))
                throw new InvalidOperationException($"Evaluating custom query expression \"{this.CustomFilterQueryString}\" resulted in an empty string.");
            return new GitHubIssueFilter(query);
        }

        string? milestone;
        try
        {
            milestone = (await ProcessedString.Parse(AH.CoalesceString(this.SimpleVersionMappingExpression, "$ReleaseNumber")).EvaluateValueAsync(context).ConfigureAwait(false)).AsString();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not parse the simple mapping expression \"{this.SimpleVersionMappingExpression}\": {ex.Message}");
        }

        if (string.IsNullOrEmpty(milestone))
            throw new InvalidOperationException($"Evaluating milestone expression \"{this.SimpleVersionMappingExpression}\" resulted in an empty string.");

        var client = this.CreateClient((ICredentialResolutionContext)context);

        var milestoneId = await client.FindMilestoneAsync(milestone, this.ProjectId, CancellationToken.None).ConfigureAwait(false) 
            ?? throw new InvalidOperationException($"Could not find Milestone {milestone} on GitHub.");

        string? labels = null;
        if (!string.IsNullOrEmpty(this.Labels))
        {
            try
            {
                labels = (await ProcessedString.Parse(AH.CoalesceString(this.SimpleVersionMappingExpression, "$ReleaseNumber")).EvaluateValueAsync(context).ConfigureAwait(false)).AsString();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not parse the Labels expression \"{this.Labels}\": {ex.Message}");
            }
        }
        return new GitHubIssueFilter(milestoneId.Number.ToString(), labels);
    }

    public override async Task EnsureVersionAsync(IssueTrackerVersion version, ICredentialResolutionContext context, CancellationToken cancellationToken = default)
    {
        var client = this.CreateClient(context);
        var milestone = await client.FindMilestoneAsync(version.Version, this.ProjectId, cancellationToken).ConfigureAwait(false);
        if (milestone == null)
        {
            await client.CreateMilestoneAsync(version.Version, null, this.ProjectId, cancellationToken).ConfigureAwait(false);
            if (version.IsClosed)
            {
                milestone = await client.FindMilestoneAsync(version.Version, this.ProjectId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Created milestone was not found.");

                milestone.State = "closed";

                await client.UpdateMilestoneAsync(milestone, this.ProjectId, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (version.IsClosed && milestone.State != "closed")
        {
            milestone.State = "closed";

            await client.UpdateMilestoneAsync(milestone, this.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        else if (!version.IsClosed && milestone.State == "closed")
        {
            milestone.State = "open";
            await client.UpdateMilestoneAsync(milestone, this.ProjectId,  cancellationToken).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<IssueTrackerIssue> EnumerateIssuesAsync(IIssuesEnumerationContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var issue in this.CreateClient(context).GetIssuesAsync(this.ProjectId, (GitHubIssueFilter)context.Filter, cancellationToken).ConfigureAwait(false))
            yield return new IssueTrackerIssue(issue.Id, issue.Status, issue.Type, issue.Title, issue.Description, issue.Submitter, issue.SubmittedDate, issue.IsClosed, issue.Url);
    }

    public override async IAsyncEnumerable<IssueTrackerVersion> EnumerateVersionsAsync(ICredentialResolutionContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var milestone in this.CreateClient(context).GetMilestonesAsync(this.ProjectId, null, cancellationToken))
            yield return new IssueTrackerVersion(milestone.Title, milestone.State == "closed");
    }

    public override RichDescription GetDescription() => new($"{this.Namespace}/{this.ProjectName}");

    public override async Task TransitionIssuesAsync(string? fromStatus, string toStatus, string? comment, IIssuesEnumerationContext context, CancellationToken cancellationToken = default)
    {
        if (!validStates.Contains(toStatus))
            throw new ArgumentOutOfRangeException($"GitHub Issue status cannot be set to \"{toStatus}\", only Open or Closed.");
        if (!string.IsNullOrEmpty(fromStatus) && !validStates.Contains(fromStatus))
            throw new ArgumentOutOfRangeException($"GitHub Issue status cannot be to \"{toStatus}\", only Open or Closed.");

        var client = this.CreateClient(context);
        await foreach (var issue in client.GetIssuesAsync(this.ProjectId, (GitHubIssueFilter)context.Filter, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(toStatus, issue.Status, StringComparison.OrdinalIgnoreCase))
                continue;
            if (fromStatus != null && !string.Equals(fromStatus, issue.Status, StringComparison.OrdinalIgnoreCase))
                continue;

            await client.UpdateIssueAsync(int.Parse(issue.Id), this.ProjectId, new { state_event = "close" }, cancellationToken).ConfigureAwait(false);

        }
    }
    private GitHubClient CreateClient(ICredentialResolutionContext context)
    {
        var creds = this.GetCredentials(context) as GitHubAccount
            ?? throw new InvalidOperationException("Credentials are required to query GitHub API.");

        return new GitHubClient(creds.ServiceUrl,creds.UserName, creds.Password, this);
    }

}
