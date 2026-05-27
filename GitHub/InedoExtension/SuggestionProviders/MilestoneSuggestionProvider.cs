using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Inedo.Extensions.GitHub.SuggestionProviders;

internal sealed class MilestoneSuggestionProvider : GitHubSuggestionProvider
{
    internal override IAsyncEnumerable<string> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        string repositoryName = AH.CoalesceString(this.ComponentConfiguration[nameof(IGitHubConfiguration.RepositoryName)], this.Resource?.RepositoryName);
        string ownerName = AH.CoalesceString(
            this.ComponentConfiguration[nameof(IGitHubConfiguration.OrganizationName)], this.Resource?.OrganizationName,
            this.ComponentConfiguration[nameof(IGitHubConfiguration.UserName)], this.Credentials?.UserName
            );
        if (string.IsNullOrEmpty(ownerName) || string.IsNullOrEmpty(repositoryName))
            return AsyncEnumerable.Empty<string>();

        return this.Client.GetMilestonesAsync(new GitHubProjectId(ownerName, repositoryName), "open", cancellationToken)
            .Select(m => m.Title);
    }
}
