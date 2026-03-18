using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Inedo.Extensions.GitHub.SuggestionProviders;

internal sealed class OrganizationNameSuggestionProvider : GitHubSuggestionProvider
{
    internal override IAsyncEnumerable<string> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        if (this.Credentials == null)
            return AsyncEnumerable.Empty<string>();

        return this.Client.GetOrganizationsAsync(cancellationToken);
    }
}
