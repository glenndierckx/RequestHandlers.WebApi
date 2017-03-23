using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using RequestHandlers.Http;

namespace RequestHandlers.WebApi.CSharp
{
    class CSharpBuilder : IControllerAssemblyBuilder
    {
        private readonly HashSet<string> _classNames;
        private readonly string _assemblyName;
        private readonly bool _debug;

        public CSharpBuilder(string assemblyName, bool debug = false)
        {
            _debug = debug;
            _classNames = new HashSet<string>();
            _assemblyName = assemblyName;
        }
        
        public Assembly Build(HttpRequestHandlerDefinition[] definitions)
        {
            var references = new AssemblyReferencesHelper()
                .AddReferenceForTypes(typeof(object), typeof(ApiController), typeof(RequestHandlerControllerBuilder))
                .AddReferenceForTypes(definitions.SelectMany(x => new[] { x.Definition.RequestType, x.Definition.ResponseType }).ToArray())
                .GetReferences();

            var compilation = CSharpCompilation.Create(_assemblyName)
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithOptimizationLevel(_debug ? OptimizationLevel.Debug : OptimizationLevel.Release))
                .AddReferences(references);
            var csharpControllers = new List<string>();
            foreach (var temp in definitions)
            {
                var sb = new StringBuilder();
                foreach (var line in CreateCSharp(GetClassName(temp.Definition.RequestType), temp))
                    sb.AppendLine(line);
                var csharp = sb.ToString();
                csharpControllers.Add(csharp);
                if(_debug){
                    var filePath = Path.Combine(Path.GetTempPath(), $"rc_{GetClassName(temp.Definition.RequestType)}.cs");
                    File.WriteAllText(filePath, csharp, Encoding.UTF8);
                    compilation =
                        compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(csharp, path: filePath,
                            encoding: Encoding.UTF8));
                }else{
                compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(csharp));
                }
            }
            
            if (!_debug)
            {
                var assemblyStream = new MemoryStream();
                var result = compilation.Emit(assemblyStream);
                CheckCompilationForErrors(result, csharpControllers);
                assemblyStream.Seek(0, SeekOrigin.Begin);
                return Assembly.Load(assemblyStream.ToArray());
            }
            else
            {
                var assemblyStream = new MemoryStream();
                var pdbStream = new MemoryStream();
                var result = compilation.Emit(assemblyStream, pdbStream);
                CheckCompilationForErrors(result, csharpControllers);
                assemblyStream.Seek(0, SeekOrigin.Begin);
                pdbStream.Seek(0, SeekOrigin.Begin);
                var rawAssembly = assemblyStream.ToArray();
                return Assembly.Load(rawAssembly, pdbStream.ToArray());
            }
        }
        private static void CheckCompilationForErrors(EmitResult result, List<string> codes)
        {
            if (!result.Success)
            {
                var errormsg = new StringBuilder();
                foreach (var diagnostic in result.Diagnostics)
                {
                    errormsg.AppendLine(diagnostic.ToString());
                }
                throw new Exception(string.Join(Environment.NewLine, codes), new Exception(errormsg.ToString()));
            }
        }

        private string GetClassName(Type requestType)
        {
            var name = requestType.Name;
            string className;
            int? addition = null;
            do
            {
                var add = addition.HasValue ? addition.ToString() : "";
                className = $"{name}Handler{add}Controller";
                addition = addition + 1 ?? 2;
            } while (_classNames.Contains(className));
            return className;
        }
        public IEnumerable<string> CreateCSharp(string className, HttpRequestHandlerDefinition builderDefinition)
        {
            yield return "namespace Proxy";
            yield return "{";

            var requestBodyProperties = builderDefinition.Parameters.Where(x => x.BindingType == BindingType.FromBody || x.BindingType == BindingType.FromForm).ToArray();
            var requestClass = builderDefinition.Definition.RequestType.Name + "_" + Guid.NewGuid().ToString().Replace("-", "");
            if (requestBodyProperties.Any())
            {
                yield return $"    public class {requestClass}";
                yield return "    {";
                foreach (var source in requestBodyProperties)
                {
                    yield return $"        public {GetCorrectFormat(source.PropertyInfo.PropertyType)} {source.PropertyInfo.Name} {{ get; set; }}";
                }
                yield return "    }";
            }

            var methodArgs = string.Join(",  ", builderDefinition.Parameters.GroupBy(x => x.PropertyName).Select(x => new
            {
                Name = x.Key,
                Type = x.First().BindingType == BindingType.FromBody || x.First().BindingType == BindingType.FromForm ? requestClass : GetCorrectFormat(x.First().PropertyInfo.PropertyType),
                Binder = x.First().BindingType == BindingType.FromBody ? "FromBody" : "FromUri"
            }).Select(x => $"[System.Web.Http.{x.Binder}Attribute] {x.Type} {x.Name}"));
            var isAsync = builderDefinition.Definition.ResponseType.IsConstructedGenericType &&
                          builderDefinition.Definition.ResponseType.GetGenericTypeDefinition() == typeof(Task<>);
            yield return $"    public class {className} : System.Web.Http.ApiController";
            yield return "    {";
            yield return "        private readonly RequestHandlers.IRequestDispatcher _requestDispatcher;";
            yield return "";
            yield return $"        public {className}(RequestHandlers.IRequestDispatcher requestDispatcher)";
            yield return "        {";
            yield return "            _requestDispatcher = requestDispatcher;";
            yield return "        }";
            yield return $"        [System.Web.Http.Http{builderDefinition.HttpMethod}Attribute, System.Web.Http.RouteAttribute(\"{builderDefinition.Route}\")]";
            yield return $"        public  {(isAsync ? "async " : string.Empty)}{GetCorrectFormat(builderDefinition.Definition.ResponseType)} Handle({methodArgs})";
            yield return "        {";
            var requestVariable = "request_" + Guid.NewGuid().ToString().Replace("-", "");
            yield return $"            var {requestVariable} = new {GetCorrectFormat(builderDefinition.Definition.RequestType)}";
            yield return "            {";
            foreach (var assignment in builderDefinition.Parameters)
            {
                var fromRequest = assignment.BindingType == BindingType.FromBody || assignment.BindingType == BindingType.FromForm;
                yield return $"                {assignment.PropertyInfo.Name} = {assignment.PropertyName}{(fromRequest ? $".{assignment.PropertyInfo.Name}" : "")},";
            }
            yield return "            };";
            yield return $"            var response = {(isAsync ? "await " : string.Empty)}_requestDispatcher.Process<{GetCorrectFormat(builderDefinition.Definition.RequestType)},{GetCorrectFormat(builderDefinition.Definition.ResponseType)}>({requestVariable});";
            yield return "            return response;";
            yield return "        }";
            yield return "    }";
            yield return "}";
        }

        private string GetCorrectFormat(Type type)
        {
            if (type.IsArray)
            {
                return GetCorrectFormat(type.GetElementType()) + "[]";
            }

            if (type.IsConstructedGenericType)
                return string.Format("{0}<{1}>", type.FullName.Split('`')[0], string.Join(", ", type.GetGenericArguments().Select(GetCorrectFormat)));
            else
                return type.FullName;
        }
    }
}
