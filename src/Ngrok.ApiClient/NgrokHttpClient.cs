﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ngrok.ApiClient
{
	public class NgrokHttpClient : INgrokApiClient
	{
		private const string ListTunnelsPath = "/api/tunnels";
		private const string GetTunnelPathFormat = "/api/tunnels/{0}";
		private const string StartTunnelPath = "/api/tunnels";
		private const string StopTunnelPathFormat = "/api/tunnels/{0}";

		public HttpClient Client { get; }

		public NgrokHttpClient(HttpClient client)
		{
			client.BaseAddress = new Uri("http://localhost:4040");

			// TODO set content-type to application/json

			Client = client;
		}

		public async Task<IEnumerable<Tunnel>> ListTunnelsAsync(CancellationToken cancellationToken = default)
		{
			var response = await Client.GetAsync(ListTunnelsPath);
			await ThrowIfError(response, cancellationToken);

			using var responseStream = await response.Content.ReadAsStreamAsync();
			var listTunnelResponse = await JsonSerializer.DeserializeAsync
				<ListTunnelsResponse>(responseStream, cancellationToken: cancellationToken);
			return listTunnelResponse.Tunnels;
		}

		public async Task<Tunnel> StartTunnelAsync(StartTunnelRequest request, CancellationToken cancellationToken = default)
		{
			var jsonOptions = new JsonSerializerOptions()
			{
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
			};

			HttpResponseMessage response;
			using (var content = new StringContent(JsonSerializer.Serialize(request, jsonOptions), System.Text.Encoding.UTF8, "application/json"))
			{
				response = await Client.PostAsync(StartTunnelPath, content, cancellationToken);
			}
			await ThrowIfError(response, cancellationToken);

			using var responseStream = await response.Content.ReadAsStreamAsync();
			return await JsonSerializer.DeserializeAsync
				<Tunnel>(responseStream, cancellationToken: cancellationToken);
		}

		public async Task<Tunnel> GetTunnelAsync(string name, CancellationToken cancellationToken = default)
		{
			var response = await Client.GetAsync(string.Format(GetTunnelPathFormat, name), cancellationToken);
			await ThrowIfError(response, cancellationToken);

			using var responseStream = await response.Content.ReadAsStreamAsync();
			return await JsonSerializer.DeserializeAsync
				<Tunnel>(responseStream, cancellationToken: cancellationToken);
		}

		public async Task StopTunnelAsync(string name, CancellationToken cancellationToken = default)
		{
			var response = await Client.DeleteAsync(string.Format(StopTunnelPathFormat, name), cancellationToken);
			await ThrowIfError(response, cancellationToken);
		}

		private async Task ThrowIfError(HttpResponseMessage response, CancellationToken cancellationToken)
		{
			if (!response.IsSuccessStatusCode)
			{
				using var responseStream = await response.Content.ReadAsStreamAsync();
				var errorResponse = await JsonSerializer.DeserializeAsync
					<ErrorResponse>(responseStream, cancellationToken: cancellationToken);
				throw new NgrokApiException(errorResponse);
			}
		}

		public async Task<bool> CheckIfLocalAPIUpAsync(CancellationToken cancellationToken = default)
		{
			using var client = new HttpClient
			{
				BaseAddress = new Uri("http://localhost:4040")
			};
			try
			{
				var response = await client.GetAsync(ListTunnelsPath, cancellationToken);
				response.EnsureSuccessStatusCode();
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}