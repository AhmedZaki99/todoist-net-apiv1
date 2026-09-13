using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Todoist.Net.Tests;

[Trait(Constants.TraitName, Constants.UnitTraitValue)]
public class RefreshableTodoistRestClientTests
{
    private const string RefreshedTokensJson = """
        {
            "access_token": "new-access-token",
            "refresh_token": "new-refresh-token",
            "token_type": "Bearer",
            "scope": "data:read_write",
            "expires_in": 3600
        }
        """;

    [Fact]
    public async Task ConcurrentRequestsAndManualRefresh_WithAnExpiredToken_RefreshOnlyOnceAndUseTheRotatedToken()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new StubTodoistHttpMessageHandler
        {
            RefreshResponder = async cancellationToken =>
            {
                refreshStarted.TrySetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return CreateJsonResponse(HttpStatusCode.OK, RefreshedTokensJson);
            }
        };
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: DateTime.UtcNow.AddMinutes(-5));
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Fire a mix of auto-refresh-triggering requests and a manual refresh at the same time.
        // Every caller runs synchronously up to the shared refresh task, so they all pile onto it.
        var tasks = new[]
        {
            restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken),
            restClient.PostAsync("sync", cancellationToken: TestContext.Current.CancellationToken),
            restClient.PostJsonAsync("sync", "{}", TestContext.Current.CancellationToken),
            restClient.PutAsync("tasks/6X7rM8997g3RQmvh/close", TestContext.Current.CancellationToken),
            restClient.DeleteAsync("tasks/6X7rM8997g3RQmvh", cancellationToken: TestContext.Current.CancellationToken),
            restClient.RefreshTokensAsync(TestContext.Current.CancellationToken)
        };

        // Step 2: Hold the refresh response back until every caller is parked on the same refresh task.
        await refreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        releaseRefresh.SetResult();

        var responses = await Task.WhenAll(tasks);


        // Step 3: Assert a single refresh happened, and every request went out with the rotated token.
        Assert.Equal(1, handler.RefreshCallCount);
        Assert.Equal(tasks.Length - 1, handler.ResourceCallCount);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.All(handler.ResourceRequestTokens, token => Assert.Equal("new-access-token", token));

        Assert.Equal("new-access-token", authContext.Tokens.AccessToken);
        Assert.Equal("new-refresh-token", authContext.Tokens.RefreshToken);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task RefreshTokens_RotatesTheStoredTokens_AndInvokesOnRefreshWithTheNewTokensAndState()
    {
        var handler = new StubTodoistHttpMessageHandler();
        using var httpClient = new HttpClient(handler);

        var refreshState = new object();
        var callbackInvocations = new List<(TokenRefreshResponse Response, object? State)>();

        var authContext = CreateAuthContext(
            expirationTimeUtc: DateTime.UtcNow.AddHours(1),
            onRefresh: (response, state, _) =>
            {
                callbackInvocations.Add((response, state));
                return Task.CompletedTask;
            },
            refreshState: refreshState);
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);

        var beforeRefresh = DateTime.UtcNow;


        // Step 1: Manually refresh the tokens.
        using var refreshResponse = await restClient.RefreshTokensAsync(TestContext.Current.CancellationToken);


        // Step 2: Assert the stored tokens were rotated to the ones from the refresh response.
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        Assert.Equal("new-access-token", authContext.Tokens.AccessToken);
        Assert.Equal("new-refresh-token", authContext.Tokens.RefreshToken);
        Assert.NotNull(authContext.Tokens.ExpirationTimeUtc);
        Assert.InRange(
            authContext.Tokens.ExpirationTimeUtc.Value,
            beforeRefresh.AddSeconds(3600),
            DateTime.UtcNow.AddSeconds(3600));


        // Step 3: Assert the callback received the new tokens and the original state object.
        var (callbackResponse, callbackState) = Assert.Single(callbackInvocations);
        Assert.Equal("new-access-token", callbackResponse.AccessToken);
        Assert.Equal("new-refresh-token", callbackResponse.RefreshToken);
        Assert.Equal("Bearer", callbackResponse.TokenType);
        Assert.Equal("data:read_write", callbackResponse.Scope);
        Assert.Equal(3600, callbackResponse.ExpiresIn);
        Assert.Same(refreshState, callbackState);


        // Step 4: Assert subsequent requests authenticate with the rotated access token.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("new-access-token", handler.ResourceRequestTokens.Last());
    }

    [Fact]
    public async Task Request_WithAValidToken_SkipsTheRefresh()
    {
        var handler = new StubTodoistHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: DateTime.UtcNow.AddHours(1));
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request with a token that is far from expiring.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the request went straight through with the original token.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, handler.RefreshCallCount);
        Assert.Equal("old-access-token", Assert.Single(handler.ResourceRequestTokens));
        Assert.Equal("old-access-token", authContext.Tokens.AccessToken);
    }

    [Fact]
    public async Task Request_WithAnUnknownExpiration_SkipsTheProactiveRefresh()
    {
        var handler = new StubTodoistHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: null);
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request with a token whose expiration time is not known.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert no proactive refresh was attempted.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, handler.RefreshCallCount);
        Assert.Equal("old-access-token", Assert.Single(handler.ResourceRequestTokens));
    }

    [Fact]
    public async Task Request_WithAnExpiredToken_RefreshesBeforeSendingTheRequest()
    {
        var handler = new StubTodoistHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: DateTime.UtcNow.AddMinutes(-5));
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request with an expired token.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the token was refreshed first, and the request was sent once with the new token.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RefreshCallCount);
        Assert.Equal("new-access-token", Assert.Single(handler.ResourceRequestTokens));
        Assert.Equal("new-access-token", authContext.Tokens.AccessToken);
    }

    [Fact]
    public async Task Request_WhenUnauthorized_RefreshesAndRetriesWithTheNewToken()
    {
        var handler = new StubTodoistHttpMessageHandler
        {
            ResourceResponder = attempt => attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : CreateJsonResponse(HttpStatusCode.OK, "{}")
        };
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: DateTime.UtcNow.AddHours(1));
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request with a valid token which the API rejects once with a 401.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the token was refreshed and the request retried once with the new token.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RefreshCallCount);

        var actualTokens = handler.ResourceRequestTokens.ToArray();
        Assert.Equal(2, actualTokens.Length);
        Assert.Equal("old-access-token", actualTokens[0]);
        Assert.Equal("new-access-token", actualTokens[1]);
    }

    [Fact]
    public async Task Request_WhenStillUnauthorizedAfterTheRetry_DoesNotRetryAgain()
    {
        var handler = new StubTodoistHttpMessageHandler
        {
            ResourceResponder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: DateTime.UtcNow.AddHours(1));
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request which the API keeps rejecting with a 401.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the request was retried exactly once after the refresh, then given up on.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, handler.RefreshCallCount);
        Assert.Equal(2, handler.ResourceCallCount);
    }

    [Fact]
    public async Task Request_WhenTheRefreshFailsDuringRetry_ReturnsTheRefreshFailureAndKeepsTheOldTokens()
    {
        var handler = new StubTodoistHttpMessageHandler
        {
            RefreshResponder = _ => Task.FromResult(
                CreateJsonResponse(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }""")),
            ResourceResponder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        using var httpClient = new HttpClient(handler);

        var callbackInvocations = 0;
        var authContext = CreateAuthContext(
            expirationTimeUtc: DateTime.UtcNow.AddHours(1),
            onRefresh: (_, _, _) =>
            {
                Interlocked.Increment(ref callbackInvocations);
                return Task.CompletedTask;
            });
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request which gets a 401, while the refresh endpoint rejects the refresh token.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the refresh failure is surfaced, the request is not retried, and nothing was rotated.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, handler.RefreshCallCount);
        Assert.Equal(1, handler.ResourceCallCount);

        Assert.Equal("old-access-token", authContext.Tokens.AccessToken);
        Assert.Equal("old-refresh-token", authContext.Tokens.RefreshToken);
        Assert.Equal(0, callbackInvocations);
    }

    [Fact]
    public async Task Request_WithAnExpiredToken_WhenTheRefreshFails_ReturnsTheRefreshFailureWithoutSendingTheRequest()
    {
        var handler = new StubTodoistHttpMessageHandler
        {
            RefreshResponder = _ => Task.FromResult(
                CreateJsonResponse(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }"""))
        };
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: DateTime.UtcNow.AddMinutes(-5));
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request with an expired token, while the refresh endpoint rejects the refresh token.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the refresh failure is surfaced and the request was never sent.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, handler.RefreshCallCount);
        Assert.Equal(0, handler.ResourceCallCount);
        Assert.Equal("old-access-token", authContext.Tokens.AccessToken);
    }

    [Fact]
    public async Task Request_WhenAutomaticRefreshIsDisabled_SendsTheExpiredTokenWithoutRefreshing()
    {
        var handler = new StubTodoistHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(
            expirationTimeUtc: DateTime.UtcNow.AddMinutes(-5),
            disableAutomaticRefresh: true);
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request with an expired token while automatic refresh is disabled.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the request went straight through with the expired token.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, handler.RefreshCallCount);
        Assert.Equal("old-access-token", Assert.Single(handler.ResourceRequestTokens));
    }

    [Fact]
    public async Task Request_WithoutARefreshToken_DoesNotRetryWhenUnauthorized()
    {
        var handler = new StubTodoistHttpMessageHandler
        {
            ResourceResponder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(
            expirationTimeUtc: DateTime.UtcNow.AddHours(1),
            refreshToken: "");
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);


        // Step 1: Send a request which the API rejects with a 401, without a refresh token to fall back on.
        using var response = await restClient.GetAsync("tasks", cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the 401 is surfaced as-is, without a refresh attempt.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, handler.RefreshCallCount);
        Assert.Equal(1, handler.ResourceCallCount);
    }

    [Fact]
    public async Task PostFiles_WithANonSeekableStream_DoesNotRetryWhenUnauthorized()
    {
        var handler = new StubTodoistHttpMessageHandler
        {
            ResourceResponder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: DateTime.UtcNow.AddHours(1));
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);

        var file = new UploadFile(new NonSeekableReadStream(TestData.Files.GreenPng10x10), "green.png");


        // Step 1: Upload a file whose stream cannot be rewound, and get a 401 back.
        using var response = await restClient.PostFilesAsync(
            "uploads",
            [file],
            cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the 401 is surfaced without a refresh, because the request cannot be replayed.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, handler.RefreshCallCount);
        Assert.Equal(1, handler.ResourceCallCount);
    }

    [Fact]
    public async Task PostFiles_WithASeekableStream_RefreshesAndRetriesWhenUnauthorized()
    {
        var handler = new StubTodoistHttpMessageHandler
        {
            ResourceResponder = attempt => attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : CreateJsonResponse(HttpStatusCode.OK, "{}")
        };
        using var httpClient = new HttpClient(handler);
        var authContext = CreateAuthContext(expirationTimeUtc: DateTime.UtcNow.AddHours(1));
        using var restClient = new RefreshableTodoistRestClient(authContext, httpClient);

        var file = new UploadFile(TestData.Files.GreenPng10x10, "green.png");


        // Step 1: Upload a file whose stream can be rewound, and get a 401 back once.
        using var response = await restClient.PostFilesAsync(
            "uploads",
            [file],
            cancellationToken: TestContext.Current.CancellationToken);


        // Step 2: Assert the token was refreshed and the upload retried with the new token.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RefreshCallCount);
        Assert.Equal(2, handler.ResourceCallCount);
        Assert.Equal("new-access-token", handler.ResourceRequestTokens.Last());
    }


    private static TodoistAuthenticationContext CreateAuthContext(
        DateTime? expirationTimeUtc,
        TokenRefreshHandler? onRefresh = null,
        object? refreshState = null,
        bool disableAutomaticRefresh = false,
        string refreshToken = "old-refresh-token")
    {
        return new TodoistAuthenticationContext(
            new ClientCredentials("client-id", "client-secret"),
            new TodoistTokens("old-access-token", refreshToken, expirationTimeUtc),
            onRefresh,
            refreshState,
            disableAutomaticRefresh);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }


    /// <summary>
    /// Routes refresh-endpoint calls to a configurable responder and records the bearer token
    /// of every resource request, so tests can assert refresh counts and token rotation without HTTP traffic.
    /// </summary>
    private sealed class StubTodoistHttpMessageHandler : HttpMessageHandler
    {
        private int _refreshCallCount;
        private int _resourceCallCount;

        public int RefreshCallCount => _refreshCallCount;

        public int ResourceCallCount => _resourceCallCount;

        public ConcurrentQueue<string?> ResourceRequestTokens { get; } = new();

        public Func<CancellationToken, Task<HttpResponseMessage>>? RefreshResponder { get; set; }

        public Func<int, HttpResponseMessage>? ResourceResponder { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null && request.RequestUri.AbsolutePath == ApiConstants.TokenRefreshEndpoint)
            {
                Interlocked.Increment(ref _refreshCallCount);

                return RefreshResponder is not null
                    ? await RefreshResponder(cancellationToken)
                    : CreateJsonResponse(HttpStatusCode.OK, RefreshedTokensJson);
            }

            var attempt = Interlocked.Increment(ref _resourceCallCount);
            ResourceRequestTokens.Enqueue(request.Headers.Authorization?.Parameter);

            return ResourceResponder?.Invoke(attempt)
                ?? CreateJsonResponse(HttpStatusCode.OK, "{}");
        }
    }

    private sealed class NonSeekableReadStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;

        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
