#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Inedo.Extensibility.IssueTrackers;
using Inedo.Extensions.GitHub.Clients;

namespace Inedo.Extensions.GitHub.IssueTrackers;

public sealed class GitHubIssueTrackerService : IssueTrackerService<GitHubIssueTrackerProject, GitHubAccount>
{
    public override string DefaultVersionFieldName => "Milestone";
    public override string? NamespaceDisplayName => "Group";
    public override string ServiceName => "GitHub";
    public override bool HasDefaultApiUrl => true;
    public override string PasswordDisplayName => "Personal access token";


    protected override IAsyncEnumerable<string> GetNamespacesAsync(GitHubAccount credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var client = new GitHubClient(credentials.ServiceUrl, credentials.UserName, credentials.Password, null);
        return client.GetOrganizationsAsync(cancellationToken);
    }
    protected override IAsyncEnumerable<string> GetProjectNamesAsync(GitHubAccount credentials, string? serviceNamespace = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(serviceNamespace);
        var client = new GitHubClient(credentials.ServiceUrl, credentials.UserName, credentials.Password);
        return serviceNamespace == null || string.Equals(credentials.UserName, serviceNamespace, StringComparison.OrdinalIgnoreCase)
            ? client.GetUserRepositoriesAsync(credentials.UserName, cancellationToken)
            : client.GetOrgRepositoriesAsync(serviceNamespace, cancellationToken);
    }
}
