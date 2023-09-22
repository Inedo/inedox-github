using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Configurations;
using Inedo.Extensibility.Operations;
using Inedo.Extensions.GitHub.Clients;
using Inedo.Extensions.GitHub.Configurations;

namespace Inedo.Extensions.GitHub.Operations.Issues
{
    [Description("Ensures a GitHub milestone exists with the specified properties.")]
    [ScriptAlias("Ensure-Milestone")]
    [ScriptNamespace("GitHub", PreferUnqualified = false)]
    public sealed class GitHubEnsureMilestoneOperation : EnsureOperation<GitHubMilestoneConfiguration>
    {
        public override async Task<PersistedConfiguration> CollectAsync(IOperationCollectionContext context)
        {
            var (credentials, resource) = this.Template.GetCredentialsAndResource(context);
            var github = new GitHubClient(credentials, resource, this);

            GitHubMilestone milestone = null;
            await foreach (var m in github.GetMilestonesAsync(new GitHubProjectId(AH.CoalesceString(resource.OrganizationName, credentials.UserName), resource.RepositoryName), null, context.CancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(m.Title, this.Template.Title, StringComparison.OrdinalIgnoreCase))
                {
                    milestone = m;
                    break;
                }
            }

            if (milestone == null)
                return new GitHubMilestoneConfiguration { Exists = false };

            return new GitHubMilestoneConfiguration
            {
                Exists = true,
                Title = milestone?.Title ?? string.Empty,
                Description = milestone?.Description ?? string.Empty,
                DueDate = milestone?.DueOn,
                State = milestone?.State
            };
        }

        public override async Task ConfigureAsync(IOperationExecutionContext context)
        {
            var (credentials, resource) = this.Template.GetCredentialsAndResource(context);
            var github = new GitHubClient(credentials, resource, this);

            var project = new GitHubProjectId(AH.CoalesceString(resource.OrganizationName, credentials.UserName), resource.RepositoryName);

            var milestone = await github.CreateMilestoneAsync(this.Template.Title, this.Template, project, context.CancellationToken).ConfigureAwait(false);

            bool update = false;

            if (this.Template.DueOn != null && milestone.DueOn != this.Template.DueOn)
            {
                milestone.DueOn = this.Template.DueOn;
                update = true;
            }

            if (this.Template.Description != null && milestone.Description != this.Template.Description)
            {
                milestone.Description = this.Template.Description;
                update = true;
            }

            if (this.Template.State != null && milestone.State != this.Template.State)
            {
                milestone.State = this.Template.State;
                update = true;
            }

            if (update)
                await github.UpdateMilestoneAsync(milestone, new GitHubProjectId(AH.CoalesceString(resource.OrganizationName, credentials.UserName), resource.RepositoryName), context.CancellationToken).ConfigureAwait(false);
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            return new ExtendedRichDescription(
                new RichDescription("Ensure milestone ", new Hilite(config[nameof(GitHubMilestoneConfiguration.Title)])),
                new RichDescription("in ", new Hilite(config.DescribeSource()))
            );
        }
    }
}
