﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Inedo.Extensions.GitHub.Credentials;
using Inedo.IO;

namespace Inedo.Extensions.GitHub.Clients
{
    public enum RefType
    {
        Branch,
        Tag
    }

    internal sealed class GitHubClient
    {
        static GitHubClient()
        {
            // Ensure TLS 1.2 is supported. See https://github.com/blog/2498-weak-cryptographic-standards-removal-notice
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
        }

        private static readonly string[] EnabledPreviews = new[]
        {
            "application/vnd.github.inertia-preview+json", // projects
        };

        public const string GitHubComUrl = "https://api.github.com";

        private string apiBaseUrl;
        private static readonly JavaScriptSerializer jsonSerializer = new JavaScriptSerializer();
        private static string Esc(string part) => Uri.EscapeUriString(part ?? string.Empty);
        private static string Esc(object part) => Esc(part?.ToString());

        public GitHubClient(string apiBaseUrl, string userName, SecureString password, string organizationName)
        {
            if (!string.IsNullOrEmpty(userName) && password == null)
                throw new InvalidOperationException("If a username is specified, a password must be specified in the operation or in the resource credential.");

            this.apiBaseUrl = AH.CoalesceString(apiBaseUrl, GitHubClient.GitHubComUrl).TrimEnd('/');
            this.UserName = userName;
            this.Password = password;
            this.OrganizationName = AH.NullIf(organizationName, string.Empty);
        }

        public GitHubClient(GitHubSecureCredentials credentials, GitHubSecureResource resource)
        {
            this.apiBaseUrl = AH.CoalesceString(resource.ApiUrl, GitHubClient.GitHubComUrl).TrimEnd('/');
            this.UserName = credentials?.UserName;
            this.Password = credentials?.Password;
            this.OrganizationName = AH.NullIf(resource.OrganizationName, string.Empty);
        }

        public string OrganizationName { get; }
        public string UserName { get; }
        public SecureString Password { get; }

        public async Task<IList<Dictionary<string, object>>> GetOrganizationsAsync(CancellationToken cancellationToken)
        {
            var results = await this.InvokePagesAsync("GET", $"{this.apiBaseUrl}/user/orgs?per_page=100", cancellationToken).ConfigureAwait(false);
            return results.Cast<Dictionary<string, object>>().ToList();
        }

        public async Task<IList<Dictionary<string, object>>> GetRepositoriesAsync(CancellationToken cancellationToken)
        {
            string url;
            if (!string.IsNullOrEmpty(this.OrganizationName))
                url = $"{this.apiBaseUrl}/orgs/{Esc(this.OrganizationName)}/repos?per_page=100";
            else
                url = $"{this.apiBaseUrl}/user/repos?per_page=100";

            IEnumerable<object> results;
            try
            {
                results = await this.InvokePagesAsync("GET", url, cancellationToken).ConfigureAwait(false);
            }
            catch when (!string.IsNullOrEmpty(this.OrganizationName))
            {
                url = $"{this.apiBaseUrl}/users/{Esc(this.OrganizationName)}/repos?per_page=100";
                results = await this.InvokePagesAsync("GET", url, cancellationToken).ConfigureAwait(false);
            }
            return results.Cast<Dictionary<string, object>>().ToList();
        }

        public async Task<IList<Dictionary<string, object>>> GetIssuesAsync(string ownerName, string repositoryName, GitHubIssueFilter filter, CancellationToken cancellationToken)
        {
            var issues = await this.InvokePagesAsync("GET", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/issues{filter.ToQueryString()}", cancellationToken).ConfigureAwait(false);
            return issues.Cast<Dictionary<string, object>>().ToList();
        }

        internal async Task<Dictionary<string, object>> GetIssueAsync(string issueUrl, CancellationToken cancellationToken)
        {
            var issue = await this.InvokeAsync("GET", issueUrl, cancellationToken).ConfigureAwait(false);
            return (Dictionary<string, object>)issue;
        }
        public async Task<Dictionary<string, object>> GetIssueAsync(string issueId, string ownerName, string repositoryName, CancellationToken cancellationToken)
        {
            var issue = await this.InvokeAsync("GET", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/issues/{issueId}", cancellationToken).ConfigureAwait(false);
            return (Dictionary<string, object>)issue;
        }
        public async Task<int> CreateIssueAsync(string ownerName, string repositoryName, object data, CancellationToken cancellationToken)
        {
            var issue = (Dictionary<string, object>)await this.InvokeAsync("POST", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/issues", data, cancellationToken).ConfigureAwait(false);
            return (int)issue["number"];
        }
        public Task UpdateIssueAsync(int issueId, string ownerName, string repositoryName, object update, CancellationToken cancellationToken)
        {
            return this.InvokeAsync("PATCH", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/issues/{issueId}", update, cancellationToken);
        }

        public async Task<int> CreateMilestoneAsync(string milestone, string ownerName, string repositoryName, CancellationToken cancellationToken)
        {
            int? milestoneNumber = await this.FindMilestoneAsync(milestone, ownerName, repositoryName, cancellationToken).ConfigureAwait(false);
            if (milestoneNumber.HasValue)
                return milestoneNumber.Value;

            var data = (Dictionary<string, object>)await this.InvokeAsync("POST", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/milestones", new { title = milestone }, cancellationToken).ConfigureAwait(false);
            return (int)data["number"];
        }
        public async Task CloseMilestoneAsync(string milestone, string ownerName, string repositoryName, CancellationToken cancellationToken)
        {
            int? milestoneNumber = await this.FindMilestoneAsync(milestone, ownerName, repositoryName, cancellationToken).ConfigureAwait(false);
            if (milestoneNumber == null)
                return;

            await this.InvokeAsync("PATCH", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/milestones/{milestoneNumber}", new { state = "closed" }, cancellationToken).ConfigureAwait(false);
        }

        public Task UpdateMilestoneAsync(int milestoneNumber, string ownerName, string repositoryName, object data, CancellationToken cancellationToken)
        {
            return this.InvokeAsync("PATCH", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/milestones/{milestoneNumber}", data, cancellationToken);
        }

        public Task CreateStatusAsync(string ownerName, string repositoryName, string commitHash, string state, string target_url, string description, string context, CancellationToken cancellationToken)
        {
            return this.InvokeAsync("POST", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/statuses/{Esc(commitHash)}", new { state, target_url, description, context }, cancellationToken);
        }

        public Task CreateCommentAsync(int issueId, string ownerName, string repositoryName, string commentText, CancellationToken cancellationToken)
        {
            return this.InvokeAsync("POST", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/issues/{issueId}/comments", new { body = commentText }, cancellationToken);
        }

        public async Task<int?> FindMilestoneAsync(string title, string ownerName, string repositoryName, CancellationToken cancellationToken)
        {
            var milestones = await this.GetMilestonesAsync(ownerName, repositoryName, "all", cancellationToken).ConfigureAwait(false);

            return milestones
                .Where(m => string.Equals(m["title"]?.ToString() ?? string.Empty, title, StringComparison.OrdinalIgnoreCase))
                .Select(m => m["number"] as int?)
                .FirstOrDefault();
        }

        public async Task<IList<Dictionary<string, object>>> GetMilestonesAsync(string ownerName, string repositoryName, string state, CancellationToken cancellationToken)
        {
            var milestones = await this.InvokePagesAsync("GET", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/milestones?state={Uri.EscapeDataString(state)}&sort=due_on&direction=desc&per_page=100", cancellationToken).ConfigureAwait(false);
            if (milestones == null)
                return new Dictionary<string, object>[0];

            return milestones.Cast<Dictionary<string, object>>().ToList();
        }

        public async Task<IList<Dictionary<string, object>>> GetProjectsAsync(string ownerName, string repositoryName, CancellationToken cancellationToken)
        {
            var url = $"{this.apiBaseUrl}/orgs/{Esc(ownerName)}/projects?state=all";
            if (!string.IsNullOrEmpty(repositoryName))
                url = $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/projects?state=all";

            var projects = await this.InvokePagesAsync("GET", url, cancellationToken);
            if (projects == null)
                return new Dictionary<string, object>[0];

            return projects.Cast<Dictionary<string, object>>().ToList();
        }

        internal async Task<IList<KeyValuePair<string, IList<Dictionary<string, object>>>>> GetProjectColumnsAsync(string projectColumnsUrl, CancellationToken cancellationToken)
        {
            var columnData = await this.InvokePagesAsync("GET", projectColumnsUrl, cancellationToken);
            if (columnData == null)
                return new KeyValuePair<string, IList<Dictionary<string, object>>>[0];

            var columns = new List<KeyValuePair<string, IList<Dictionary<string, object>>>>();
            foreach (var column in columnData.Cast<Dictionary<string, object>>())
            {
                var cardData = await this.InvokePagesAsync("GET", (string)column["cards_url"], cancellationToken);
                var cards = cardData?.Cast<Dictionary<string, object>>().ToArray() ?? new Dictionary<string, object>[0];
                columns.Add(new KeyValuePair<string, IList<Dictionary<string, object>>>((string)column["name"], cards));
            }

            return columns;
        }

        public async Task<Dictionary<string, object>> GetReleaseAsync(string ownerName, string repositoryName, string tag, CancellationToken cancellationToken)
        {
            try
            {
                var releases = await this.InvokePagesAsync("GET", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/releases", cancellationToken).ConfigureAwait(false);
                return releases.Cast<Dictionary<string, object>>().SingleOrDefault(r => string.Equals(r["tag_name"], tag));
            }
            catch (Exception ex) when (((ex.InnerException as WebException)?.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<IEnumerable<string>> ListRefsAsync(string ownerName, string repositoryName, RefType? type, CancellationToken cancellationToken)
        {
            var prefix = AH.Switch<RefType?, string>(type)
                .Case(null, "refs")
                .Case(RefType.Branch, "refs/heads")
                .Case(RefType.Tag, "refs/tags")
                .End();

            var refs = await this.InvokePagesAsync("GET", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/git/{prefix}", cancellationToken).ConfigureAwait(false);

            return refs.Cast<Dictionary<string, object>>().Select(r => trim((string)r["ref"]));

            string trim(string s)
            {
                if (s.StartsWith(prefix))
                    s = s.Substring(prefix.Length);

                if (s.StartsWith("/"))
                    s = s.Substring(1);

                return s;
            }
        }

        public async Task<object> EnsureReleaseAsync(string ownerName, string repositoryName, string tag, string target, string name, string body, bool? draft, bool? prerelease, CancellationToken cancellationToken)
        {
            var data = new Dictionary<string, object>();
            data["tag_name"] = tag;
            if (target != null)
            {
                data["target_commitish"] = target;
            }
            if (name != null)
            {
                data["name"] = name;
            }
            if (body != null)
            {
                data["body"] = body;
            }
            if (draft.HasValue)
            {
                data["draft"] = draft.Value;
            }
            if (prerelease.HasValue)
            {
                data["prerelease"] = prerelease.Value;
            }

            var existingRelease = await this.GetReleaseAsync(ownerName, repositoryName, tag, cancellationToken).ConfigureAwait(false);
            if (existingRelease != null)
            {
                return await this.InvokeAsync("PATCH", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/releases/{Esc(existingRelease["id"])}", data, cancellationToken).ConfigureAwait(false);
            }
            return await this.InvokeAsync("POST", $"{this.apiBaseUrl}/repos/{Esc(ownerName)}/{Esc(repositoryName)}/releases", data, cancellationToken).ConfigureAwait(false);
        }

        public async Task<object> UploadReleaseAssetAsync(string ownerName, string repositoryName, string tag, string name, string contentType, Stream contents, Action<long> reportProgress, CancellationToken cancellationToken)
        {
            var release = await this.GetReleaseAsync(ownerName, repositoryName, tag, cancellationToken).ConfigureAwait(false);
            if (release == null)
            {
                throw new ArgumentException($"No release found with tag {tag} in repository {ownerName}/{repositoryName}", nameof(tag));
            }

            string uploadUrl = FormatTemplateUri((string)release["upload_url"], name);

            var request = this.CreateRequest("POST", uploadUrl);
            request.ContentType = contentType;
            request.AllowWriteStreamBuffering = false;
            try
            {
                request.ContentLength = contents.Length;
            }
            catch
            {
                request.SendChunked = true;
            }

            using (cancellationToken.Register(() => request.Abort()))
            {
                using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                {
                    await contents.CopyToAsync(requestStream, 81920, cancellationToken, reportProgress).ConfigureAwait(false);
                }

                try
                {
                    using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                    using (var responseStream = response.GetResponseStream())
                    using (var reader = new StreamReader(responseStream, InedoLib.UTF8Encoding))
                    {
                        var js = new JavaScriptSerializer();
                        string responseText = await reader.ReadToEndAsync().ConfigureAwait(false);
                        var responseJson = js.DeserializeObject(responseText);
                        return responseJson;
                    }
                }
                catch (WebException ex) when (ex.Status == WebExceptionStatus.RequestCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
                catch (WebException ex) when (ex.Response != null)
                {
                    using (var responseStream = ex.Response.GetResponseStream())
                    using (var reader = new StreamReader(responseStream, InedoLib.UTF8Encoding))
                    {
                        string message = await reader.ReadToEndAsync().ConfigureAwait(false);
                        throw new Exception(message, ex);
                    }
                }
            }
        }

        private static string FormatTemplateUri(string templateUri, string name)
        {
            // quick hack for URI templates since former NuGet package doesn't support target framework v4.5.2 
            // The format of templatedUploadUri is: https://host/repos/org/repoName/releases/1000/assets{?name,label}

            int index = templateUri.IndexOf('{');
            return templateUri.Substring(0, index) + "?name=" + Uri.EscapeDataString(name);
        }

        private static LazyRegex NextPageLinkPattern = new LazyRegex("<(?<uri>[^>]+)>; rel=\"next\"", RegexOptions.Compiled);

        private async Task<IEnumerable<object>> InvokePagesAsync(string method, string uri, CancellationToken cancellationToken)
        {
            return (IEnumerable<object>)await this.InvokeAsync(method, uri, null, true, cancellationToken).ConfigureAwait(false);
        }
        private Task<object> InvokeAsync(string method, string uri, CancellationToken cancellationToken)
        {
            return this.InvokeAsync(method, uri, null, false, cancellationToken);
        }
        private Task<object> InvokeAsync(string method, string uri, object data, CancellationToken cancellationToken)
        {
            return this.InvokeAsync(method, uri, data, false, cancellationToken);
        }
        private async Task<object> InvokeAsync(string method, string uri, object data, bool allPages, CancellationToken cancellationToken)
        {
            var request = this.CreateRequest(method, uri);

            using (cancellationToken.Register(() => request.Abort()))
            {
                var cacheKey = $"github-{this.UserName ?? string.Empty}-{uri}";
                var cachedResponse = method == "GET" ? (Tuple<string, string, object>)MemoryCache.Default.Get(cacheKey) : null;
                if (cachedResponse != null)
                {
                    request.Headers.Add("If-None-Match", cachedResponse.Item1);
                }

                if (data != null)
                {
                    using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    using (var writer = new StreamWriter(requestStream, InedoLib.UTF8Encoding))
                    {
                        await writer.WriteAsync(jsonSerializer.Serialize(data)).ConfigureAwait(false);
                    }
                }

                try
                {
                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        object responseJson;
                        string linkHeader;

                        if (response.StatusCode == HttpStatusCode.NotModified)
                        {
                            responseJson = cachedResponse.Item3;
                            linkHeader = cachedResponse.Item2;
                        }
                        else
                        {
                            linkHeader = response.Headers["Link"] ?? string.Empty;

                            using (var responseStream = response.GetResponseStream())
                            using (var reader = new StreamReader(responseStream, InedoLib.UTF8Encoding))
                            {
                                string responseText = await reader.ReadToEndAsync().ConfigureAwait(false);
                                responseJson = jsonSerializer.DeserializeObject(responseText);
                            }

                            if (method == "GET")
                            {
                                MemoryCache.Default.Set(cacheKey, Tuple.Create(response.Headers.Get("ETag"), linkHeader, responseJson), null);
                            }
                        }

                        if (allPages)
                        {
                            var nextPage = NextPageLinkPattern.Match(linkHeader);
                            if (nextPage.Success)
                            {
                                responseJson = ((IEnumerable<object>)responseJson).Concat((IEnumerable<object>)await this.InvokeAsync(method, nextPage.Groups["uri"].Value, data, true, cancellationToken).ConfigureAwait(false));
                            }
                        }

                        return responseJson;
                    }
                }
                catch (WebException ex) when (ex.Status == WebExceptionStatus.RequestCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
                catch (WebException ex) when (ex.Response != null)
                {
                    using (var responseStream = ex.Response.GetResponseStream())
                    using (var reader = new StreamReader(responseStream, InedoLib.UTF8Encoding))
                    {
                        string message = await reader.ReadToEndAsync().ConfigureAwait(false);
                        throw new Exception(message, ex);
                    }
                }
            }
        }
        private HttpWebRequest CreateRequest(string method, string uri)
        {
            var request = WebRequest.CreateHttp(uri);
            request.UserAgent = "InedoGitHubExtension/" + typeof(GitHubClient).Assembly.GetName().Version.ToString();
            request.Method = method;
            request.Accept = string.Join(", ", new[] { "application/vnd.github.v3+json" }.Concat(EnabledPreviews));

            if (!string.IsNullOrEmpty(this.UserName))
                request.Headers[HttpRequestHeader.Authorization] = "basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(this.UserName + ":" + AH.Unprotect(this.Password)));

            return request;
        }
    }

    internal sealed class GitHubIssueFilter
    {
        private string milestone;

        public string Milestone
        {
            get { return this.milestone; }
            set
            {
                if (value != null && AH.ParseInt(value) == null && value != "*" && !string.Equals("none", value, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("milestone must be an integer, or a string of '*' or 'none'.");

                this.milestone = value;
            }
        }
        public string Labels { get; set; }
        public string CustomFilterQueryString { get; set; }

        public string ToQueryString()
        {
            if (!string.IsNullOrEmpty(this.CustomFilterQueryString))
                return this.CustomFilterQueryString;

            var buffer = new StringBuilder(128);
            buffer.Append("?state=all");
            if (!string.IsNullOrEmpty(this.Milestone))
                buffer.Append("&milestone=" + Uri.EscapeDataString(this.Milestone));
            if (!string.IsNullOrEmpty(this.Labels))
                buffer.Append("&labels=" + Uri.EscapeDataString(this.Labels));
            buffer.Append("&per_page=100");

            return buffer.ToString();
        }
    }
}
