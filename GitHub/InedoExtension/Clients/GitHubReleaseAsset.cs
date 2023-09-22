using System.Text.Json.Serialization;

namespace Inedo.Extensions.GitHub.Clients
{
    internal sealed class GitHubReleaseAsset
    {
        [JsonConstructor]
        public GitHubReleaseAsset(string name) => this.Name = name;

        [JsonPropertyName("name")]
        public string Name { get; }
    }
}
