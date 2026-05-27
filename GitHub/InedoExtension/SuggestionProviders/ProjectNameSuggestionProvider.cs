using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Inedo.Extensions.GitHub.IssueSources;

namespace Inedo.Extensions.GitHub.SuggestionProviders;

internal sealed class ProjectNameSuggestionProvider : GitHubSuggestionProvider
{
    internal override IAsyncEnumerable<string> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        var repositoryName = AH.NullIf(this.ComponentConfiguration[nameof(GitHubProjectIssueSource.RepositoryName)], string.Empty);
        return this.Client.GetProjectsAsync(this.Resource.OrganizationName, repositoryName, cancellationToken).Select(p => p.Name);
    }
}
