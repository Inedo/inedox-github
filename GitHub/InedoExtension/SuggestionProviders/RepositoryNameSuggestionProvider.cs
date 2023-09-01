using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Inedo.Extensions.GitHub.SuggestionProviders
{
    public sealed class RepositoryNameSuggestionProvider : GitHubSuggestionProvider
    {
        internal override Task<IEnumerable<string>> GetSuggestionsAsync() => MakeAsync(
            string.Equals(this.ComponentConfiguration[nameof(IGitHubConfiguration.OrganizationName)], this.Credentials.UserName, System.StringComparison.OrdinalIgnoreCase)
                ? this.Client.GetUserRepositoriesAsync(this.Credentials.UserName, CancellationToken.None)
                : this.Client.GetOrgRepositoriesAsync(this.ComponentConfiguration[nameof(IGitHubConfiguration.OrganizationName)], CancellationToken.None)
        );
    }
}
