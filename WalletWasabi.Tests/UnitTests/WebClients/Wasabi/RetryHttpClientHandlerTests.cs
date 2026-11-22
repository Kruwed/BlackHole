using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using WalletWasabi.WebClients.Wasabi;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WebClients.Wasabi;

public class RetryHttpClientHandlerTests
{
	// Trivial test to make sure that the mock handler works as expected.
	[Fact]
	public async Task SendAsync_OkTestAsync()
	{
		var callbackCalled = false;

		var handler = new RetryHttpClientHandler("retry-handler", _ => callbackCalled = true,
			HttpClientHandlerConfiguration.Default,
			(_, _, _) =>
			{
				var responseMessage = new HttpResponseMessage(HttpStatusCode.OK);
				responseMessage.Content =
					new StringContent("My Response", Encoding.UTF8, MediaTypeNames.Text.Plain);
				return Task.FromResult(responseMessage);
			});

		using (handler)
		{
			using var httpClient = new HttpClient(handler, disposeHandler: false);
			using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.dev");

			using var response = await httpClient.SendAsync(request);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);

			using var stringContent = Assert.IsType<StringContent>(response.Content);
			var payload = await stringContent.ReadAsStringAsync();
			Assert.Equal("My Response", payload);
		}

		Assert.True(callbackCalled);
	}

	// Tests that the handler stops retrying if .
	[Fact]
	public async Task SendAsync_RepeatingStopsAsync()
	{
		var callbackCalled = false;
		var requestsCount = 0;
		var handler = new RetryHttpClientHandler("retry-handler", _ => callbackCalled = true, HttpClientHandlerConfiguration.Default,
			(retryHandler, _, _) =>
			{
				requestsCount++;

				if (requestsCount == 1)
				{
					// Simulate a disposed handler on the first request.
					retryHandler.Dispose();

					// Force the HTTP handler to repeat the request.
					throw new HttpRequestException(HttpRequestError.ConnectionError, "Make sure the request is repeated.");
				}
				throw new UnreachableException();
			});

		using (handler)
		{
			using var httpClient = new HttpClient(handler, disposeHandler: false);
			using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.dev");

			// Expect an ObjectDisposedException since the handler was disposed during the first request.
			var e = await Assert.ThrowsAsync<TimeoutException>(async () => await httpClient.SendAsync(request).ConfigureAwait(false));
			Assert.Equal("HTTP handler 'retry-handler' was disposed during request.", e.Message);

			Assert.Equal(1, requestsCount);
		}
		Assert.True(callbackCalled);
	}

	// Tests that what went wrong the last time is kept when every attempt failed, for whoever has to tell why the server cannot be reached.
	[Fact]
	public async Task SendAsync_LastErrorIsKeptAsync()
	{
		var requestsCount = 0;
		var config = new HttpClientHandlerConfiguration { MaxAttempts = 3, TimeBeforeRetryingAfterNetworkError = TimeSpan.FromMilliseconds(10) };

		var handler = new RetryHttpClientHandler("retry-handler", _ => { }, config,
			(_, _, _) =>
			{
				requestsCount++;
				throw new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused, attempt {requestsCount}.");
			});

		using (handler)
		{
			using var httpClient = new HttpClient(handler, disposeHandler: false);
			using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.dev");

			var e = await Assert.ThrowsAsync<HttpRequestException>(async () => await httpClient.SendAsync(request).ConfigureAwait(false));

			Assert.Equal("Failed to make http request 'http://test.dev/' after 3 attempts.", e.Message);
			Assert.Equal("Connection refused, attempt 3.", Assert.IsType<HttpRequestException>(e.InnerException).Message);
			Assert.Equal(3, requestsCount);
		}
	}

	// Tests that whoever holds a switchable factory gets the clients of the factory that it was switched to, without being told.
	[Fact]
	public void SwitchableFactoryHandsOutTheClientsOfTheFactoryItWasSwitchedTo()
	{
		var first = new CoordinatorHttpClientFactory(new Uri("http://first.example/"), new HttpClientFactory());
		var second = new CoordinatorHttpClientFactory(new Uri("http://second.example/"), new HttpClientFactory());

		var factory = new SwitchableHttpClientFactory(first);
		IHttpClientFactory seenByOthers = factory;

		using (var client = seenByOthers.CreateClient("satoshi-test"))
		{
			Assert.Equal(new Uri("http://first.example/"), client.BaseAddress);
		}

		factory.SwitchTo(second);

		using (var client = seenByOthers.CreateClient("satoshi-test"))
		{
			Assert.Equal(new Uri("http://second.example/"), client.BaseAddress);
		}
	}

	// Tests that an ObjectDisposedException is thrown when the handler is disposed before sending an HTTP request.
	[Fact]
	public async Task SendAsync_DisposeExceptionIsThrownAsync()
	{
		var callbackCalled = false;

		var handler = new RetryHttpClientHandler("retry-handler", _ => callbackCalled = true, HttpClientHandlerConfiguration.Default,
			(_, _, _) =>
			{
				var responseMessage = new HttpResponseMessage(HttpStatusCode.OK);
				responseMessage.Content = new StringContent("My Response", Encoding.UTF8, MediaTypeNames.Text.Plain);
				return Task.FromResult(responseMessage);
			});

		using var httpClient = new HttpClient(handler, disposeHandler: false);
		// Dispose the handler to trigger the disposed exception.
		handler.Dispose();

		// Send a request after disposing the handler.
		using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.dev");

		// Expect an ObjectDisposedException since the handler was disposed before sending the request.
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await httpClient.SendAsync(request).ConfigureAwait(false));

		Assert.True(callbackCalled);
	}
}
