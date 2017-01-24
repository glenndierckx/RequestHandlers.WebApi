using System.Reflection;
using RequestHandlers.Http;

namespace RequestHandlers.WebApi
{
    public interface IControllerAssemblyBuilder
    {
        Assembly Build(HttpRequestHandlerDefinition[] definitions);
    }
}