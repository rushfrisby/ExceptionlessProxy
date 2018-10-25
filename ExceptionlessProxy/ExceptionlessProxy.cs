using System;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using Exceptionless;
using System.Collections.Generic;
using Newtonsoft.Json;
using Exceptionless.Logging;

namespace ExceptionlessProxy
{
	public class ExceptionlessProxy<T> : DispatchProxy where T : class
	{
		private T _decorated;
		private ExceptionlessClient _exceptionlessClient;
		private bool _logMethodCalls;
		private static readonly JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings
		{
			ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
			Formatting = Formatting.Indented,
			PreserveReferencesHandling = PreserveReferencesHandling.Objects
		};

		protected override object Invoke(MethodInfo targetMethod, object[] args)
		{
			if (targetMethod == null)
			{
				throw new ArgumentException(nameof(targetMethod));
			}

			var correlationId = Guid.NewGuid().ToString();

			if (_logMethodCalls)
			{
				try
				{
					LogBefore(targetMethod, args, correlationId);
				}
				catch (Exception ex)
				{
					//Do not stop method execution if exception
					LogException(ex, correlationId);
				}
			}

			object result = null;

			try
			{
				result = targetMethod.Invoke(_decorated, args);
				var resultTask = result as Task;

				if (resultTask != null)
				{
					resultTask.ContinueWith(task =>
					{
						if (task.Exception != null)
						{
							LogException(task.Exception.InnerException ?? task.Exception, correlationId);
						}
						else
						{
							object taskResult = null;

							if (task.GetType().GetTypeInfo().IsGenericType && task.GetType().GetGenericTypeDefinition() == typeof(Task<>))
							{
								var property = task.GetType().GetTypeInfo().GetProperties().FirstOrDefault(p => p.Name == "Result");
								if (property != null)
								{
									taskResult = property.GetValue(task);
								}
							}
							if (_logMethodCalls)
							{
								try
								{
									LogAfter(targetMethod, args, taskResult, correlationId);
								}
								catch (Exception ex)
								{
									LogException(ex, correlationId);
								}
							}
						}
					});
				}
				else
				{
					if (_logMethodCalls)
					{
						try
						{
							LogAfter(targetMethod, args, result, correlationId);
						}
						catch (Exception ex)
						{
							//Do not stop method execution if exception
							LogException(ex, correlationId);
						}
					}
				}
			}
			catch (Exception ex)
			{
				if (ex is TargetInvocationException)
				{
					LogException(ex.InnerException ?? ex, correlationId);
					throw ex.InnerException ?? ex;
				}
			}

			return result;
		}

		public static T Create(T decorated, ExceptionlessClient client, bool logMethodCalls = false)
		{
			object proxy = Create<T, ExceptionlessProxy<T>>();
			((ExceptionlessProxy<T>)proxy).SetParameters(decorated, client, logMethodCalls);
			return (T)proxy;
		}

		private void SetParameters(T decorated, ExceptionlessClient exceptionlessClient, bool logMethodCalls)
		{
			_decorated = decorated ?? throw new ArgumentNullException(nameof(decorated));
			_exceptionlessClient = exceptionlessClient ?? throw new ArgumentNullException(nameof(exceptionlessClient));
			_logMethodCalls = logMethodCalls;
		}

		private string GetStringValue(object obj)
		{
			if (obj == null)
			{
				return "null";
			}

			if (obj.GetType().GetTypeInfo().IsPrimitive || obj.GetType().GetTypeInfo().IsEnum || obj is string)
			{
				return obj.ToString();
			}

			try
			{
				return JsonConvert.SerializeObject(obj);
			}
			catch
			{
				return obj.ToString();
			}
		}

		private void LogException(Exception exception, string correlationId)
		{
			try
			{
				exception.ToExceptionless().AddObject(new
				{
					CorrelationId = correlationId
				}).Submit();
			}
			catch
			{
				// ignored
				//Method should return original exception
			}
		}

		private void LogAfter(MethodInfo methodInfo, object[] args, object result, string correlationId)
		{
			var dic = new Dictionary<string, string>();
			var parameters = methodInfo.GetParameters();
			if (parameters.Any())
			{
				for (var i = 0; i < parameters.Length; i++)
				{
					var parameter = parameters[i];
					var arg = args[i];
					dic.Add(parameter.Name, GetStringValue(arg));
				}
			}

			var data = new
			{
				Class = _decorated.GetType().FullName,
				Method = methodInfo.Name,
				Result = GetStringValue(result),
				Parameters = dic,
				CorrelationId = correlationId
			};

			_exceptionlessClient.CreateLog($"Method completed: {data.Class}.{data.Method}", LogLevel.Debug).AddObject(data).Submit();
		}

		private void LogBefore(MethodInfo methodInfo, object[] args, string correlationId)
		{
			var dic = new Dictionary<string, string>();
			var parameters = methodInfo.GetParameters();
			if (parameters.Any())
			{
				for (var i = 0; i < parameters.Length; i++)
				{
					var parameter = parameters[i];
					var arg = args[i];
					dic.Add(parameter.Name, GetStringValue(arg));
				}
			}

			var data = new
			{
				Class = _decorated.GetType().FullName,
				Method = methodInfo.Name,
				Parameters = dic,
				CorrelationId = correlationId
			};

			_exceptionlessClient.CreateLog($"Method called: {data.Class}.{data.Method}", LogLevel.Debug).AddObject(data).Submit();
		}
	}
}
