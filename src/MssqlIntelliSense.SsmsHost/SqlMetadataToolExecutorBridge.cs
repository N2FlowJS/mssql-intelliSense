using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MssqlIntelliSense.Core.Ai;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

internal static class SqlMetadataToolExecutorBridge
{
    public static async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        DatabaseMetadata metadata,
        Func<string, Task<string>>? sqlExecutor = null)
    {
        var argumentsJson = arguments.GetRawText();
        if (string.Equals(toolName, "execute", StringComparison.OrdinalIgnoreCase) && sqlExecutor != null)
        {
            var query = GetStringArgument(argumentsJson, "query", string.Empty);
            return await sqlExecutor(query);
        }

        var executorType = typeof(SqlMetadataToolExecutor);
        var methods = executorType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(SqlMetadataToolExecutor.ExecuteToolAsync))
            .ToList();

        var stringArgumentsMethod = methods.FirstOrDefault(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == 3 && parameters[1].ParameterType == typeof(string);
        });
        if (stringArgumentsMethod != null)
        {
            return await AwaitStringTaskAsync(stringArgumentsMethod.Invoke(null, new object[] { toolName, argumentsJson, metadata }));
        }

        var jsonElementMethod = methods.FirstOrDefault(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == 3 &&
                parameters[1].ParameterType.FullName == "System.Text.Json.JsonElement";
        });
        if (jsonElementMethod != null)
        {
            var compatibleArguments = ConvertJsonElementArgument(jsonElementMethod.GetParameters()[1].ParameterType, argumentsJson);
            return await AwaitStringTaskAsync(jsonElementMethod.Invoke(null, new object[] { toolName, compatibleArguments, metadata }));
        }

        var fourParameterMethod = methods.FirstOrDefault(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == 4 &&
                parameters[1].ParameterType.FullName == "System.Text.Json.JsonElement";
        });
        if (fourParameterMethod != null)
        {
            var compatibleArguments = ConvertJsonElementArgument(fourParameterMethod.GetParameters()[1].ParameterType, argumentsJson);
            return await AwaitStringTaskAsync(fourParameterMethod.Invoke(null, new object?[] { toolName, compatibleArguments, metadata, null }));
        }

        var assembly = executorType.Assembly;
        throw new MissingMethodException(
            $"{executorType.FullName}.ExecuteToolAsync not found in {assembly.FullName} loaded from {assembly.Location}");
    }

    private static object ConvertJsonElementArgument(Type jsonElementType, string argumentsJson)
    {
        if (jsonElementType == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.Clone();
        }

        var jsonDocumentType = jsonElementType.Assembly.GetType("System.Text.Json.JsonDocument")
            ?? throw new InvalidOperationException("Unable to resolve compatible System.Text.Json.JsonDocument.");
        var parseMethod = jsonDocumentType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "Parse" &&
                    parameters.Length is 1 or 2 &&
                    parameters[0].ParameterType == typeof(string);
            });
        var parseParameters = parseMethod.GetParameters();
        var parseArguments = parseParameters.Length == 1
            ? new object[] { string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson }
            : new[] { string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson, Activator.CreateInstance(parseParameters[1].ParameterType)! };
        var parsedDocument = parseMethod.Invoke(null, parseArguments);
        try
        {
            var rootElement = jsonDocumentType.GetProperty("RootElement", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(parsedDocument);
            var cloneMethod = jsonElementType.GetMethod("Clone", BindingFlags.Public | BindingFlags.Instance);
            return cloneMethod?.Invoke(rootElement, null) ?? rootElement!;
        }
        finally
        {
            (parsedDocument as IDisposable)?.Dispose();
        }
    }

    private static string GetStringArgument(string argumentsJson, string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? fallback;
            }
        }
        catch
        {
            return fallback;
        }

        return fallback;
    }

    private static async Task<string> AwaitStringTaskAsync(object? invocationResult)
    {
        if (invocationResult is Task<string> stringTask)
        {
            return await stringTask;
        }

        if (invocationResult is Task task)
        {
            await task;
            var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return resultProperty?.GetValue(task)?.ToString() ?? string.Empty;
        }

        return invocationResult?.ToString() ?? string.Empty;
    }
}
