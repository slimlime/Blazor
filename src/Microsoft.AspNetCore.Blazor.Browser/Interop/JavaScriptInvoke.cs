using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Blazor.Browser.Interop
{
    internal class JavaScriptInvoke
    {
        private static int NextFunction = 0;
        private static readonly ConcurrentDictionary<string, string> ResolvedFunctionRegistrations = new ConcurrentDictionary<string, string>();
        private static readonly IDictionary<string, object> ResolvedFunctions = new Dictionary<string, object>();

        private const string InvokePromiseCallback = "invokePromiseCallback";

        public static string FindDotNetMethod(string methodOptions)
        {
            var result = ResolvedFunctionRegistrations.GetOrAdd(methodOptions, opts =>
            {
                var options = JsonUtil.Deserialize<MethodOptions>(methodOptions);
                var argumentDeserializer = GetOrCreateArgumentDeserializer(options);
                var invoker = GetOrCreateInvoker(options, argumentDeserializer);

                var invokerRegistration = NextFunction.ToString();
                NextFunction++;
                if (ResolvedFunctions.ContainsKey(invokerRegistration))
                {
                    throw new InvalidOperationException($"A function with registration '{invokerRegistration}' was already registered");
                }
                else
                {
                    ResolvedFunctions[invokerRegistration] = invoker;
                }

                return invokerRegistration;
            });

            return result;
        }

        public static string InvokeDotNetMethod(string registration, string callbackId, string methodArguments)
        {
            // We invoke the dotnet method and wrap either the result or the exception produced by
            // an error into an invocation result type. This invocation result is just a discriminated
            // union with either success or failure.
            try
            {
                return InvocationResult<object>.Success(InvokeDotNetMethodCore(registration, callbackId, methodArguments));
            }
            catch (Exception e)
            {
                var exception = e;
                while (exception.InnerException != null)
                {
                    exception = exception.InnerException;
                }

                return InvocationResult<object>.Fail(exception);
            }
        }

        private static object InvokeDotNetMethodCore(string registration, string callbackId, string methodArguments)
        {
            if (!ResolvedFunctions.TryGetValue(registration, out var registeredFunction))
            {
                throw new InvalidOperationException($"No method exists with registration number '{registration}'.");
            }

            if (!(registeredFunction is Func<string, object> invoker))
            {
                throw new InvalidOperationException($"Produced invoker has the wrong signature!");
            }

            var result = invoker(methodArguments);
            if (callbackId != null && !(result is Task))
            {
                var methodSpec = ResolvedFunctionRegistrations.Single(kvp => kvp.Value == registration);
                var options = JsonUtil.Deserialize<MethodOptions>(methodSpec.Key);
                throw new InvalidOperationException($"'{options.Method.Name}' in '{options.Type.Name}' must return a Task.");
            }

            if (result is Task && callbackId == null)
            {
                var methodSpec = ResolvedFunctionRegistrations.Single(kvp => kvp.Value == registration);
                var options = JsonUtil.Deserialize<MethodOptions>(methodSpec.Key);
                throw new InvalidOperationException($"'{options.Method.Name}' in '{options.Type.Name}' must not return a Task.");
            }

            if (result is Task taskResult)
            {
                // For async work, we just setup the callback on the returned task to invoke the appropiate callback in JavaScript.
                SetupResultCallback(callbackId, taskResult);

                // We just return null here as the proper result will be returned through invoking a JavaScript callback when the
                // task completes.
                return null;
            }
            else
            {
                return result;
            }
        }

        private static void SetupResultCallback(string callbackId, Task taskResult)
        {
            taskResult.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    if (task.GetType() == typeof(Task))
                    {
                        RegisteredFunction.Invoke<bool>(
                            InvokePromiseCallback,
                            callbackId,
                            new InvocationResult<object> { Succeeded = true, Result = null });
                    }
                    else
                    {
                        var resultProperty = task.GetType()
                                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                .SingleOrDefault(m => m.Name == "ResultOnSuccess") ??
                            task.GetType()
                                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                .SingleOrDefault(m => m.Name == "Result");

                        var returnValue = resultProperty.GetValue(task);
                        RegisteredFunction.Invoke<bool>(
                            InvokePromiseCallback,
                            callbackId,
                            new InvocationResult<object> { Succeeded = true, Result = returnValue });
                    }
                }
                else
                {
                    Exception exception = task.Exception;
                    while (exception.InnerException != null)
                    {
                        exception = exception.InnerException;
                    }

                    RegisteredFunction.Invoke<bool>(
                        InvokePromiseCallback,
                        callbackId,
                        new InvocationResult<object> { Succeeded = false, Message = exception.Message });
                }
            });
        }

        private static Func<string, object> GetOrCreateInvoker(MethodOptions options, Func<string, object[]> argumentDeserializer)
        {
            var method = options.GetMethodOrThrow();
            return (string args) => method.Invoke(null, argumentDeserializer(args));
        }

        private static Func<string, object[]> GetOrCreateArgumentDeserializer(MethodOptions options)
        {
            var info = options.GetMethodOrThrow();
            var argsClass = ArgumentList.GetArgumentClass(info.GetParameters().Select(p => p.ParameterType).ToArray());
            var deserializeMethod = ArgumentList.GetDeserializer(argsClass);
            var toParameterListMethod = argsClass.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => string.Equals(nameof(ArgumentList.ToParameterList), m.Name))
                .Single();

            return Deserialize;

            object[] Deserialize(string arguments)
            {
                var argsInstance = deserializeMethod(arguments);
                return argsInstance.ToParameterList();
            }
        }
    }
}