using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Todoist.Net.Exceptions;
using Todoist.Net.Models;

namespace Todoist.Net
{
    internal class RefreshableTodoistRestClient : TodoistRestClient, IRefreshableTodoistRestClient
    {
        private Task<HttpResponseMessage> _activeRefreshTask = null;
        private readonly object _refreshLock = new object();

        private readonly TodoistAuthenticationContext _authContext;

        public RefreshableTodoistRestClient(TodoistAuthenticationContext authContext) : base(authContext?.Tokens?.AccessToken)
        {
            ThrowHelper.ThrowIfNull(authContext, nameof(authContext));

            _authContext = authContext;
        }

        public RefreshableTodoistRestClient(TodoistAuthenticationContext authContext, IWebProxy proxy) : base(authContext?.Tokens?.AccessToken, proxy)
        {
            ThrowHelper.ThrowIfNull(authContext, nameof(authContext));

            _authContext = authContext;
        }

        public RefreshableTodoistRestClient(TodoistAuthenticationContext authContext, HttpClient httpClient) : base(authContext?.Tokens?.AccessToken, httpClient)
        {
            ThrowHelper.ThrowIfNull(authContext, nameof(authContext));

            _authContext = authContext;
        }


        /// <inheritdoc/>
        public override Task<HttpResponseMessage> GetAsync(string resource, Dictionary<string, string> queryParams = null, CancellationToken cancellationToken = default)
        {
            return ExecuteWithTokenRefreshAsync(() =>
                base.GetAsync(resource, queryParams, cancellationToken), cancellationToken);
        }

        /// <inheritdoc/>
        public override Task<HttpResponseMessage> PostAsync(string resource, Dictionary<string, string> formParams = null, CancellationToken cancellationToken = default)
        {
            return ExecuteWithTokenRefreshAsync(() =>
                base.PostAsync(resource, formParams, cancellationToken), cancellationToken);
        }

        /// <inheritdoc/>
        public override Task<HttpResponseMessage> PostFilesAsync(string resource, UploadFile[] files, Dictionary<string, string> formParams = null, CancellationToken cancellationToken = default)
        {
            return ExecuteWithTokenRefreshAsync(() =>
                base.PostFilesAsync(resource, files, formParams, cancellationToken), cancellationToken);
        }

        /// <inheritdoc/>
        public override Task<HttpResponseMessage> PostJsonAsync(string resource, string jsonContent, CancellationToken cancellationToken = default)
        {
            return ExecuteWithTokenRefreshAsync(() =>
                base.PostJsonAsync(resource, jsonContent, cancellationToken), cancellationToken);
        }

        /// <inheritdoc/>
        public override Task<HttpResponseMessage> PutAsync(string resource, CancellationToken cancellationToken = default)
        {
            return ExecuteWithTokenRefreshAsync(() =>
                base.PutAsync(resource, cancellationToken), cancellationToken);
        }

        /// <inheritdoc/>
        public override Task<HttpResponseMessage> PutJsonAsync(string resource, string jsonContent, CancellationToken cancellationToken = default)
        {
            return ExecuteWithTokenRefreshAsync(() =>
                base.PutJsonAsync(resource, jsonContent, cancellationToken), cancellationToken);
        }

        /// <inheritdoc/>
        public override Task<HttpResponseMessage> DeleteAsync(string resource, Dictionary<string, string> queryParams = null, CancellationToken cancellationToken = default)
        {
            return ExecuteWithTokenRefreshAsync(() => 
                base.DeleteAsync(resource, queryParams, cancellationToken), cancellationToken);
        }


        /// <inheritdoc/>
        public Task<HttpResponseMessage> RefreshTokensAsync(CancellationToken cancellationToken = default)
        {
            lock (_refreshLock)
            {
                if (_activeRefreshTask == null)
                {
                    _activeRefreshTask = RefreshTokensCoreAsync(cancellationToken)
                        .ContinueWith(t =>
                        {
                            _activeRefreshTask = null;
                            return t.Result;
                        }, cancellationToken);
                }
                return _activeRefreshTask;
            }
        }

        /// <inheritdoc/>
        public async Task<HttpResponseMessage> RevokeTokensAsync(CancellationToken cancellationToken = default)
        {
            var formParams = new Dictionary<string, string>
            {
                { "token", _authContext.Tokens.AccessToken },
                { "token_type_hint", "access_token" }
            };
            var encodedCreds = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_authContext.Credentials.ClientId}:{_authContext.Credentials.ClientSecret}"));

            using (var request = new HttpRequestMessage(HttpMethod.Post, ApiConstants.TokenRevokeEndpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCreds);
                request.Content = new FormUrlEncodedContent(formParams);

                return await HttpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }


        private async Task<HttpResponseMessage> ExecuteWithTokenRefreshAsync(Func<Task<HttpResponseMessage>> action, CancellationToken cancellationToken)
        {
            bool tokenFoundExpired = _authContext.Tokens.ExpirationTimeUtc <= DateTime.UtcNow.AddMinutes(1);
            bool refreshTokenExist = !string.IsNullOrEmpty(_authContext.Tokens.RefreshToken);

            if (tokenFoundExpired && refreshTokenExist)
            {
                return await RefreshAndExecuteAsync(action, cancellationToken).ConfigureAwait(false);
            }

            var response = await action().ConfigureAwait(false);
            if (!tokenFoundExpired && refreshTokenExist && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                return await RefreshAndExecuteAsync(action, cancellationToken).ConfigureAwait(false);
            }
            return response;
        }

        private async Task<HttpResponseMessage> RefreshAndExecuteAsync(Func<Task<HttpResponseMessage>> action, CancellationToken cancellationToken)
        {
            var refreshResponse = await RefreshTokensAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshResponse.IsSuccessStatusCode)
            {
                return refreshResponse;
            }
            refreshResponse.Dispose();

            return await action().ConfigureAwait(false);
        }


        private async Task<HttpResponseMessage> RefreshTokensCoreAsync(CancellationToken cancellationToken)
        {
            var formParams = new Dictionary<string, string>
            {
                { "client_id", _authContext.Credentials.ClientId },
                { "client_secret", _authContext.Credentials.ClientSecret },
                { "refresh_token", _authContext.Tokens.RefreshToken },
                { "grant_type", "refresh_token" }
            };
            using (var content = new FormUrlEncodedContent(formParams))
            {
                var response = await HttpClient.PostAsync(ApiConstants.TokenRefreshEndpoint, content, cancellationToken)
                    .ConfigureAwait(false);

                await HandleTokenRefreshResponseAsync(response, cancellationToken)
                    .ConfigureAwait(false);

                return response;
            }
        }

        private async Task<bool> HandleTokenRefreshResponseAsync(HttpResponseMessage refreshResponse, CancellationToken cancellationToken)
        {
            if (!refreshResponse.IsSuccessStatusCode)
            {
                return false;
            }
            var jsonResponse = await GetJsonAndResetContentAsync<TokenRefreshResponse>(refreshResponse)
                .ConfigureAwait(false);

            var expirationTimeUtc = jsonResponse.ExpiresIn > 0
                ? DateTime.UtcNow.AddSeconds(jsonResponse.ExpiresIn)
                : (DateTime?)null;

            AccessToken = jsonResponse.AccessToken;
            _authContext.Tokens = new TodoistTokens(
                jsonResponse.AccessToken,
                jsonResponse.RefreshToken,
                expirationTimeUtc);

            if (_authContext.OnRefresh != null)
            {
                await _authContext.OnRefresh(jsonResponse, _authContext.RefreshState, cancellationToken)
                    .ConfigureAwait(false);
            }
            return true;
        }


        private static async Task<T> GetJsonAndResetContentAsync<T>(HttpResponseMessage response)
        {
            using (var originalContent = response.Content)
            {
                var responseBody = await originalContent.ReadAsByteArrayAsync()
                    .ConfigureAwait(false);

                var bufferedContent = new ByteArrayContent(responseBody);
                foreach (var header in originalContent.Headers)
                {
                    bufferedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                response.Content = bufferedContent;
                return JsonSerializer.Deserialize<T>(responseBody);
            }
        }
    }
}
