using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Inedo.Extensibility.Git;
using Inedo.Extensions.GitHub.Clients;

namespace Inedo.Extensions.GitHub
{
    [DisplayName("GitHub")]
    [Description("Provides integration for hosted GitHub repositories.")]
    public sealed class GitHubServiceInfo : GitService<GitHubRepository, GitHubAccount>
    {
        public override string ServiceName => "GitHub";
        public override bool HasDefaultApiUrl => true;
        public override string PasswordDisplayName => "Personal access token";

        protected override async IAsyncEnumerable<string> GetNamespacesAsync(GitHubAccount credentials, [EnumeratorCancellation]CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(credentials);
            var client = new GitHubClient(credentials.ServiceUrl, credentials.UserName, credentials.Password, this);
            
            await foreach (var org in client.GetOrganizationsAsync(cancellationToken))
                yield return org;
            yield return credentials.UserName;
        }
        protected override IAsyncEnumerable<string> GetRepositoryNamesAsync(GitHubAccount credentials, string serviceNamespace, CancellationToken cancellationToken = default)
        {
            var client = new GitHubClient(credentials.ServiceUrl, credentials.UserName, credentials.Password, this);
            return serviceNamespace == null || string.Equals(credentials.UserName, serviceNamespace, StringComparison.OrdinalIgnoreCase)
                ? client.GetUserRepositoriesAsync(credentials.UserName, cancellationToken)
                : client.GetOrgRepositoriesAsync(serviceNamespace, cancellationToken);
        }
    }
}
