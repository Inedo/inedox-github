using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Inedo.Documentation;
using Inedo.Extensibility.Credentials;
using Inedo.Extensibility.IssueSources;
using Inedo.Extensibility.SecureResources;
using Inedo.Extensions.GitHub.Clients;
using Inedo.Extensions.GitHub.SuggestionProviders;
using Inedo.Serialization;
using Inedo.Web;

namespace Inedo.Extensions.GitHub.IssueSources
{
    [DisplayName("[Obsolete] GitHub Issue Source")]
    [Description("Legacy issue source; replace with GitHub Issue Tracker.")]
    public sealed class GitHubIssueSource : IssueSource<GitHubRepository>, IMissingPersistentPropertyHandler
    {
        [Persistent]
        [DisplayName("Repository name")]
        [PlaceholderText("Use repository name from credentials")]
        [SuggestableValue(typeof(RepositoryNameSuggestionProvider))]
        public string RepositoryName { get; set; }

        [Persistent]
        [DisplayName("Milestone")]
        [SuggestableValue(typeof(MilestoneSuggestionProvider))]
        public string MilestoneTitle { get; set; }

        [Persistent]
        [DisplayName("Labels")]
        [PlaceholderText("Any")]
        [Description("A list of comma separated label names. Example: bug,ui,@high")]
        public string Labels { get; set; }

        [Persistent]
        [FieldEditMode(FieldEditMode.Multiline)]
        [DisplayName("Custom filter query")]
        [PlaceholderText("Use above fields")]
        [Description("If a custom filter query string is set, the above filters are ignored. See "
            + "<a href=\"https://developer.github.com/v3/issues/#list-issues-for-a-repository\" target=\"_blank\">GitHub API List Issues for a Repository</a> "
            + "for more information.<br /><br />" 
            + "For example, to filter by all issues assigned to 'BuildMasterUser' without a set milestone:<br /><br />" 
            + "<pre>milestone=none&amp;assignee=BuildMasterUser&amp;state=all</pre>")]
        public string CustomFilterQueryString { get; set; }

        void IMissingPersistentPropertyHandler.OnDeserializedMissingProperties(IReadOnlyDictionary<string, string> missingProperties)
        {
            if (string.IsNullOrEmpty(this.ResourceName) && missingProperties.TryGetValue("CredentialName", out var value))
                this.ResourceName = value;
        }

        public override async IAsyncEnumerable<IIssueTrackerIssue> EnumerateIssuesAsync(IIssueSourceEnumerationContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var resource = SecureResource.TryCreate(SecureResourceType.GitRepository, this.ResourceName, new ResourceResolutionContext(context.ProjectId)) as GitHubRepository;
            var credentials = resource?.GetCredentials(new CredentialResolutionContext(context.ProjectId, null)) as GitHubAccount;
            if (resource == null)
                throw new InvalidOperationException($"A resource must be supplied to enumerate GitHub issues.");

            string repositoryName = AH.CoalesceString(this.RepositoryName, resource.RepositoryName);
            if (string.IsNullOrEmpty(repositoryName))
                throw new InvalidOperationException("A repository name must be defined in either the issue source or associated GitHub credentials in order to enumerate GitHub issues.");

            var client = new GitHubClient(credentials, resource, context.Log);

            string ownerName = AH.CoalesceString(resource.OrganizationName, credentials?.UserName);
            if (string.IsNullOrEmpty(ownerName))
                throw new InvalidOperationException("A organization or user name must be defined to enumerate GitHub issues.");

            var projectId = new GitHubProjectId(ownerName, repositoryName);

            GitHubIssueFilter filter;
            if (string.IsNullOrEmpty(this.CustomFilterQueryString))
            {
                filter = new GitHubIssueFilter(this.CustomFilterQueryString);
            }
            else
            {
                string milestoneTitle;
                if (!string.IsNullOrEmpty(this.MilestoneTitle))
                {
                    var m = await client.FindMilestoneAsync(this.MilestoneTitle, projectId, cancellationToken).ConfigureAwait(false);
                    if (m == null)
                        throw new InvalidOperationException($"Milestone '{this.MilestoneTitle}' not found in repository '{repositoryName}' owned by '{ownerName}'.");

                    milestoneTitle = m.Number.ToString();
                }
                else
                {
                    milestoneTitle = "*";
                }
                filter = new GitHubIssueFilter(milestoneTitle, this.Labels);

            }

            await foreach (var i in client.GetIssuesAsync(projectId, filter, cancellationToken).ConfigureAwait(false))
                yield return i;
        }

        public override RichDescription GetDescription()
        {
            return new RichDescription("Get Issues from GitHub");
        }
    }
}
