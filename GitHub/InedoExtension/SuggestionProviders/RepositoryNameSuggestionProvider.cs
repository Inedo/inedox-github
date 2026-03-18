using System;
using System.Collections.Generic;
using System.Threading;

namespace Inedo.Extensions.GitHub.SuggestionProviders;

internal sealed class RepositoryNameSuggestionProvider : GitHubSuggestionProvider
{
    internal override IAsyncEnumerable<string> GetSuggestionsAsync(CancellationToken cancellationToken) => 
        string.Equals(this.ComponentConfiguration[nameof(IGitHubConfiguration.OrganizationName)], this.Credentials.UserName, StringComparison.OrdinalIgnoreCase)
            ? this.Client.GetUserRepositoriesAsync(this.Credentials.UserName, cancellationToken)
            : this.Client.GetOrgRepositoriesAsync(this.ComponentConfiguration[nameof(IGitHubConfiguration.OrganizationName)], cancellationToken)
    ;
}
