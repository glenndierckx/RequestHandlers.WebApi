using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Results;

namespace RequestHandlers.WebApi
{
    public interface IWebApiRequestProcessor
    {
        IHttpActionResult Process<TRequest, TResponse>(TRequest request, ApiController controller);
        Task<IHttpActionResult> ProcessAsync<TRequest, TResponse>(TRequest request, ApiController controller);
    }

    public class DefaultWebApiRequestProcessor : IWebApiRequestProcessor
    {
        private readonly IRequestDispatcher _dispatcher;

        public DefaultWebApiRequestProcessor(IRequestDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public IHttpActionResult Process<TRequest, TResponse>(TRequest request, ApiController controller)
        {
            var response = _dispatcher.Process<TRequest, TResponse>(request);
            return new OkNegotiatedContentResult<TResponse>(response, controller);
        }

        public async Task<IHttpActionResult> ProcessAsync<TRequest, TResponse>(TRequest request, ApiController controller)
        {
            var response = await _dispatcher.Process<TRequest, Task<TResponse>>(request);
            return new OkNegotiatedContentResult<TResponse>(response, controller);
        }
    }
}
