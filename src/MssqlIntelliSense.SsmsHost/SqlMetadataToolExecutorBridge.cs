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
        DatabaseMetadata metadata)
    {
        var executorType = typeof(SqlMetadataToolExecutor);
        var methods = executorType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(SqlMetadataToolExecutor.ExecuteToolAsync))
            .ToList();

        var threeParameterMethod = methods.FirstOrDefault(method => method.GetParameters().Length == 3);
        if (threeParameterMethod != null)
        {
            return await AwaitStringTaskAsync(threeParameterMethod.Invoke(null, new object[] { toolName, arguments, metadata }));
        }

        var fourParameterMethod = methods.FirstOrDefault(method => method.GetParameters().Length == 4);
        if (fourParameterMethod != null)
        {
            return await AwaitStringTaskAsync(fourParameterMethod.Invoke(null, new object?[] { toolName, arguments, metadata, null }));
        }

        var assembly = executorType.Assembly;
        throw new MissingMethodException(
            $"{executorType.FullName}.ExecuteToolAsync not found in {assembly.FullName} loaded from {assembly.Location}");
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
