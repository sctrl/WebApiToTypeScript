﻿using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WebApiToTypeScript
{
    public class WebApiToTypeScript : AppDomainIsolatedTask
    {
        private const string IHaveQueryParams = nameof(IHaveQueryParams);

        [Required]
        public string ConfigFilePath { get; set; }

        public Config Config { get; set; }

        private List<TypeDefinition> Types { get; }
            = new List<TypeDefinition>();

        private List<TypeDefinition> Enums { get; set; }
            = new List<TypeDefinition>();

        public override bool Execute()
        {
            Config = GetConfig(ConfigFilePath);

            CreateOuputDirectory();

            var webApiApplicationModule = ModuleDefinition
                .ReadModule(Config.WebApiModuleFileName);

            AddAllTypes(webApiApplicationModule);

            var apiControllers = webApiApplicationModule.GetTypes()
                .Where(IsControllerType());

            var moduleOrNamespace = Config.WriteNamespaceAsModule ? "module" : "namespace";

            var endpointBlock = new TypeScriptBlock($"{moduleOrNamespace} {Config.EndpointsNamespace}")
                .AddAndUseBlock($"export interface {IHaveQueryParams}")
                .AddStatement("getQueryParams(): Object")
                .Parent;

            foreach (var apiController in apiControllers)
                WriteEndpointClass(endpointBlock, apiController);

            CreateFileForBlock(endpointBlock, Config.EndpointsOutputDirectory, Config.EndpointsFileName);

            if (Config.GenerateEnums)
            {
                var enumsBlock = new TypeScriptBlock($"{moduleOrNamespace} {Config.EnumsNamespace}");

                foreach (var typeDefinition in Enums)
                    CreateEnumForType(enumsBlock, typeDefinition);

                CreateFileForBlock(enumsBlock, Config.EnumsOutputDirectory, Config.EnumsFileName);
            }

            return true;
        }

        private void AddAllTypes(ModuleDefinition webApiApplicationModule)
        {
            Types.AddRange(webApiApplicationModule.GetTypes());

            var moduleDirectoryName = Path.GetDirectoryName(Config.WebApiModuleFileName);

            foreach (var reference in webApiApplicationModule.AssemblyReferences)
            {
                var fileName = $"{reference.Name}.dll";
                var path = Path.Combine(moduleDirectoryName, fileName);

                if (!File.Exists(path))
                    continue;

                var moduleDefinition = ModuleDefinition.ReadModule(path);
                Types.AddRange(moduleDefinition.GetTypes());
            }
        }

        private void WriteEndpointClass(TypeScriptBlock endpointBlock,
            TypeDefinition apiController)
        {
            var webApiController = new WebApiController(apiController);

            var moduleOrNamespace = Config.WriteNamespaceAsModule ? "module" : "namespace";

            var moduleBlock = endpointBlock
                .AddAndUseBlock($"export {moduleOrNamespace} {webApiController.Name}");

            var actions = webApiController.Actions;

            foreach (var action in actions)
            {
                var classBlock = moduleBlock
                     .AddAndUseBlock($"export class {action.Name}")
                     .AddStatement($"verb: string = '{action.Verb}';");

                CreateConstructorBlock(classBlock, webApiController.RouteParts, action);

                CreateQueryStringBlock(classBlock, action);

                CreateToStringBlock(classBlock, webApiController.BaseEndpoint, action);
            }
        }

        private void CreateToStringBlock(TypeScriptBlock classBlock,
            string baseEndpoint, WebApiAction action)
        {
            var toStringBlock = classBlock
                .AddAndUseBlock("toString = (): string =>");

            var queryString = action.QueryStringParameters.Any()
                ? " + this.getQueryString()"
                : string.Empty;

            toStringBlock
                .AddStatement($"return `{baseEndpoint}{action.Endpoint}`{queryString};");
        }

        private void CreateQueryStringBlock(TypeScriptBlock classBlock, WebApiAction action)
        {
            var baseTypeScriptTypes = new[] { "string", "number", "boolean" };

            var queryStringParameters = action.QueryStringParameters;

            if (!queryStringParameters.Any())
                return;

            var queryStringBlock = classBlock
                .AddAndUseBlock("private getQueryString = (): string =>")
                .AddStatement("let parameters: string[] = [];")
                .AddNewLine();

            foreach (var parameter in queryStringParameters)
            {
                var isOptional = parameter.IsOptional;
                var parameterName = parameter.Name;

                var block = !isOptional
                    ? queryStringBlock
                    : queryStringBlock
                        .AddAndUseBlock($"if (this.{parameterName} != null)");

                if (parameter.HasCustomAttributes
                    && parameter.CustomAttributes.Any(a => a.AttributeType.Name == "FromUriAttribute")
                    && !baseTypeScriptTypes.Contains(GetTypeScriptType(parameter)))
                {
                    block
                        .AddStatement($"let {parameterName}Params = this.{parameterName}.getQueryParams();")
                        .AddAndUseBlock($"Object.keys({parameterName}Params).forEach((key) =>", isFunctionBlock: true)
                        .AddStatement($"parameters.push(`${{key}}=${{{parameterName}Params[key]}}`);");
                }
                else
                {
                    block
                        .AddStatement($"parameters.push(`{parameterName}=${{this.{parameterName}}}`);");
                }
            }

            queryStringBlock
                .AddAndUseBlock("if (parameters.length > 0)")
                .AddStatement("return '?' + parameters.join('&');")
                .Parent
                .AddStatement("return '';");
        }

        private void CreateConstructorBlock(TypeScriptBlock classBlock,
            List<WebApiRoutePart> baseRouteParts, WebApiAction action)
        {
            var constructorParameters = action.Method.Parameters
                .Where(p => baseRouteParts.Any(brp => brp.ParameterName == p.Name)
                    || action.RouteParts.Any(rp => rp.ParameterName == p.Name)
                    || action.QueryStringParameters.Any(qsp => qsp.Name == p.Name))
                .ToList();

            if (!constructorParameters.Any())
                return;

            var constructorParameterStrings = constructorParameters
                .Select(GetParameterStrings(true))
                .Select(p => $"public {p}");

            var constructorParametersList =
                string.Join(", ", constructorParameterStrings);

            classBlock
                .AddAndUseBlock($"constructor({constructorParametersList})");
        }

        private Func<ParameterDefinition, string> GetParameterStrings(
            bool processOptional = false)
        {
            return p => $"{p.Name}{(processOptional && p.IsOptional ? "?" : "")}: {GetTypeScriptType(p)}";
        }

        private string GetTypeScriptType(ParameterDefinition parameter)
        {
            var type = parameter.ParameterType;
            var typeName = type.FullName;

            var typeMapping = Config.TypeMappings
                .SingleOrDefault(t => typeName.StartsWith(t.WebApiTypeName)
                    || (t.TreatAsAttribute
                        && parameter.HasCustomAttributes
                        && parameter.CustomAttributes.Any(a => a.AttributeType.Name == t.WebApiTypeName)));

            if (typeMapping != null)
                return typeMapping.TypeScriptTypeName;

            var typeDefinition = Types
                .FirstOrDefault(t => t.FullName == typeName);

            if (typeDefinition?.IsEnum ?? false)
            {
                if (!Config.GenerateEnums)
                    return "number";

                if (Enums.All(e => e.FullName != typeDefinition.FullName))
                    Enums.Add(typeDefinition);

                return $"{Config.EnumsNamespace}.{typeDefinition.Name}";
            }

            var genericType = type as GenericInstanceType;
            if (genericType != null
                && genericType.FullName.StartsWith("System.Nullable`1")
                && genericType.HasGenericArguments
                && genericType.GenericArguments.Count == 1)
            {
                typeName = genericType.GenericArguments.Single().FullName;
            }

            switch (typeName)
            {
                case "System.String":
                    return "string";

                case "System.Int32":
                    return "number";

                case "System.Boolean":
                    return "boolean";

                default:
                    if (Config.GenerateInterfaces)
                    {
                    }

                    return $"{IHaveQueryParams}"
            ;
            }
        }

        private static void CreateEnumForType(TypeScriptBlock enumsBlock, TypeDefinition typeDefinition)
        {
            var fields = typeDefinition.Fields
                .Where(f => f.HasConstant && !f.IsSpecialName);

            var enumBlock = enumsBlock
                .AddAndUseBlock($"export enum {typeDefinition.Name}");

            foreach (var field in fields)
                enumBlock.AddStatement($"{field.Name} = {field.Constant},");
        }

        private Config GetConfig(string configFilePath)
        {
            var configFileContent = File.ReadAllText(configFilePath);

            return JsonConvert.DeserializeObject<Config>(configFileContent);
        }

        private Func<TypeDefinition, bool> IsControllerType()
        {
            var apiControllerType = "System.Web.Http.ApiController";

            return t => t.IsClass
                && !t.IsAbstract
                && t.Name.EndsWith("Controller")
                && GetBaseTypes(t).Any(bt => bt.FullName == apiControllerType);
        }

        private IEnumerable<TypeReference> GetBaseTypes(TypeDefinition type)
        {
            var baseType = type.BaseType;
            while (baseType != null)
            {
                yield return baseType;

                var baseTypeDefinition = baseType as TypeDefinition;
                baseType = baseTypeDefinition?.BaseType;
            }
        }

        private void CreateFileForBlock(TypeScriptBlock typeScriptBlock, string outputDirectory, string fileName)
        {
            var filePath = Path.Combine(outputDirectory, fileName);
            using (var endpointFileWriter = new StreamWriter(filePath, false))
            {
                endpointFileWriter.Write(typeScriptBlock.ToString());
            }

            LogMessage($"{filePath} created!");
        }

        private void CreateOuputDirectory()
        {
            if (!Directory.Exists(Config.EndpointsOutputDirectory))
            {
                Directory.CreateDirectory(Config.EndpointsOutputDirectory);
                LogMessage($"{Config.EndpointsOutputDirectory} created!");
            }
            else
            {
                LogMessage($"{Config.EndpointsOutputDirectory} already exists!");
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