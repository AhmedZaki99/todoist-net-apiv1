using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Todoist.Net.Exceptions;
using Todoist.Net.Extensions;
using Todoist.Net.Models;

namespace Todoist.Net
{
    internal class TodoistRestClient : ITodoistRestClient
    {
        private static readonly Lazy<HttpClient> _defaultClient = new Lazy<HttpClient>(() => CreateClient());
        private static readonly ConcurrentDictionary<IWebProxy, HttpClient> _proxiedClients = new ConcurrentDictionary<IWebProxy, HttpClient>();

        protected string AccessToken { get; set; }
        protected HttpClient HttpClient { get; }

        public TodoistRestClient(string token) : this(token, (IWebProxy)null)
        { }

        public TodoistRestClient(string token, IWebProxy proxy)
        {
            ThrowHelper.ThrowIfNullOrEmpty(token, nameof(token));

            // We use long-lived HttpClient instances in cases where IHttpClientFactory is not available (e.g., in .NET Framework).
            // This is to avoid socket exhaustion issues.
            AccessToken = token;
            HttpClient = proxy == null
                ? _defaultClient.Value
                : _proxiedClients.GetOrAdd(proxy, CreateClient);
        }

        public TodoistRestClient(string token, HttpClient httpClient)
        {
            ThrowHelper.ThrowIfNullOrEmpty(token, nameof(token));
            ThrowHelper.ThrowIfNull(httpClient, nameof(httpClient));

            // We use the provided short-lived HttpClient instance here because it has its own lifetime management.
            AccessToken = token;
            HttpClient = httpClient;
        }


        protected virtual void Dispose(bool disposing) { }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        /// <inheritdoc/>
        public virtual async Task<HttpResponseMessage> GetAsync(string resource, Dictionary<string, string> queryParams = null, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNullOrEmpty(resource, nameof(resource));

            using (var request = BuildResourceRequest(HttpMethod.Get, resource, queryParams))
            {
                return await HttpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public virtual async Task<HttpResponseMessage> PostAsync(string resource, Dictionary<string, string> formParams = null, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNullOrEmpty(resource, nameof(resource));

            using (var request = BuildResourceRequest(HttpMethod.Post, resource))
            {
                request.Content = new FormUrlEncodedContent(formParams ?? new Dictionary<string, string>());

                return await HttpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public virtual async Task<HttpResponseMessage> PostFilesAsync(string resource, UploadFile[] files, Dictionary<string, string> formParams = null, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNullOrEmpty(resource, nameof(resource));
            ThrowHelper.ThrowIfNull(files, nameof(files));

            using (var request = BuildResourceRequest(HttpMethod.Post, resource))
            {
                request.Content = new MultipartFormDataContent()
                    .AddStringParts(formParams)
                    .AddFileParts("file", files);

                return await HttpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public virtual async Task<HttpResponseMessage> PostJsonAsync(string resource, string jsonContent, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNullOrEmpty(resource, nameof(resource));
            ThrowHelper.ThrowIfNullOrEmpty(jsonContent, nameof(jsonContent));

            using (var request = BuildResourceRequest(HttpMethod.Post, resource))
            {
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                return await HttpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public virtual async Task<HttpResponseMessage> PutAsync(string resource, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNullOrEmpty(resource, nameof(resource));

            using (var request = BuildResourceRequest(HttpMethod.Put, resource))
            {
                return await HttpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public virtual async Task<HttpResponseMessage> PutJsonAsync(string resource, string jsonContent, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNullOrEmpty(resource, nameof(resource));
            ThrowHelper.ThrowIfNullOrEmpty(jsonContent, nameof(jsonContent));

            using (var request = BuildResourceRequest(HttpMethod.Put, resource))
            {
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                return await HttpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public virtual async Task<HttpResponseMessage> DeleteAsync(string resource, Dictionary<string, string> queryParams = null, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNullOrEmpty(resource, nameof(resource));

            using (var request = BuildResourceRequest(HttpMethod.Delete, resource, queryParams))
            {
                return await HttpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }


        private HttpRequestMessage BuildResourceRequest(HttpMethod method, string resource, Dictionary<string, string> queryParams = null)
        {
            var requestUri = BuildResourceUri(resource, queryParams);
            return new HttpRequestMessage(method, requestUri);
        }

        private static string BuildResourceUri(string resource, Dictionary<string, string> queryParams = null)
        {
            if (queryParams == null || queryParams.Count == 0)
            {
                return $"{ApiConstants.ResourcesEndpoint}/{resource}";
            }

            string encode(string data) => string.IsNullOrEmpty(data)
                ? string.Empty
                : Uri.EscapeDataString(data).Replace("%20", "+");

            var builder = new StringBuilder();

            foreach (var pair in queryParams)
            {
                if (builder.Length > 0)
                {
                    builder.Append('&');
                }
                builder.Append(encode(pair.Key));
                builder.Append('=');
                builder.Append(encode(pair.Value));
            }
            return $"{ApiConstants.ResourcesEndpoint}/{resource}?{builder}";
        }

        private static HttpClient CreateClient(IWebProxy proxy = null)
        {
            var rootHandler = new HttpClientHandler();
            if (proxy != null)
            {
                rootHandler.Proxy = proxy;
                rootHandler.UseProxy = true;
            }
            var authHandler = new TodoistAuthMessageHandler(null)
            {
                InnerHandler = rootHandler
            };

            return new HttpClient(authHandler)
            {
                BaseAddress = new Uri(ApiConstants.ApiBaseUrl)
            };
        }
    }
}
