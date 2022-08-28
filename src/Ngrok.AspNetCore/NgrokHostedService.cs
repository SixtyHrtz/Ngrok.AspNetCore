using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ngrok.ApiClient;
using Ngrok.AspNetCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tunnel = Ngrok.ApiClient.Tunnel;

namespace Ngrok.AspNetCore
{
	public class NgrokHostedService : INgrokHostedService
	{
		private readonly NgrokOptions _options;
		private readonly NgrokDownloader _nGrokDownloader;
		private readonly NgrokProcessMgr _processMgr;
		private readonly INgrokApiClient _client;
		private readonly IServer _server;
		private readonly ILogger<NgrokHostedService> _logger;

		private readonly TaskCompletionSource<IReadOnlyCollection<Tunnel>> _tunnelTaskSource;
		private readonly TaskCompletionSource<bool> _shutdownSource;
		private IEnumerable<Tunnel> _tunnels;

		private readonly CancellationTokenSource _cancellationTokenSource;

		public async Task<IReadOnlyCollection<Tunnel>> GetTunnelsAsync()
		{
			if (_options.Disable)
				return Array.Empty<Tunnel>();

			return await WaitForTaskWithTimeout(_tunnelTaskSource.Task, 300_000, "No tunnels were found within 5 minutes. Perhaps the server was taking too long to start?");
		}

		public event Action<IReadOnlyCollection<Tunnel>> Ready;

		public NgrokHostedService(
			IOptionsMonitor<NgrokOptions> optionsMonitor,
			ILogger<NgrokHostedService> logger,
			NgrokDownloader nGrokDownloader,
			IServer server,
			NgrokProcessMgr processMgr,
			INgrokApiClient client)
		{
			_logger = logger;
			_options = optionsMonitor.CurrentValue;
			_nGrokDownloader = nGrokDownloader;
			_server = server;
			_processMgr = processMgr;
			_client = client;

			_tunnelTaskSource = new TaskCompletionSource<IReadOnlyCollection<Tunnel>>();
			_shutdownSource = new TaskCompletionSource<bool>();
			_cancellationTokenSource = new CancellationTokenSource();
		}

		public async Task StartAsync(CancellationToken cancellationToken)
		{
			cancellationToken.Register(() => _cancellationTokenSource.Cancel());

			await RunAsync(_cancellationTokenSource.Token);
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			if (!_options.ManageNgrokProcess && _tunnels != null)
			{
				foreach (var tunnel in _tunnels)
				{
					await _client.StopTunnelAsync(tunnel.Name, cancellationToken);
				}
			}

			_cancellationTokenSource.Cancel();

			// Stop the process
			await _processMgr.StopNgrokAsync();

			await _shutdownSource.Task;
		}

		private async Task RunAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				if (_options.Disable)
					return;

				if (_options.DownloadNgrok)
				{
					await _nGrokDownloader.DownloadExecutableAsync(_cancellationTokenSource.Token);
				}

				await _processMgr.EnsureNgrokStartedAsync(cancellationToken);

				if (_cancellationTokenSource.IsCancellationRequested)
					return;

				var url = _options.ApplicationHttpUrl;

				if (string.IsNullOrWhiteSpace(url))
					throw new InvalidOperationException("No application URL has been set.");

				_logger.LogInformation("Picked hosting URL {Url}.", url);

				if (_cancellationTokenSource.IsCancellationRequested)
					return;

				var tunnels = await GetTunnelsListAsync(cancellationToken);

				if (_cancellationTokenSource.IsCancellationRequested)
					return;

				if (tunnels == null || tunnels.Length == 0)
					tunnels = await StartTunnelsAsync(url, cancellationToken);

				_logger.LogInformation("Tunnels {Tunnels} have been started.", new object[] { tunnels });

				if (_cancellationTokenSource.IsCancellationRequested)
					return;

				if (tunnels != null)
					OnTunnelsFetched(tunnels);

			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "An error occured while running the Ngrok service.");
			}
			finally
			{
				_shutdownSource.SetResult(true);
			}
		}

		private void OnTunnelsFetched(IEnumerable<Tunnel> tunnels)
		{
			if (tunnels == null)
				throw new ArgumentNullException(nameof(tunnels), "Tunnels was not expected to be null here.");

			_tunnels = tunnels;
			_tunnelTaskSource.SetResult(tunnels.ToArray());
			Ready?.Invoke(tunnels.ToArray());
		}

		private async Task<Tunnel[]?> StartTunnelsAsync(string address, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(address))
			{
				address = "80";
			}
			else
			{
				if (!int.TryParse(address, out _))
				{
					var url = new Uri(address);
					if (url.Port != 80 && url.Port != 443)
					{
						address = $"{url.Host}:{url.Port}";
					}
					else
					{
						if (address.StartsWith("http://"))
						{
							address = address.Remove(address.IndexOf("http://"), "http://".Length);
						}
						if (address.StartsWith("https://"))
						{
							address = address.Remove(address.IndexOf("https://"), "https://".Length);
						}
					}
				}
			}

			// Start Tunnel
			var tunnel = await _client.StartTunnelAsync(new StartTunnelRequest()
			{
				Name = System.AppDomain.CurrentDomain.FriendlyName,
				Address = address,
				Protocol = "http"
			}, cancellationToken);

			// Get Tunnels
			var tunnels = await GetTunnelsListAsync(cancellationToken);

			return tunnels;
		}

		private async Task<Tunnel[]?> GetTunnelsListAsync(CancellationToken cancellationToken)
		{
			return (await _client.ListTunnelsAsync(cancellationToken))
				.Where(t => t.Name == System.AppDomain.CurrentDomain.FriendlyName ||
					t.Name == $"{System.AppDomain.CurrentDomain.FriendlyName} (http)")
				?.ToArray();
		}

		private async Task<T> WaitForTaskWithTimeout<T>(Task<T> task, int timeoutInMilliseconds, string timeoutMessage)
		{
			if (await Task.WhenAny(task, Task.Delay(timeoutInMilliseconds, _cancellationTokenSource.Token)) == task)
				return await task;

			throw new InvalidOperationException(timeoutMessage);
		}
	}
}
