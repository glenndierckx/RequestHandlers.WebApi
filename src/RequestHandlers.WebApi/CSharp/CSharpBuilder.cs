using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using RequestHandlers.Http;

namespace RequestHandlers.WebApi.CSharp
{
    public class CSharpBuilder : IControllerAssemblyBuilder
    {
        public class Options
        {
            public string RoutePrefix { get; set; }        
        }
        private readonly HashSet<string> _classNames;
        private readonly string _assemblyName;
        private readonly bool _debug;
        private readonly Options _options;

        public CSharpBuilder(string assemblyName, bool debug = false, Options options = null)
        {
            _debug = debug;
            _options = options;
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
            var operationResults = definitions.Select(temp => CreateCSharp(GetClassName(temp.Definition.RequestType), temp)).ToArray();
            var files = new Dictionary<string, string>();
            files.Add("ProxyController", $@"namespace Proxy
{{{CodeStr.If(!string.IsNullOrEmpty(_options.RoutePrefix), $@"
    [{GetCorrectFormat(typeof(RoutePrefixAttribute))}(""{_options.RoutePrefix}"")]")}
    public class ProxyController : {GetCorrectFormat(typeof(ApiController))}
    {{
        private readonly {GetCorrectFormat(typeof(IWebApiRequestProcessor))} _requestProcessor;
        public ProxyController({GetCorrectFormat(typeof(IWebApiRequestProcessor))} requestProcessor)
        {{
            _requestProcessor = requestProcessor;
        }}

    {CodeStr.Foreach(operationResults.SelectMany(x => x.Operation.Split(new [] {Environment.NewLine}, StringSplitOptions.None)), operation => $@"
        {operation}")}
    }}
}}");

            foreach (var operationResult in operationResults)
            {
                files.Add(operationResult.OperationName, operationResult.RequestClass);
            }

            foreach (var temp in files)
            {
                if(_debug){
                    var filePath = Path.Combine(Path.GetTempPath(), $"rc_{temp.Key}.cs");
                    File.WriteAllText(filePath, temp.Value, Encoding.UTF8);
                    compilation =
                        compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(temp.Value, path: filePath,
                            encoding: Encoding.UTF8));
                }else{
                compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(temp.Value));
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
                className = $"{name}Handler{add}";
                addition = addition + 1 ?? 2;
            } while (_classNames.Contains(className));
            _classNames.Add(className);
            return className;
        }
        public OperationResult CreateCSharp(string operationName, HttpRequestHandlerDefinition builderDefinition)
        {
            var requestBodyProperties = builderDefinition.Parameters.Where(x => x.BindingType == BindingType.FromBody || x.BindingType == BindingType.FromForm).ToArray();
            var requestClass = builderDefinition.Definition.RequestType.Name + "_" + Guid.NewGuid().ToString().Replace("-", "");


            var methodArgs = string.Join(",  ", builderDefinition.Parameters.GroupBy(x => x.PropertyName).Select(x => new
            {
                Name = x.Key,
                Type = x.First().BindingType == BindingType.FromBody || x.First().BindingType == BindingType.FromForm ? requestClass : GetCorrectFormat(x.First().PropertyInfo.PropertyType),
                Binder = x.First().BindingType == BindingType.FromBody ? "FromBody" : "FromUri"
            }).Select(x => $"[System.Web.Http.{x.Binder}Attribute] {x.Type} {x.Name}"));
            var isAsync = builderDefinition.Definition.ResponseType.IsConstructedGenericType &&
                          builderDefinition.Definition.ResponseType.GetGenericTypeDefinition() == typeof(Task<>);
            var responseType = isAsync 
                ? builderDefinition.Definition.ResponseType.GetGenericArguments()[0]
                : builderDefinition.Definition.ResponseType;
            var requestVariable = "request_" + Guid.NewGuid().ToString().Replace("-", "");



            var operationResult = new OperationResult();
            operationResult.OperationName = operationName;
            operationResult.RequestClass = $@"public class {requestClass}
{{{CodeStr.Foreach(requestBodyProperties, source => $@"
    public {GetCorrectFormat(source.PropertyInfo.PropertyType)} {source.PropertyInfo.Name} {{ get; set; }}")}
}}";
            operationResult.Operation = $@"[System.Web.Http.Http{builderDefinition.HttpMethod}Attribute, {GetCorrectFormat(typeof(RouteAttribute))}(""{ builderDefinition.Route}""), {GetCorrectFormat(typeof(ResponseTypeAttribute))}(typeof({GetCorrectFormat(responseType)}))]
public  {(isAsync ? "async " : string.Empty)}{GetCorrectFormat(isAsync ? typeof(Task<IHttpActionResult>) : typeof(IHttpActionResult))} {operationName}({methodArgs})
{{
    var {requestVariable} = new {GetCorrectFormat(builderDefinition.Definition.RequestType)}
    {{{CodeStr.Foreach(builderDefinition.Parameters, assignment => $@"
        {assignment.PropertyInfo.Name} = {assignment.PropertyName}{(assignment.BindingType == BindingType.FromBody || assignment.BindingType == BindingType.FromForm ? $".{assignment.PropertyInfo.Name}" : "")},").Trim(',')}
    }};

    var response = {(isAsync ? "await " : string.Empty)}_requestProcessor.Process{(isAsync ? "Async" : string.Empty)}<{GetCorrectFormat(builderDefinition.Definition.RequestType)},{GetCorrectFormat(responseType)}>({requestVariable}, this);
    return response;
}}";
            return operationResult;
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

    public class OperationResult
    {
        public string OperationName { get; set; }
        public string RequestClass { get; set; }
        public string Operation { get; set; }
    }
    static class CodeStr
    {
        public static string Foreach<T>(IEnumerable<T> source, Func<T, string> format)
        {
            var sb = new StringBuilder();
            foreach (var item in source)
            {
                sb.Append(format(item));
            }
            return sb.ToString();
        }

        public static string If(bool value, string ifTrue, string ifFalse = "") => value ? ifTrue : ifFalse;
        public static string Wrap(Func<string> action) => action();
    }
}
