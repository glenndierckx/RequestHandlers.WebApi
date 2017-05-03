using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using System.Web.Http.Dispatcher;
using Autofac;
using Autofac.Integration.WebApi;
using Microsoft.Owin;
using Owin;
using RequestHandlers.Http;
using RequestHandlers.WebApi;
//using RequestHandlers.WebApi;
using Swashbuckle.Application;

[assembly: OwinStartup(typeof(RequestHandlers.TestHost.Startup))]

namespace RequestHandlers.TestHost
{
    [GetRequest("api/test")]
    public class TestRequest : IReturn<TestResponse>
    {
        
    }

    public class TestResponse
    {
        
    }

    public class TestRequestHandlers : IAsyncRequestHandler<TestRequest, TestResponse>
    {
        public async Task<TestResponse> Handle(TestRequest request)
        {
            await Task.FromResult(0);
            return new TestResponse();
        }
    }
    

    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            var builder = new Autofac.ContainerBuilder();
            builder.RegisterType<DefaultRequestDispacher>().As<IRequestDispatcher>();
            builder.RegisterType<RequestHandlerResolver>().As<IRequestHandlerResolver>();
            builder.RegisterType<DefaultWebApiRequestProcessor>().As<IWebApiRequestProcessor>();
            var requestHandlerInterface = typeof(IRequestHandler<,>);
            var requestHandlerDefinitions = RequestHandlerFinder.InAssembly(this.GetType().GetTypeInfo().Assembly);
            foreach (var requestHandler in requestHandlerDefinitions)
            {
                builder.RegisterType(requestHandler.RequestHandlerType)
                    .As(requestHandlerInterface.MakeGenericType(requestHandler.RequestType, requestHandler.ResponseType));
            }



            var config = new HttpConfiguration();
            var generated = config.ConfigureRequestHandlers(requestHandlerDefinitions);
            builder.RegisterApiControllers(generated);

            var container = builder.Build();
            config.DependencyResolver = new AutofacWebApiDependencyResolver(container);
            // Register the Autofac middleware FIRST, then the Autofac Web API middleware,
            // and finally the standard Web API middleware.
            config.MapHttpAttributeRoutes();
            app.UseAutofacMiddleware(container);
            app.UseAutofacWebApi(config);
            app.UseWebApi(config);



            config.EnableSwagger(c =>
            {
                c.SingleApiVersion("v1", "RequestHandlers.TestHost");
            })
                .EnableSwaggerUi(c =>
                {
                });
        }
    }

    class RequestHandlerResolver : IRequestHandlerResolver
    {
        private readonly ILifetimeScope _scope;

        public RequestHandlerResolver(ILifetimeScope scope)
        {
            _scope = scope;
        }

        public IRequestHandler<TRequest, TResponse> Resolve<TRequest, TResponse>()
        {
            var result = (IRequestHandler<TRequest, TResponse>)_scope.Resolve(typeof(IRequestHandler<TRequest, TResponse>));
            return result;
        }
    }
}
