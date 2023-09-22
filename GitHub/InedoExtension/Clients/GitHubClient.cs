using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Inedo.Diagnostics;
using Inedo.ExecutionEngine.Executer;
using Inedo.Extensibility.Git;
using Inedo.Extensions.GitHub.IssueSources;

#nullable enable

namespace Inedo.Extensions.GitHub.Clients;

internal sealed class GitHubClient : ILogSink
{
    public const string GitHubComUrl = "https://api.github.com";
    private static readonly LazyRegex NextPageLinkPattern = new("<(?<uri>[^>]+)>; rel=\"next\"", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] EnabledPreviews = new[]
    {
        "application/vnd.github.inertia-preview+json", // projects
    };
    private readonly string apiBaseUrl;
    private readonly ILogSink? log;
    private readonly HttpClient httpClient;

    void ILogSink.Log(IMessage message) => this.log?.Log(message);

    public GitHubClient(string? apiBaseUrl, string userName, SecureString password, ILogSink? log = null)
    {
        if (!string.IsNullOrEmpty(userName) && password == null)
            throw new InvalidOperationException("If a username is specified, a password must be specified in the operation or in the resource credential.");

        this.apiBaseUrl = AH.CoalesceString(apiBaseUrl, GitHubComUrl).TrimEnd('/');
        this.UserName = userName;
        this.Password = password;
        this.log = log;
        this.httpClient = CreateHttpClient(this.apiBaseUrl, AH.Unprotect(password));
    }
    public GitHubClient(GitHubAccount credentials, GitHubRepository resource, ILogSink? log = null)
    {
        this.apiBaseUrl = AH.CoalesceString(resource?.LegacyApiUrl, GitHubComUrl).TrimEnd('/');
        this.UserName = credentials?.UserName;
        this.Password = credentials?.Password;
        this.log = log;
        this.httpClient = CreateHttpClient(this.apiBaseUrl, AH.Unprotect(credentials?.Password));
    }

    public string? UserName { get; }
    public SecureString? Password { get; }

    public IAsyncEnumerable<string> GetOrganizationsAsync(CancellationToken cancellationToken)
    {
        return this.InvokePagesAsync(
            $"{this.apiBaseUrl}/user/orgs?per_page=100",
            d => SelectString(d, "login"),
            cancellationToken
        );
    }
    public IAsyncEnumerable<string> GetOrgRepositoriesAsync(string organizationName, CancellationToken cancellationToken)
    {
        return this.InvokePagesAsync(
            $"{this.apiBaseUrl}/orgs/{Esc(organizationName)}/repos?per_page=100",
            d => SelectString(d, "name"),
            cancellationToken
        );
    }
    public IAsyncEnumerable<string> GetUserRepositoriesAsync(string username, CancellationToken cancellationToken)
    {
        return this.InvokePagesAsync(
            $"{this.apiBaseUrl}/users/{Esc(username)}/repos?per_page=100",
            d => SelectString(d, "name"),
            cancellationToken
        );
    }
    public async Task<GitHubRepositoryInfo> GetRepositoryAsync(GitHubProjectId project, CancellationToken cancellationToken = default)
    {
        var url = $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}";

        using var doc = await this.InvokeAsync(HttpMethod.Get, url, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc == null)
            throw new InvalidOperationException($"Repository {project} not found.");
        var obj = doc.RootElement;

        return new GitHubRepositoryInfo
        {
            RepositoryUrl = obj.GetProperty("clone_url").GetString(),
            BrowseUrl = obj.GetProperty("html_url").GetString(),
            DefaultBranch = obj.GetProperty("default_branch").GetString()
        };
    }
    public IAsyncEnumerable<GitRemoteBranch> GetBranchesAsync(GitHubProjectId project, CancellationToken cancellationToken = default)
    {
        var url = $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/branches?per_page=100";

        return this.InvokePagesAsync(url, selectBranches, cancellationToken);

        static IEnumerable<GitRemoteBranch> selectBranches(JsonDocument d)
        {
            foreach (var e in d.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    continue;

                var name = nameProp.GetString()!;

                if (!e.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.Object || !commit.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String)
                    continue;

                if (!GitObjectId.TryParse(sha.GetString(), out var hash))
                    continue;

                bool isProtected = e.TryGetProperty("protected", out var p) && p.ValueKind == JsonValueKind.True;

                yield return new GitRemoteBranch(hash, name, isProtected);
            }
        }
    }
    public IAsyncEnumerable<GitPullRequest> GetPullRequestsAsync(GitHubProjectId project, bool includeClosed, CancellationToken cancellationToken = default)
    {
        var url = $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/pulls?per_page=100";

        url = $"{url}&state={(includeClosed ? "all" : "open")}";

        return this.InvokePagesAsync(url, selectPullRequests, cancellationToken);

        static IEnumerable<GitPullRequest> selectPullRequests(JsonDocument d)
        {
            foreach (var pr in d.RootElement.EnumerateArray())
            {
                if (!tryGetId(pr, "head", out long sourceRepoId) || !tryGetId(pr, "base", out long targetRepoId))
                    continue;

                // skip requests from other repositories for now
                if (sourceRepoId != targetRepoId)
                    continue;

                var url = pr.GetProperty("url").GetString();
                var id = pr.GetProperty("id").ToString();
                bool closed = pr.GetProperty("state").ValueEquals("closed");
                var title = pr.GetProperty("title").GetString();
                var from = pr.GetProperty("head").GetProperty("ref").GetString();
                var to = pr.GetProperty("base").GetProperty("ref").GetString();

                yield return new GitPullRequest(id, url, title, closed, from, to);
            }

            static bool tryGetId(JsonElement element, string name, out long id)
            {
                if (element.TryGetProperty(name, out var root) && root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("repo", out var repo) && repo.ValueKind == JsonValueKind.Object)
                    {
                        if (repo.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.Number)
                        {
                            id = idProperty.GetInt64();
                            return true;
                        }
                    }
                }

                id = 0;
                return false;
            }
        }
    }
    public async Task MergePullRequestAsync(GitHubProjectId project, int id, string headCommit, string message, string method, CancellationToken cancellationToken = default)
    {
        var url = $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/pulls/";

        url += $"{id}/merge";

        using var doc = await this.InvokeAsync(
            HttpMethod.Post,
            url,
            new
            {
                commit_title = message,
                merge_method = method,
                sha = headCommit
            },
            cancellationToken: cancellationToken
        );
    }
    public async Task<int> CreatePullRequestAsync(GitHubProjectId project, string source, string target, string title, string description, CancellationToken cancellationToken = default)
    {
        var url = $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/pulls";

        using var doc = await this.InvokeAsync(
            HttpMethod.Post,
            url,
            new
            {
                title,
                body = description,
                head = source,
                @base = target
            },
            cancellationToken: cancellationToken
        );

        if (doc == null)
            throw new ArgumentException("Pull request not found.");

        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public async Task SetCommitStatusAsync(GitHubProjectId project, string commit, string status, string description, string context, CancellationToken cancellationToken)
    {
        var url = $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/statuses/{Uri.EscapeDataString(commit)}";

        using var doc = await this.InvokeAsync(
            HttpMethod.Post,
            url,
            new
            {
                state = status,
                description,
                context
            },
            cancellationToken: cancellationToken
        );
    }

    public IAsyncEnumerable<GitHubIssue> GetIssuesAsync(GitHubProjectId project, GitHubIssueFilter filter, CancellationToken cancellationToken)
    {
        return this.InvokePagesAsync(
            $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/issues{filter.ToQueryString()}",
            getIssues,
            cancellationToken
        );

        static IEnumerable<GitHubIssue> getIssues(JsonDocument doc)
        {
            foreach (var obj in doc.RootElement.EnumerateArray())
                yield return new GitHubIssue(obj);
        }
    }
    public async Task<GitHubIssue> GetIssueAsync(string issueUrl, string? statusOverride = null, bool? closedOverride = null, CancellationToken cancellationToken = default)
    {
        using var doc = await this.InvokeAsync(HttpMethod.Get, issueUrl, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc == null)
            throw new ArgumentException("Issue not found: " + issueUrl);
        return new GitHubIssue(doc.RootElement, statusOverride, closedOverride);
    }
    public async Task<int> CreateIssueAsync(GitHubProjectId project, object data, CancellationToken cancellationToken)
    {
        using var doc = await this.InvokeAsync(
            HttpMethod.Post,
            $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/issues",
            data,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        if (doc == null)
            throw new ArgumentException("Project not found: " + project);

        return doc.RootElement.GetProperty("number").GetInt32();
    }
    public async Task UpdateIssueAsync(int issueId, GitHubProjectId project, object update, CancellationToken cancellationToken)
    {
        using var doc = await this.InvokeAsync(
            HttpMethod.Patch,
            $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/issues/{issueId}",
            update,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<GitHubMilestone> CreateMilestoneAsync(string milestoneTitle, GitHubProjectId project, CancellationToken cancellationToken)
    {
        var milestone = await this.FindMilestoneAsync(milestoneTitle, project, cancellationToken).ConfigureAwait(false);
        if (milestone != null)
            return milestone;


        using var doc = await this.InvokeAsync(
            HttpMethod.Post,
            $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/milestones",
            new { title = milestone },
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        if (doc == null)
            throw new ArgumentException("Project not found: " + project);

        return doc.Deserialize<GitHubMilestone>()
            ?? throw new InvalidOperationException("Milestone was empty");
    }
    public async Task UpdateMilestoneAsync(GitHubMilestone milestone, GitHubProjectId project, object data, CancellationToken cancellationToken)
    {
        using var doc = await this.InvokeAsync(
            HttpMethod.Patch,
            $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/milestones/{milestone.Number}",
            data,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task CreateStatusAsync(string ownerName, string repositoryName, string commitHash, string state, string target_url, string description, string context, CancellationToken cancellationToken)
    {
        using var doc = await this.InvokeAsync(
            HttpMethod.Post,
            $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/statuses/{Esc(commitHash)}",
            new { state, target_url, description, context },
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task CreateCommentAsync(int issueId, string ownerName, string repositoryName, string commentText, CancellationToken cancellationToken)
    {
        using var doc = await this.InvokeAsync(
            HttpMethod.Post,
            $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/issues/{issueId}/comments",
            new { body = commentText },
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<GitHubMilestone?> FindMilestoneAsync(string title, GitHubProjectId projectId, CancellationToken cancellationToken)
    {
        await foreach (var m in this.GetMilestonesAsync(projectId, "all", cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return null;
    }
    public IAsyncEnumerable<GitHubMilestone> GetMilestonesAsync(GitHubProjectId project, string? state, CancellationToken cancellationToken)
    {
        return this.InvokePagesAsync(
            $"{this.apiBaseUrl}/repos/{Esc(project.OrganizationName)}/{Esc(project.RepositoryName)}/milestones?state={Uri.EscapeDataString(state ?? "all")}&sort=due_on&direction=desc&per_page=100",
            d => d.Deserialize<IEnumerable<GitHubMilestone>>()!,
            cancellationToken
        );
    }

    public IAsyncEnumerable<GitHubProject> GetProjectsAsync(string ownerName, string repositoryName, CancellationToken cancellationToken)
    {
        var url = $"{this.apiBaseUrl}/orgs/{Esc(ownerName)}/projects?state=all";
        if (!string.IsNullOrEmpty(repositoryName))
            url = $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/projects?state=all";

        return this.InvokePagesAsync(
            url,
            d => d.Deserialize<IEnumerable<GitHubProject>>()!,
            cancellationToken
        );
    }

    public async IAsyncEnumerable<ProjectColumnData> GetProjectColumnsAsync(string projectColumnsUrl, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var (cardsUrl, name) in this.InvokePagesAsync(projectColumnsUrl, getColumns, cancellationToken).ConfigureAwait(false))
        {
            var issueUrls = new List<string>();

            await foreach (var issueUrl in this.InvokePagesAsync(cardsUrl, getIssueUrls, cancellationToken).ConfigureAwait(false))
                issueUrls.Add(issueUrl);

            yield return new ProjectColumnData(name, issueUrls);
        }

        IEnumerable<(string cardsUrl, string name)> getColumns(JsonDocument doc)
        {
            foreach (var obj in doc.RootElement.EnumerateArray())
            {
                var (url, name) = (obj.GetProperty("cards_url").GetString(), obj.GetProperty("name").GetString());
                if (url == null || name == null)
                {
                    this.LogDebug($"Could not parse Columns: {obj}");
                    continue;
                }
                yield return (url, name);
            }
        }

         static IEnumerable<string> getIssueUrls(JsonDocument doc)
        {
            foreach (var obj in doc.RootElement.EnumerateArray())
            {
                if (obj.TryGetProperty("content_url", out var value))
                    yield return value.GetString()!;
            }
        }
    }

    public async Task<GitHubRelease?> GetReleaseAsync(string ownerName, string repositoryName, string tag, CancellationToken cancellationToken)
    {
        using var doc = await this.InvokeAsync(
            HttpMethod.Get,
            $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/releases/tags/{Esc(tag)}",
            nullOn404: true,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        return doc != null ? JsonSerializer.Deserialize<GitHubRelease>(doc) : null;
    }

    public IAsyncEnumerable<string> ListRefsAsync(string ownerName, string repositoryName, RefType? type, CancellationToken cancellationToken = default)
    {
        var prefix = type switch
        {
            RefType.Branch => "refs/heads",
            RefType.Tag => "refs/tags",
            _ => "refs"
        };

        return this.InvokePagesAsync(
            $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/git/{prefix}",
            getRefs,
            cancellationToken
        );

        IEnumerable<string> getRefs(JsonDocument doc)
        {
            foreach (var obj in doc.RootElement.EnumerateArray())
            {
                var s = obj.GetProperty("ref").GetString()!;

                if (s.StartsWith(prefix))
                    s = s[prefix.Length..];

                if (s.StartsWith("/"))
                    s = s[1..];

                yield return s;
            }
        }
    }

    public async Task<GitHubRelease> EnsureReleaseAsync(string ownerName, string repositoryName, string tag, string target, string name, string body, bool? draft, bool? prerelease, CancellationToken cancellationToken)
    {
        var release = new GitHubRelease
        {
            Tag = tag,
            Target = target,
            Title = name,
            Description = body,
            Draft = draft,
            Prerelease = prerelease
        };

        var existingRelease = await this.GetReleaseAsync(ownerName, repositoryName, tag, cancellationToken).ConfigureAwait(false);

        using var doc = existingRelease != null
            ? await this.InvokeAsync(HttpMethod.Patch, $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/releases/{existingRelease.Id}", release, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await this.InvokeAsync(HttpMethod.Post, $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/releases", release, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        if (doc == null)
            throw new ExecutionFailureException($"Unexpected release return.");

        return doc.Deserialize<GitHubRelease>()
            ?? throw new InvalidOperationException("Unable to deserialize document: ");
    }

    public async Task UploadReleaseAssetAsync(string ownerName, string repositoryName, string tag, string name, string contentType, Stream contents, CancellationToken cancellationToken)
    {
        var release = (await this.GetReleaseAsync(ownerName, repositoryName, tag, cancellationToken).ConfigureAwait(false))
            ?? throw new ExecutionFailureException($"No release found with tag {tag} in repository {ownerName}/{repositoryName}");

        if (release.Assets?.Select(a => a.Name).Contains(name) ?? false)
        {
            this.LogError($"Release {tag} already has an asset named {name}.");
            return;
        }

        var uploadUrl = FormatTemplateUri(release.UploadUrl, name);

        using var content = new StreamContent(contents);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await this.httpClient.PostAsync(uploadUrl, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await GetErrorResponseExceptionAsync(response).ConfigureAwait(false);
    }

    private static string FormatTemplateUri(string templateUri, string name)
    {
        // quick hack for URI templates since former NuGet package doesn't support target framework v4.5.2 
        // The format of templatedUploadUri is: https://host/repos/org/repoName/releases/1000/assets{?name,label}

        int index = templateUri.IndexOf('{');
        return templateUri[..index] + "?name=" + Uri.EscapeDataString(name);
    }
    private static string Esc(string part) => Uri.EscapeUriString(part ?? string.Empty);
    private static async Task<Exception> GetErrorResponseExceptionAsync(HttpResponseMessage response)
    {
        var errorMessage = $"Server replied with {(int)response.StatusCode}";

        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("application/json") == true)
        {
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            string? parsedMessage = null;

            if (doc.RootElement.TryGetProperty("message", out var message))
                parsedMessage = message.GetString();

            if (!string.IsNullOrWhiteSpace(parsedMessage))
                errorMessage += ": " + parsedMessage;

            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var moreDetails = new List<string>();

                foreach (var d in errors.EnumerateArray())
                    moreDetails.Add($"{GetStringOrDefault(d, "resource")} {GetStringOrDefault(d, "code")}".Trim());

                if(moreDetails.Count > 0)
                    errorMessage += $" ({string.Join(", ", moreDetails)})";
            }
        }
        else
        {
            var details = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(details))
                errorMessage += ": " + details;
        }

        return new ExecutionFailureException(errorMessage);
    }
    private async Task<JsonDocument?> InvokeAsync(HttpMethod method, string url, object? data = null, bool nullOn404 = false, CancellationToken cancellationToken = default)
    {
        this.log?.LogDebug($"{method} {url}");
        using var request = new HttpRequestMessage(method, url);

        if (data != null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data, data.GetType(), new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("content/json");
            request.Content = content;
        }

        using var response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (nullOn404 && response.StatusCode == HttpStatusCode.NotFound)
                return null;

            throw await GetErrorResponseExceptionAsync(response).ConfigureAwait(false);
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    private async IAsyncEnumerable<T> InvokePagesAsync<T>(string url, Func<JsonDocument, IEnumerable<T>> getItems, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var currentUrl = url;

        while (currentUrl != null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);

            using var response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await GetErrorResponseExceptionAsync(response).ConfigureAwait(false);

            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var item in getItems(doc))
            {
                if (item != null)
                    yield return item;
            }
            currentUrl = null;
            if (response.Headers.TryGetValues("Link", out var links))
            {
                foreach (var link in links)
                {
                    var m = NextPageLinkPattern.Match(link);
                    if (m.Success)
                    {
                        currentUrl = m.Groups["uri"].Value;
                        break;
                    }
                }
            }
        }
    }
    private static IEnumerable<string> SelectString(JsonDocument doc, string name)
    {
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in doc.RootElement.EnumerateArray())
            {
                if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var path) && path.ValueKind == JsonValueKind.String)
                    yield return path.GetString()!;
            }
        }
    }
    private static string? GetStringOrDefault(in JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var value))
            return value.GetString();
        else
            return null;
    }
    private static HttpClient CreateHttpClient(string baseUrl, string? password)
    {
        var http = SDK.CreateHttpClient();
        http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : (baseUrl + "/"));
        if (!string.IsNullOrEmpty(password))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", password);

        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        foreach (var preview in EnabledPreviews)
            http.DefaultRequestHeaders.Accept.ParseAdd(preview);

        return http;
    }
}
