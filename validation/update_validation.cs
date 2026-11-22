using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using dnSpy.CallGraph.Updates;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Menus;
using dnlib.DotNet;

namespace CallGraph.Validation {
	static partial class program {
		sealed class update_http : HttpMessageHandler {
			readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
			public List<Uri> requests { get; } = new();
			public update_http(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;
			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); this.requests.Add(request.RequestUri!); return Task.FromResult(this.respond(request)); }
		}
		static void check_updates() {
			var current = update_protocol.current_version; var next = new Version(current.Major, current.Minor, current.Build + 1, 0);
			string url = "https://github.com/" + update_protocol.repository + "/releases/download/v" + update_protocol.format(next) + "/CallGraph.Extension.x.dll";
			string feed = "v" + update_protocol.format(next) + "\n" + url;
			var release = update_protocol.parse_feed("\uFEFF" + feed.Replace("\n", "\r\n") + "\r\n");
			var file_version = FileVersionInfo.GetVersionInfo(update_protocol.installed_path);
			program.check("public update version is independent of internal assembly version and matches Windows file properties", current != typeof(update_protocol).Assembly.GetName().Version && update_protocol.parse_version(file_version.FileVersion!) == current && update_protocol.parse_version(file_version.ProductVersion!) == current);
			program.check("update manifest accepts exactly two CRLF lines and BOM", release.version == next && release.is_dll);
			program.check("update versions compare numerically not lexicographically", update_protocol.parse_version("v0.4.10") > update_protocol.parse_version("v0.4.9") && update_protocol.parse_version("v0.4.2") == update_protocol.parse_version("0.4.2.0"));
			bool fails(Action action) { try { action(); return false; } catch (Exception) { return true; } }
			foreach (string invalid in new[] { "v0.4", "-0.4.2", "v0.4.2-beta", "v0.4.2\nother", "999999999999.1.1" }) program.check("invalid release version is rejected: " + invalid, fails(() => update_protocol.parse_version(invalid)));
			foreach (string invalid in new[] { feed + "\nextra", "<html>not a feed</html>", feed.Replace("https://github.com", "http://github.com"), feed.Replace("github.com/", "github.com.evil.test/"), feed.Replace(update_protocol.repository, "another/repo"), feed.Replace("/releases/download/v" + update_protocol.format(next), "/releases/download/v9.9.9"), feed.Replace("https://github.com/", "https://user:pass@github.com/") }) program.check("malformed or cross-repository manifest rejected", fails(() => update_protocol.parse_feed(invalid)));
			program.check("signed GitHub asset redirects are accepted but arbitrary hosts are rejected", update_protocol.is_download_hop(new Uri("https://release-assets.githubusercontent.com/path?sig=123"), next) && !update_protocol.is_download_hop(new Uri("https://evil.test/payload.dll"), next) && !update_protocol.is_download_hop(new Uri("http://release-assets.githubusercontent.com/path"), next));
			string directory = Path.Combine(Path.GetTempPath(), "CallGraph-update-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
			try {
				var preference_values = new Dictionary<string, object?>(); var preference_service = depth_services.empty<ISettingsService>(preference_values); var state = new update_state(preference_service);
				var old = update_protocol.parse_feed("v0.4.1\nhttps://github.com/" + update_protocol.repository + "/releases/download/v0.4.1/v0.4.1.rar");
				program.check("old v0.4.1 RAR cannot trigger downgrade or download", !state.should_offer(old) && !old.is_dll);
				program.check("new DLL is offered on startup", state.should_offer(release)); state.ignore(release);
				program.check("ignored version survives restart", !new update_state(preference_service).should_offer(release));
				var later = release with { version = new Version(9, 9, 9, 0) }; program.check("ignoring one update does not hide later releases", state.should_offer(later));
				program.check("release notes initially display once per installed version", state.should_show_notes); state.mark_notes_seen(); program.check("release-note seen marker survives restart", !new update_state(preference_service).should_show_notes);
				string legacy = Path.Combine(directory, "legacy.json"); File.WriteAllText(legacy, "{broken"); var broken = new update_state(depth_services.empty<ISettingsService>(), legacy_path: legacy); program.check("damaged legacy preferences do not prevent startup or create a new preference file", broken.should_show_notes);
				program.check("update preferences are written only to dnSpy settings", preference_values.Keys.Any(key => key.StartsWith("SeenVersion_", StringComparison.Ordinal)) && !Directory.Exists(Path.Combine(directory, "state")));
				using (var client = new update_client(new update_http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) }))) program.check("HTTP manifest uses production parser", client.check_async(default).GetAwaiter().GetResult() == release);
				using (var client = new update_client(new update_http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 9000)) }))) program.check("oversized update feed is rejected", fails(() => client.check_async(default).GetAwaiter().GetResult()));
				using (var client = new update_client(new update_http(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))) program.check("offline server cannot become an update", fails(() => client.check_async(default).GetAwaiter().GetResult()));
				byte[] bytes = File.ReadAllBytes(update_protocol.installed_path); string downloaded = Path.Combine(directory, "download.dll");
				var redirects = new update_http(request => request.RequestUri!.Host == "github.com" ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/test.dll?sig=abc") } } : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
				using (var client = new update_client(redirects)) client.download_async(release, downloaded, default).GetAwaiter().GetResult();
				program.check("download follows a validated GitHub redirect without altering bytes", redirects.requests.Count == 2 && update_protocol.hash(downloaded) == update_protocol.hash(update_protocol.installed_path));
				update_protocol.validate_dll(downloaded, current); program.check("downloaded DLL is inspected as metadata without loading code", true);
				program.check("manifest version must match both DLL product and file versions", fails(() => update_protocol.validate_dll(downloaded, next)));
				string future_path = Path.Combine(directory, "future.dll");
				using (var future = ModuleDefMD.Load(downloaded)) {
					void set_attribute(string name, string value) { var attribute = future.Assembly.CustomAttributes.Single(attribute => attribute.TypeFullName == name); attribute.ConstructorArguments[0] = new CAArgument(attribute.ConstructorArguments[0].Type, new UTF8String(value)); }
					set_attribute("System.Reflection.AssemblyInformationalVersionAttribute", update_protocol.format(next)); set_attribute("System.Reflection.AssemblyFileVersionAttribute", next.ToString(4)); future.Write(future_path);
					update_protocol.validate_dll(future_path, next); program.check("future-version DLL passes only when both product and file versions agree", true);
					if (Environment.Is64BitProcess) { future.Is32BitRequired = true; future.Write(future_path); program.check("x86-only update cannot break 64-bit dnSpy", fails(() => update_protocol.validate_dll(future_path, next))); future.Is32BitRequired = false; }
					set_attribute("System.Reflection.AssemblyFileVersionAttribute", current.ToString(4)); future.Write(future_path); program.check("mismatched file version is rejected even with matching product version", fails(() => update_protocol.validate_dll(future_path, next)));
					set_attribute("System.Reflection.AssemblyFileVersionAttribute", next.ToString(4)); set_attribute("System.Runtime.Versioning.TargetFrameworkAttribute", ".NETFramework,Version=v4.8"); future.Write(future_path); program.check("different-runtime update is rejected before shutdown", fails(() => update_protocol.validate_dll(future_path, next)));
				}
				string foreign = Path.Combine(directory, "foreign.dll"); File.Copy(Assembly.GetExecutingAssembly().Location, foreign); program.check("another managed assembly cannot replace Call Graph", fails(() => update_protocol.validate_dll(foreign, current)));
				File.WriteAllText(foreign, "<html>download failed</html>"); program.check("HTML cannot be installed as an update DLL", fails(() => update_protocol.validate_dll(foreign, current)));
				var blocked = new update_http(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.test/update.dll") } });
				using (var client = new update_client(blocked)) program.check("untrusted redirect is blocked before second HTTP request", fails(() => client.download_async(release, Path.Combine(directory, "bad.dll"), default).GetAwaiter().GetResult()) && blocked.requests.Count == 1);
				using (var client = new update_client(new update_http(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(url) } }))) program.check("redirect cycles terminate", fails(() => client.download_async(release, Path.Combine(directory, "loop.dll"), default).GetAwaiter().GetResult()));
				using (var client = new update_client(new update_http(_ => throw new Exception("RAR must not be downloaded")))) program.check("archive update cannot start a download or installer", fails(() => client.download_async(old, Path.Combine(directory, "archive.rar"), default).GetAwaiter().GetResult()) && !File.Exists(Path.Combine(directory, "archive.rar")));
				using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); using var client = new update_client(new update_http(_ => throw new Exception("cancelled"))); program.check("update HTTP respects cancellation", fails(() => client.check_async(cancelled.Token).GetAwaiter().GetResult())); }
				using (var content = new ByteArrayContent(new byte[100])) { content.Headers.ContentLength = null; using var output = new MemoryStream(); program.check("chunked bodies cannot evade response size limit", fails(() => update_client.copy_bounded_async(content, output, 20, default).GetAwaiter().GetResult())); }
				using (var content = new ByteArrayContent(new byte[10])) { content.Headers.ContentLength = 50; using var output = new MemoryStream(); program.check("truncated download fails before installation", fails(() => update_client.copy_bounded_async(content, output, 100, default).GetAwaiter().GetResult())); }
				var start = update_installer.launcher(directory); program.check("batch updater is launched with a hidden window and no shell interpolation", !start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == ProcessWindowStyle.Hidden && start.ArgumentList.SequenceEqual(new[] { "/d", "/c", "install.cmd" }));
				program.check("restart opens a fresh host instead of redirecting into another already running dnSpy", update_installer.restart_arguments(new[] { "example.dll" }).SequenceEqual(new[] { "example.dll", "--multiple" }) && update_installer.restart_arguments(new[] { "--multiple", "example.dll" }).Count(value => value == "--multiple") == 1);
				program.check("installation cannot target another application's DLL", fails(() => update_installer.validate_destination(Environment.ProcessPath!, foreign)));
				program.check_update_notifications(Path.Combine(directory, "notifications"), feed); program.check_update_notes(); program.check_update_helper(directory);
			}
			finally {
				foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) { try { File.Delete(path); } catch (IOException) { } }
				foreach (string path in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) { try { Directory.Delete(path); } catch (IOException) { } }
				try { Directory.Delete(directory); } catch (IOException) { }
			}
		}
		static void check_update_notifications(string directory, string feed) {
			var notifications = new List<(string message, InfoBarIcon icon, InfoBarInteraction[] actions)>(); var calls = new List<string>();
			var bar = depth_services.empty<IAppInfoBar>(new Dictionary<string, object?> { ["$callback:Show"] = new Func<object?[]?, object?>(arguments => { notifications.Add(((string)arguments![0]!, (InfoBarIcon)arguments[1]!, (InfoBarInteraction[])arguments[2]!)); return depth_services.empty<IInfoBarElement>(new Dictionary<string, object?> { ["$trace"] = calls }); }) });
			var app = depth_services.empty<IAppWindow>(new Dictionary<string, object?> { ["$get_InfoBar"] = bar });
			var state = new update_state(depth_services.empty<ISettingsService>()); bool offline = false;
			var service = new update_service(app, state, () => new update_client(new update_http(_ => new HttpResponseMessage(offline ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(feed) })));
			void check() { var pending = service.check_async(); program.pump_ui(() => !pending.IsCompleted); pending.GetAwaiter().GetResult(); }
			check();
			program.check("production update service uses native InfoBar with exactly download and ignore actions", notifications.Count == 1 && notifications[0].icon == InfoBarIcon.Information && notifications[0].actions.Length == 2 && notifications[0].actions[0].Text == dnSpy.CallGraph.ui_text.pick("Download update", "Скачать обновление") && notifications[0].actions[1].Text == dnSpy.CallGraph.ui_text.pick("Ignore this message", "Проигнорировать это сообщение"));
			var context = depth_services.empty<IInfoBarInteractionContext>(new Dictionary<string, object?> { ["$trace"] = calls }); notifications[0].actions[1].Action(context);
			check(); program.check("ignore action persists and prevents repeated startup notifications", notifications.Count == 1 && calls.Contains("CloseElement"));
			offline = true; check(); program.check("automatic network failure stays silent and never closes dnSpy", notifications.Count == 1);
			program.check("update errors do not create a persistent log directory", !Directory.Exists(directory));
		}
		static void check_update_notes() {
			program.check_update_localization();
			var document = release_notes_window.render_markdown("# Heading\n\n## Section\n- **Important** `Alt+Q`\n<script>literal</script>\n```\ncode();\n```");
			program.check("release notes render headings bold code and literal HTML without browser execution", document.Blocks.OfType<Paragraph>().First().FontSize == 24 && document.Blocks.OfType<Paragraph>().Any(paragraph => paragraph.Inlines.OfType<Bold>().Any()) && new TextRange(document.ContentStart, document.ContentEnd).Text.Contains("<script>literal</script>", StringComparison.Ordinal));
			var owner = new Window { Left = -10000, Top = -10000, Width = 1, Height = 1, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual }; owner.Show();
			var window = release_notes_window.create(owner); window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.ShowActivated = false;
			var style = new Style(window.GetType()); window.Resources["DialogWindowStyle"] = style; window.Resources["SystemColorsWindow"] = Brushes.Black; window.Resources["SystemColorsWindowText"] = Brushes.Gainsboro;
			program.check("what's-new window uses native dnSpy dialog styling", ReferenceEquals(window.Style, style) && ReferenceEquals(window.Background, Brushes.Black) && !window.ShowInTaskbar);
window.Close(); owner.Close();
		}
		static void check_update_localization() {
			var original = CultureInfo.CurrentUICulture;
			try {
				foreach (string language in new[] { "ru-RU", "ru-UA", "en-US", "en-GB", "de-DE" }) {
					CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language); bool russian = language.StartsWith("ru", StringComparison.Ordinal);
					string markdown = release_notes_window.markdown;
					program.check("release-note resource follows interface language " + language, markdown.Contains(russian ? "Изменения" : "Changes", StringComparison.Ordinal) && !markdown.Contains(russian ? "Changes" : "Изменения", StringComparison.Ordinal));
					program.check("localized notes match the installed public version " + language, markdown.Replace("\r", "").StartsWith("# Call Graph v" + update_protocol.format(update_protocol.current_version) + "\n", StringComparison.Ordinal));
					var window = release_notes_window.create(null!);
					var document = ((DockPanel)window.Content).Children.OfType<FlowDocumentScrollViewer>().Single().Document;
					program.check("window title buttons and document share language " + language, window.Title == (russian ? "Что нового в Call Graph" : "What's new in Call Graph") && ((DockPanel)window.Content).Children.OfType<Button>().Single().Content.Equals(russian ? "Закрыть" : "Close") && new TextRange(document.ContentStart, document.ContentEnd).Text.Contains(russian ? "Изменения" : "Changes", StringComparison.Ordinal));
					window.Close();
				}
			}
			finally { CultureInfo.CurrentUICulture = original; }
		}
		
	}
}
