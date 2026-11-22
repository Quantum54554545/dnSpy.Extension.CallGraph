using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.CallGraph.Updates {
	sealed class update_client : IDisposable {
		readonly HttpClient http;
		public update_client(HttpMessageHandler? handler = null) {
			this.http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, MaxResponseHeadersLength = 32 });
			this.http.Timeout = Timeout.InfiniteTimeSpan;
			this.http.DefaultRequestHeaders.UserAgent.ParseAdd("dnSpy.CallGraph/" + update_protocol.format(update_protocol.current_version));
		}
		public async Task<update_release> check_async(CancellationToken token) {
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
			using var response = await this.http.GetAsync(update_protocol.feed, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			using var memory = new MemoryStream(); await update_client.copy_bounded_async(response.Content, memory, 8192, timeout.Token).ConfigureAwait(false);
			return update_protocol.parse_feed(new UTF8Encoding(false, true).GetString(memory.ToArray()));
		}
		public async Task download_async(update_release release, string destination, CancellationToken token) {
			if (!release.is_dll) throw new InvalidDataException("Automatic updates require a .dll asset. The old RAR release is not installed automatically.");
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
			var uri = release.download;
			for (int redirects = 0; redirects <= 5; redirects++) {
				if (!update_protocol.is_download_hop(uri, release.version)) throw new InvalidDataException("Unexpected download host.");
				using var response = await this.http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
				if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) {
					uri = response.Headers.Location is Uri location ? new Uri(uri, location) : throw new InvalidDataException("Missing download redirect."); continue;
				}
				response.EnsureSuccessStatusCode();
				await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
				await update_client.copy_bounded_async(response.Content, output, update_protocol.maximum_dll_bytes, timeout.Token).ConfigureAwait(false); return;
			}
			throw new InvalidDataException("Too many download redirects.");
		}
		internal static async Task copy_bounded_async(HttpContent content, Stream output, int limit, CancellationToken token) {
			if (content.Headers.ContentLength > limit) throw new InvalidDataException("Update response is too large.");
			await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
			byte[] buffer = new byte[65536]; int total = 0;
			while (true) {
				int count = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break;
				if (count > limit - total) throw new InvalidDataException("Update response is too large.");
				total += count; await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
			}
			if (content.Headers.ContentLength is long length && total != length) throw new InvalidDataException("The download was incomplete.");
		}
		public void Dispose() => this.http.Dispose();
	}
}
