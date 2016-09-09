﻿using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WebApiToTypeScript.Block;
using WebApiToTypeScript.Config;
using WebApiToTypeScript.Enums;
using WebApiToTypeScript.Interfaces;
using WebApiToTypeScript.Types;
using WebApiToTypeScript.WebApi;

namespace WebApiToTypeScript
{
    public class WebApiToTypeScript : AppDomainIsolatedTask
    {
        private const string IHaveQueryParams = nameof(IHaveQueryParams);
        private const string IEndpoint = nameof(IEndpoint);
        private const string AngularEndpointsService = nameof(AngularEndpointsService);

        private readonly TypeService typeService
            = new TypeService();

        private InterfaceService interfaceService;
        private EnumsService enumsService;

        [Required]
        public string ConfigFilePath { get; set; }

        public Config.Config Config { get; set; }

        public override bool Execute()
        {
            Config = GetConfig(ConfigFilePath);

            typeService.LoadAllTypes(Config.WebApiModuleFileName);

            enumsService = new EnumsService();
            interfaceService = new InterfaceService(Config, typeService, enumsService);

            var apiControllers = typeService.GetControllers(Config.WebApiModuleFileName);

            var endpointBlock = CreateEndpointBlock();

            var serviceBlock = CreateServiceBlock();

            foreach (var apiController in apiControllers)
            {
                var webApiController = new WebApiController(apiController);

                WriteEndpointClassToBlock(endpointBlock, webApiController);
                WriteServiceObjectToBlock(serviceBlock.Children.First() as TypeScriptBlock, webApiController);
            }

            CreateFileForBlock(endpointBlock, Config.EndpointsOutputDirectory, Config.EndpointsFileName);
            CreateFileForBlock(serviceBlock, Config.ServiceOutputDirectory, Config.ServiceFileName);

            var enumsBlock = Config.GenerateEnums
                ? new TypeScriptBlock($"{Config.NamespaceOrModuleName} {Config.EnumsNamespace}")
                : new TypeScriptBlock();

            var interfacesBlock =
                new TypeScriptBlock($"{Config.NamespaceOrModuleName} {Config.InterfacesNamespace}");

            if (Config.GenerateInterfaces)
            {
                interfaceService.WriteInterfacesToBlock(interfacesBlock);

                CreateFileForBlock(interfacesBlock, Config.InterfacesOutputDirectory, Config.InterfacesFileName);
            }

            if (Config.GenerateEnums)
            {
                enumsService.WriteEnumsToBlock(enumsBlock);

                CreateFileForBlock(enumsBlock, Config.EnumsOutputDirectory, Config.EnumsFileName);
            }

            return true;
        }

        private TypeScriptBlock CreateEndpointBlock()
        {
            return new TypeScriptBlock($"{Config.NamespaceOrModuleName} {Config.EndpointsNamespace}")
                .AddAndUseBlock($"export interface {IEndpoint}")
                .AddStatement("verb: string")
                .AddStatement("toString(): string")
                .Parent
                .AddAndUseBlock($"export interface {IHaveQueryParams}")
                .AddStatement("getQueryParams(): Object")
                .Parent;
        }

        private TypeScriptBlock CreateServiceBlock()
        {
            return new TypeScriptBlock($"{Config.NamespaceOrModuleName} {Config.ServiceNamespace}")
                .AddAndUseBlock($"export class {AngularEndpointsService}")
                .AddStatement("static $inject = ['$http'];")
                .AddStatement("static $http: ng.IHttpService;")
                .AddAndUseBlock("constructor($http: ng.IHttpService)")
                .AddStatement($"{AngularEndpointsService}.$http = $http;")
                .Parent
                .AddAndUseBlock("static call(endpoint: IEndpoint, data)")
                .AddAndUseBlock($"return {AngularEndpointsService}.$http(", isFunctionBlock: true, terminationString: ";")
                .AddStatement("method: endpoint.verb,")
                .AddStatement("url: endpoint.toString(),")
                .AddStatement("data: data")
                .Parent
                .Parent
                .Parent;
        }

        private void WriteServiceObjectToBlock(TypeScriptBlock serviceBlock, WebApiController webApiController)
        {
            var controllerBlock = serviceBlock
                .AddAndUseBlock($"public {webApiController.Name} =");

            var actions = webApiController.Actions;

            for (var a = 0; a < actions.Count; a++)
            {
                var action = actions[a];

                for (var v = 0; v < action.Verbs.Count; v++)
                {
                    var verb = action.Verbs[v];

                    var actionName = action.GetActionNameForVerb(verb);

                    var isLastActionAndVerb = a == actions.Count - 1
                        && v == action.Verbs.Count - 1;

                    var constructorParametersList = GetConstructorParametersList(action);
                    var constructorParameterNamesList = GetConstructorParameterNamesList(action);
                    var callArgumentsList = GetCallArgumentsList(action, verb);
                    var callArgumentValues = GetCallArgumentValues(action, verb);

                    var interfaceFullName = $"{Config.EndpointsNamespace}.{webApiController.Name}.I{actionName}";
                    var endpointFullName = $"{Config.EndpointsNamespace}.{webApiController.Name}.{actionName}";

                    controllerBlock
                        .AddAndUseBlock
                        (
                            outer: $"{actionName}: ({constructorParametersList}): {interfaceFullName} =>",
                            isFunctionBlock: false,
                            terminationString: !isLastActionAndVerb ? "," : string.Empty
                        )
                        .AddStatement($"var endpoint = new {endpointFullName}({constructorParameterNamesList});")
                        .AddAndUseBlock("var callHook =")
                        .AddAndUseBlock($"call({callArgumentsList})")
                        .AddStatement($"return {AngularEndpointsService}.call(this, {callArgumentValues});")
                        .Parent
                        .Parent
                        .AddStatement("return _.extend(endpoint, callHook);");
                }
            }
        }

        private string GetCallArgumentValues(WebApiAction action, WebApiHttpVerb verb)
        {
            var isFormBody = verb == WebApiHttpVerb.Post || verb == WebApiHttpVerb.Put;

            var callArgumentValueStrings = action.BodyParameters
                .Select(argument =>
                {
                    var typeScriptType = GetTypeScriptType(argument)
                    .TypeName;

                    var valueFormat = $"{argument.Name}";

                    switch (typeScriptType)
                    {
                        case "string":
                            valueFormat = $"`\"${{{argument.Name}}}\"`";
                            break;
                    }

                    return $"{argument.Name} != null ? {valueFormat} : null";
                })
                .ToList();

            var callArgumentValuesList = string.Join(", ", callArgumentValueStrings);

            return (!isFormBody || string.IsNullOrEmpty(callArgumentValuesList))
                 ? "null"
                 : callArgumentValuesList;
        }

        private string GetCallArgumentsList(WebApiAction action, WebApiHttpVerb verb)
        {
            var isFormBody = verb == WebApiHttpVerb.Post || verb == WebApiHttpVerb.Put;
            if (!isFormBody)
                return string.Empty;

            var callArgumentStrings = action.BodyParameters
                .Select(a => GetParameterString(a, false))
                .ToList();

            var callArgumentsList = string.Join(", ", callArgumentStrings);

            return callArgumentsList;
        }

        private string GetConstructorParameterNamesList(WebApiAction action)
        {
            var constructorParameterNames = GetConstructorParameterMappings(action)
                .Select(p => p.Name);

            return string.Join(", ", constructorParameterNames);
        }

        private string GetConstructorParametersList(WebApiAction action)
        {
            var constructorParameterMappings = GetConstructorParameterMappings(action);

            var constructorParameterStrings = constructorParameterMappings
                .Select(p => p.String);

            var constructorParametersList =
                string.Join(", ", constructorParameterStrings);
            return constructorParametersList;
        }

        private void WriteEndpointClassToBlock(TypeScriptBlock endpointBlock, WebApiController webApiController)
        {
            var controllerBlock = endpointBlock
                .AddAndUseBlock($"export {Config.NamespaceOrModuleName} {webApiController.Name}");

            var actions = webApiController.Actions;

            foreach (var action in actions)
            {
                foreach (var verb in action.Verbs)
                {
                    var actionName = action.GetActionNameForVerb(verb);

                    var interfaceBlock = controllerBlock
                        .AddAndUseBlock($"export interface I{actionName} extends {IEndpoint}");

                    WriteInterfaceToBlock(interfaceBlock, action);

                    var classBlock = controllerBlock
                        .AddAndUseBlock($"export class {actionName} implements {IEndpoint}")
                        .AddStatement($"verb = '{verb.VerbMethod}';");

                    WriteConstructorToBlock(classBlock, action);

                    WriteGetQueryStringToBlock(classBlock, action);

                    WriteToStringToBlock(classBlock, action);
                }
            }
        }

        private void WriteInterfaceToBlock(TypeScriptBlock interfaceBlock, WebApiAction action)
        {
            var constructorParameterMappings = GetConstructorParameterMappings(action);
            foreach (var constructorParameterMapping in constructorParameterMappings)
            {
                interfaceBlock
                    .AddStatement($"{constructorParameterMapping.String};");
            }

            var callArguments = action.BodyParameters;

            var callArgumentStrings = callArguments
                .Select(a => GetParameterString(a, false))
                .ToList();

            var callArgumentsList = string.Join(", ", callArgumentStrings);

            interfaceBlock
                .AddStatement($"call({callArgumentsList});");
        }

        private void CreateCallBlock(TypeScriptBlock classBlock, WebApiAction action,
            WebApiHttpVerb verb)
        {
            var isFormBody = verb == WebApiHttpVerb.Post || verb == WebApiHttpVerb.Put;

            var callArguments = action.BodyParameters;

            var callArgumentStrings = callArguments
                .Select(a => GetParameterString(a, false))
                .ToList();

            var callArgumentsList = string.Join(", ", callArgumentStrings);

            var dataDelimiter = isFormBody && callArgumentStrings.Any() ? "," : string.Empty;

            var callBlock = classBlock
                .AddAndUseBlock($"call = ({callArgumentsList}) =>")
                .AddStatement("var httpService = angular.injector(['ng']).get<ng.IHttpService>('$http');")
                .AddAndUseBlock("return httpService(", isFunctionBlock: true, terminationString: ";")
                .AddStatement($"method: '{verb.VerbMethod}',")
                .AddStatement($"url: this.toString(){dataDelimiter}");

            if (!isFormBody)
                return;

            foreach (var argument in callArguments)
            {
                var typeScriptType = GetTypeScriptType(argument)
                    .TypeName;

                var valueFormat = $"{argument.Name}";

                switch (typeScriptType)
                {
                    case "string":
                        valueFormat = $"`\"${{{argument.Name}}}\"`";
                        break;
                }

                callBlock
                    .AddStatement($"data: {argument.Name} != null ? {valueFormat} : null");
            }
        }

        private void WriteToStringToBlock(TypeScriptBlock classBlock, WebApiAction action)
        {
            var toStringBlock = classBlock
                .AddAndUseBlock("toString = (): string =>");

            var queryString = action.QueryStringParameters.Any()
                ? " + this.getQueryString()"
                : string.Empty;

            toStringBlock
                .AddStatement($"return `{action.Controller.BaseEndpoint}{action.Endpoint}`{queryString};");
        }

        private void WriteGetQueryStringToBlock(TypeScriptBlock classBlock, WebApiAction action)
        {
            var queryStringParameters = action.QueryStringParameters;

            if (!queryStringParameters.Any())
                return;

            var queryStringBlock = classBlock
                .AddAndUseBlock("private getQueryString = (): string =>")
                .AddStatement("var parameters: string[] = [];");

            foreach (var routePart in queryStringParameters)
            {
                var argumentName = routePart.Name;

                var block = queryStringBlock
                    .AddAndUseBlock($"if (this.{argumentName} != null)");

                var argumentType = GetTypeScriptType(routePart);

                if (argumentType.IsPrimitive || argumentType.IsEnum)
                {
                    if (argumentType.IsCollection)
                    {
                        block
                            .AddStatement($"parameters.push(`{argumentName}=${{this.{argumentName}.join(',')}}`);");
                    }
                    else
                    {
                        block
                            .AddStatement($"parameters.push(`{argumentName}=${{encodeURIComponent(this.{argumentName}.toString())}}`);");
                    }
                }
                else
                {
                    block
                        .AddStatement($"var {argumentName}Params = this.{argumentName}.getQueryParams();")
                        .AddAndUseBlock($"Object.keys({argumentName}Params).forEach((key) =>", isFunctionBlock: true, terminationString: ";")
                        .AddAndUseBlock($"if ({argumentName}Params[key] != null)")
                        .AddStatement($"parameters.push(`${{key}}=${{encodeURIComponent({argumentName}Params[key].toString())}}`);");
                }
            }

            queryStringBlock
                .AddAndUseBlock("if (parameters.length > 0)")
                .AddStatement("return '?' + parameters.join('&');")
                .Parent
                .AddStatement("return '';");
        }

        private void WriteConstructorToBlock(TypeScriptBlock classBlock, WebApiAction action)
        {
            var constructorParameterMappings = GetConstructorParameterMappings(action);

            if (!constructorParameterMappings.Any())
                return;

            var constructorParameterStrings = constructorParameterMappings
                .Select(p => $"public {p.String}");

            var constructorParametersList =
                string.Join(", ", constructorParameterStrings);

            var constructorBlock = classBlock
                .AddAndUseBlock($"constructor({constructorParametersList})");

            foreach (var mapping in constructorParameterMappings)
            {
                if (mapping.TypeMapping?.AutoInitialize ?? false)
                {
                    constructorBlock
                        .AddAndUseBlock($"if (this.{mapping.Name} == null)")
                        .AddStatement($"this.{mapping.Name} = new {mapping.TypeMapping.TypeScriptTypeName}();");
                }
            }
        }

        private IOrderedEnumerable<ConstructorParameterMapping> GetConstructorParameterMappings(WebApiAction action)
        {
            var tempConstructorParameters = action.Method.Parameters
                .Select(p => new
                {
                    Parameter = p,
                    RoutePart = action.Controller.RouteParts.SingleOrDefault(brp => brp.ParameterName == p.Name)
                        ?? action.RouteParts.SingleOrDefault(rp => rp.ParameterName == p.Name)
                        ?? action.QueryStringParameters.SingleOrDefault(qsp => qsp.Name == p.Name)
                })
                .Where(cp => cp.RoutePart != null)
                .ToList();

            var constructorParameters = new List<WebApiRoutePart>();
            foreach (var tcp in tempConstructorParameters)
            {
                if (tcp.RoutePart.Parameter == null)
                    tcp.RoutePart.Parameter = tcp.Parameter;

                constructorParameters.Add(tcp.RoutePart);
            }

            var constructorParameterMappings = constructorParameters
                .Select(routePart => new ConstructorParameterMapping
                {
                    IsOptional = IsParameterOptional(routePart.Parameter),
                    TypeMapping = GetTypeMapping(routePart),
                    Name = routePart.Parameter.Name,
                    String = GetParameterString(routePart)
                })
                .OrderBy(p => p.IsOptional);

            return constructorParameterMappings;
        }

        private string GetParameterString(WebApiRoutePart routePart, bool withOptionals = true)
        {
            var parameter = routePart.Parameter;
            var isOptional = withOptionals && IsParameterOptional(parameter);
            var typeScriptType = GetTypeScriptType(routePart);

            var collectionString = typeScriptType.IsCollection ? "[]" : string.Empty;

            return $"{parameter.Name}{(isOptional ? "?" : "")}: {typeScriptType.TypeName}{collectionString}";
        }

        private bool IsParameterOptional(ParameterDefinition parameter)
        {
            return parameter.IsOptional || !parameter.ParameterType.IsValueType || IsNullable(parameter.ParameterType);
        }

        private TypeScriptType GetTypeScriptType(WebApiRoutePart routePart)
        {
            var result = new TypeScriptType();

            var parameter = routePart.Parameter;
            var type = parameter.ParameterType;
            var typeName = type.FullName;

            var typeMapping = GetTypeMapping(routePart);

            if (typeMapping != null)
            {
                var tsTypeName = typeMapping.TypeScriptTypeName;
                result.TypeName = tsTypeName;
                result.IsPrimitive = typeService.IsPrimitiveTypeScriptType(result.TypeName);
                result.IsEnum = tsTypeName.StartsWith($"{Config.EnumsNamespace}")
                    || result.IsPrimitive;

                return result;
            }

            typeName = typeService.StripNullable(type) ?? typeName;

            var collectionType = typeService.StripCollection(type);
            result.IsCollection = collectionType != null;
            typeName = collectionType ?? typeName;

            var typeDefinition = typeService.GetTypeDefinition(typeName);

            if (typeDefinition?.IsEnum ?? false)
            {
                if (!Config.GenerateEnums)
                {
                    result.TypeName = "number";
                    result.IsPrimitive = true;
                }
                else
                {
                    enumsService.AddEnum(typeDefinition);

                    result.TypeName = $"{Config.EnumsNamespace}.{typeDefinition.Name}";
                    result.IsPrimitive = false;
                }

                result.IsEnum = true;
                return result;
            }

            var primitiveType = typeService.GetPrimitiveTypeScriptType(typeName);

            if (!string.IsNullOrEmpty(primitiveType))
            {
                result.TypeName = primitiveType;
                result.IsPrimitive = true;

                return result;
            }

            if (!typeDefinition?.IsValueType ?? false)
            {
                if (!Config.GenerateInterfaces)
                {
                    result.TypeName = $"{IHaveQueryParams}";
                }
                else
                {
                    interfaceService.AddInterfaceNode(typeDefinition);

                    result.TypeName = $"{Config.InterfacesNamespace}.{typeDefinition.Name}";
                }

                return result;
            }

            throw new NotSupportedException("Maybe it is a generic class, or a yet unsupported collection, or chain thereof?");
        }

        private TypeMapping GetTypeMapping(WebApiRoutePart routePart)
        {
            if (routePart.Parameter == null)
                return null;

            var parameter = routePart.Parameter;
            var typeName = parameter.ParameterType.FullName;

            var typeMapping = Config.TypeMappings
                .SingleOrDefault(t => typeName.StartsWith(t.WebApiTypeName)
                    || (t.TreatAsAttribute
                        && (Helpers.HasCustomAttribute(parameter, $"{t.WebApiTypeName}Attribute"))
                    || (t.TreatAsConstraint
                        && routePart.Constraints.Any(c => c == Helpers.ToCamelCase(t.WebApiTypeName)))));

            return typeMapping;
        }

        private bool IsNullable(TypeReference type)
        {
            var genericType = type as GenericInstanceType;
            return genericType != null
                   && genericType.FullName.StartsWith("System.Nullable`1");
        }

        private Config.Config GetConfig(string configFilePath)
        {
            var configFileContent = File.ReadAllText(configFilePath);

            return JsonConvert.DeserializeObject<Config.Config>(configFileContent);
        }

        private void CreateFileForBlock(TypeScriptBlock typeScriptBlock, string outputDirectory, string fileName)
        {
            CreateOuputDirectory(outputDirectory);

            var filePath = Path.Combine(outputDirectory, fileName);

            using (var endpointFileWriter = new StreamWriter(filePath, false))
            {
                endpointFileWriter.Write(typeScriptBlock.ToString());
            }

            LogMessage($"{filePath} created!");
        }

        private void CreateOuputDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                LogMessage($"{directory} created!");
            }
            else
            {
                LogMessage($"{directory} already exists!");
            }
        }

        private void LogMessage(string log)
        {
            try
            {
                Log.LogMessage(log);
            }
            catch (Exception)
            {
                Console.WriteLine(log);
            }
        }
    }
}