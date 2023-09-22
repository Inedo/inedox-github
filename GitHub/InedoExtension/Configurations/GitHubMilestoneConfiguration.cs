using System.Collections.Generic;
using System.ComponentModel;
using System.Security;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Configurations;
using Inedo.Extensions.GitHub.SuggestionProviders;
using Inedo.Serialization;
using Inedo.Web;

namespace Inedo.Extensions.GitHub.Configurations
{
    [DisplayName("GitHub Milestone")]
    public sealed class GitHubMilestoneConfiguration : PersistedConfiguration, IExistential, IGitHubConfiguration, IMissingPersistentPropertyHandler
    {
        [Persistent]
        [ScriptAlias("From")]
        [ScriptAlias("Credentials")]
        [DisplayName("From GitHub resource")]
        [SuggestableValue(typeof(SecureResourceSuggestionProvider<GitHubRepository>))]
        [IgnoreConfigurationDrift]
        public string ResourceName { get; set; }

        [Persistent]
        [Category("Connection/Identity")]
        [ScriptAlias("UserName")]
        [DisplayName("User name")]
        [PlaceholderText("Use user name from GitHub resource's credentials")]
        [IgnoreConfigurationDrift]
        public string UserName { get; set; }

        [Persistent(Encrypted = true)]
        [Category("Connection/Identity")]
        [ScriptAlias("Password")]
        [DisplayName("Password")]
        [PlaceholderText("Use password from GitHub resource's credentials")]
        [IgnoreConfigurationDrift]
        public SecureString Password { get; set; }

        [Persistent]
        [Category("Connection/Identity")]
        [ScriptAlias("Organization")]
        [DisplayName("Organization name")]
        [PlaceholderText("Use organization from Github resource")]
        [SuggestableValue(typeof(OrganizationNameSuggestionProvider))]
        [IgnoreConfigurationDrift]
        public string OrganizationName { get; set; }

        [Persistent]
        [Category("Connection/Identity")]
        [ScriptAlias("Repository")]
        [DisplayName("Repository name")]
        [PlaceholderText("Use repository from Github resource")]
        [SuggestableValue(typeof(RepositoryNameSuggestionProvider))]
        [IgnoreConfigurationDrift]
        public string RepositoryName { get; set; }

        [Persistent]
        [Category("Connection/Identity")]
        [ScriptAlias("ApiUrl")]
        [DisplayName("API URL")]
        [PlaceholderText("Use URL from Github resource.")]
        [IgnoreConfigurationDrift]
        public string ApiUrl { get; set; }

        [Required]
        [Persistent]
        [ScriptAlias("Title")]
        public string Title { get; set; }

        [Persistent]
        [DisplayName("Due date")]
        [ScriptAlias("DueDate")]
        public string DueDate { get; set; }

        public string DueOn
        {
            get
            {
                var d = this.DueDate;
                if (string.IsNullOrEmpty(d))
                    return null;

                if (d.Contains('T'))
                    return this.DueDate;
                else
                    return $"{this.DueDate}T08:00:00Z";
            }
        }

        [Persistent]
        [ScriptAlias("Description")]
        [FieldEditMode(FieldEditMode.Multiline)]
        public string Description { get; set; }

        [Persistent]
        [ScriptAlias("State")]
        [SuggestableValue("open", "closed")]
        public string State { get; set; }

        [Persistent]
        public bool Exists { get; set; } = true;

        void IMissingPersistentPropertyHandler.OnDeserializedMissingProperties(IReadOnlyDictionary<string, string> missingProperties)
        {
            if (string.IsNullOrEmpty(this.ResourceName) && missingProperties.TryGetValue("CredentialName", out var value))
                this.ResourceName = value;
        }
    }
}
