using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Todoist.Net
{
    internal class TodoistAuthMessageHandler : DelegatingHandler
    {
        private Task<HttpResponseMessage> _cachedRefreshTask = null;
        private readonly object _refreshLock = new object();

        private readonly TodoistAuthenticationContext _authContext;

        public TodoistAuthMessageHandler(TodoistAuthenticationContext authContext)
        {
            _authContext = authContext;
        }


        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization != null || request.RequestUri.OriginalString == ApiConstants.TokenRefreshEndpoint) //?
            {
                return base.SendAsync(request, cancellationToken);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authContext.Tokens.AccessToken);

            return base.SendAsync(request, cancellationToken);
        }
    }
}
