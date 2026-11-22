using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Logging;

namespace WalletWasabi.Services;

public class HostedServices : IDisposable
{
	private volatile bool _disposedValue = false; // To detect redundant calls

	private readonly List<HostedService> _services = new();

	private readonly Lock _servicesLock = new();
	private bool IsStartAllAsyncStarted { get; set; } = false;

	public void Register<T>(Func<IHostedService> serviceFactory, string friendlyName) where T : class, IHostedService
	{
		Register<T>(serviceFactory(), friendlyName);
	}

	private void Register<T>(IHostedService service, string friendlyName) where T : class, IHostedService
	{
		if (typeof(T) != service.GetType())
		{
			throw new ArgumentException($"Type mismatch: {nameof(T)} is {typeof(T).Name}, but {nameof(service)} is {service.GetType()}.");
		}

		if (IsStartAllAsyncStarted)
		{
			throw new InvalidOperationException("_services are already started.");
		}

		lock (_servicesLock)
		{
			if (AnyNoLock<T>())
			{
				throw new InvalidOperationException($"{typeof(T).Name} is already registered.");
			}
			_services.Add(new HostedService(service, friendlyName));
		}
	}

	/// <summary>
	/// Registers a service after the others were started, and starts it. Until the others are started, it is registered
	/// like any other and started with them.
	/// </summary>
	/// <exception cref="InvalidOperationException">A service of this kind is registered already.</exception>
	public async Task RegisterAndStartAsync<T>(Func<IHostedService> serviceFactory, string friendlyName, CancellationToken token = default) where T : class, IHostedService
	{
		var service = serviceFactory();

		if (typeof(T) != service.GetType())
		{
			throw new ArgumentException($"Type mismatch: {nameof(T)} is {typeof(T).Name}, but {nameof(service)} is {service.GetType()}.");
		}

		bool startNow;

		lock (_servicesLock)
		{
			if (AnyNoLock<T>())
			{
				throw new InvalidOperationException($"{typeof(T).Name} is already registered.");
			}

			_services.Add(new HostedService(service, friendlyName));
			startNow = IsStartAllAsyncStarted;
		}

		if (startNow)
		{
			await service.StartAsync(token).ConfigureAwait(false);
			Logger.LogInfo($"Started {friendlyName}.");
		}
	}

	public async Task StartAllAsync(CancellationToken token = default)
	{
		HostedService[] services;

		// A service that is registered from now on is started by whoever registers it, see RegisterAndStartAsync.
		lock (_servicesLock)
		{
			if (IsStartAllAsyncStarted)
			{
				throw new InvalidOperationException("Operation is already started.");
			}
			IsStartAllAsyncStarted = true;
			services = _services.ToArray();
		}

		var exceptions = new List<Exception>();
		var exceptionsLock = new object();

		var tasks = services.Select(x => x.Service.StartAsync(token).ContinueWith(y =>
		{
			if (y.Exception is null)
			{
				Logger.LogInfo($"Started {x.FriendlyName}.");
			}
			else
			{
				lock (exceptionsLock)
				{
					exceptions.Add(y.Exception);
				}
				Logger.LogError($"Error starting {x.FriendlyName}.");
				Logger.LogError(y.Exception);
			}
		}));

		await Task.WhenAll(tasks).ConfigureAwait(false);

		if (exceptions.Count != 0)
		{
			throw new AggregateException(exceptions);
		}
	}

	/// <remarks>This method does not throw exceptions.</remarks>
	public async Task StopAllAsync(CancellationToken token = default)
	{
		var tasks = CloneServices().Select(x => x.Service.StopAsync(token).ContinueWith(y =>
		{
			if (y.Exception is null)
			{
				Logger.LogInfo($"Stopped {x.FriendlyName}.");
			}
			else
			{
				Logger.LogError($"Error stopping {x.FriendlyName}.");
				Logger.LogError(y.Exception);
			}
		}));

		await Task.WhenAll(tasks).ConfigureAwait(false);
	}

	private IEnumerable<HostedService> CloneServices()
	{
		lock (_servicesLock)
		{
			return _services.ToArray();
		}
	}

	public T? GetOrDefault<T>() where T : class, IHostedService
	{
		lock (_servicesLock)
		{
			return _services.SingleOrDefault(x => x.Service is T)?.Service as T;
		}
	}

	public T Get<T>() where T : class, IHostedService
	{
		lock (_servicesLock)
		{
			return (T)_services.Single(x => x.Service is T).Service;
		}
	}

	private bool AnyNoLock<T>() where T : class, IHostedService => _services.Any(x => x.Service is T);

	#region IDisposable Support

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposedValue)
		{
			if (disposing)
			{
				foreach (var service in CloneServices())
				{
					if (service.Service is IDisposable disposable)
					{
						disposable?.Dispose();
						Logger.LogInfo($"Disposed {service.FriendlyName}.");
					}
				}
			}

			_disposedValue = true;
		}
	}

	// This code added to correctly implement the disposable pattern.
	public void Dispose()
	{
		// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		Dispose(true);
	}

	#endregion IDisposable Support
}
