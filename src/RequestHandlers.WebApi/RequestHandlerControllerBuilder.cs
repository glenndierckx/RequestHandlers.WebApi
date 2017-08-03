using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Web.Http;
using System.Web.Http.Dispatcher;
using RequestHandlers.Http;
using RequestHandlers.WebApi.CSharp;

namespace RequestHandlers.WebApi
{
    public class DynamicHttpControllerTypeResolver : DefaultHttpControllerTypeResolver
    {
        private readonly Assembly[] _dynamicAssemblies;

        public DynamicHttpControllerTypeResolver(params Assembly[] dynamicAssemblies)
        {
            _dynamicAssemblies = dynamicAssemblies;
        }

        public override ICollection<Type> GetControllerTypes(IAssembliesResolver assembliesResolver)
        {
            if (assembliesResolver == null)
            {
                throw new ArgumentNullException(nameof(assembliesResolver));
            }

            var result = new List<Type>();
            result.AddRange(base.GetControllerTypes(assembliesResolver));
            var assemblies = assembliesResolver.GetAssemblies();
            foreach (Assembly assembly in assemblies.Where(assembly => (assembly == null || assembly.IsDynamic) && _dynamicAssemblies.Contains(assembly)))
            {
                result.AddRange(assembly.GetTypes().Where(x => !result.Contains(x)).Where(x => IsControllerTypePredicate(x)).ToArray());
            }

            return result;
        }
    }
    
    public static class WebApiConfig
    {
        public static Assembly ConfigureRequestHandlers(this HttpConfiguration config, 
            IRequestDefinition[] requestHandlerDefinitions, 
            IControllerAssemblyBuilder controllerAssemblyBuilder = null)
        {
            var assembly = RequestHandlerControllerBuilder.Build(requestHandlerDefinitions, controllerAssemblyBuilder);

            return config.ReplaceServicesSoNewControllersInGeneratedAssembliesCanBeResolved(assembly);
        }
        private static Assembly ReplaceServicesSoNewControllersInGeneratedAssembliesCanBeResolved(this HttpConfiguration config, Assembly assembly)
        {
            var assemblyResolver = new DynamicAssemblyResolver(assembly);
            config.Services.Replace(typeof(IAssembliesResolver), assemblyResolver);

            config.Services.Replace(typeof(IHttpControllerTypeResolver), new DynamicHttpControllerTypeResolver(assembly));
            return assembly;
        }
    }
    public class DynamicAssemblyResolver : DefaultAssembliesResolver
    {
        private readonly Assembly[] _assembliesToLoad;

        public DynamicAssemblyResolver(params Assembly[] assembliesToLoad)
        {
            _assembliesToLoad = assembliesToLoad;
        }

        public override ICollection<Assembly> GetAssemblies()
        {
            var result = _assembliesToLoad.ToList();
            result.AddRange(base.GetAssemblies());
            return result.Distinct().ToArray();
        }
    }
    public static class RequestHandlerControllerBuilder
    {
        public static Assembly Build(IRequestDefinition[] definitions, IControllerAssemblyBuilder controllerAssemblyBuilder = null)
        {
            controllerAssemblyBuilder = controllerAssemblyBuilder ?? new CSharpBuilder("Proxy");
            var controllerDefinitions =
                definitions.SelectMany(x => 
                    x.RequestType.GetTypeInfo()
                        .GetCustomAttributes(true)
                        .OfType<HttpRequestAttribute>()
                        .Select(d => new HttpRequestHandlerDefinition(d, x))
                )
                .ToArray();
            return controllerAssemblyBuilder.Build(controllerDefinitions);
        }
    }

}
