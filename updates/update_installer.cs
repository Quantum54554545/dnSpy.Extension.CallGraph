using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace dnSpy.CallGraph.Updates {
	sealed record install_plan(
		[property: JsonPropertyName("Candidate")] string candidate,
		[property: JsonPropertyName("Destination")] string destination,
		[property: JsonPropertyName("Sha256")] string sha256,
		[property: JsonPropertyName("OriginalSha256")] string original_sha256,
		[property: JsonPropertyName("Version")] string version,
		[property: JsonPropertyName("Executable")] string executable,
		[property: JsonPropertyName("WorkingDirectory")] string working_directory,
		[property: JsonPropertyName("Arguments")] string[] arguments,
		[property: JsonPropertyName("ProcessId")] int process_id,
		[property: JsonPropertyName("ProcessStartTicks")] long process_start_ticks,
		[property: JsonPropertyName("Nonce")] string nonce = "",
		[property: JsonPropertyName("AcknowledgeSeconds")] int acknowledge_seconds = 60
	);
	sealed class prepared_update : IDisposable {
		public string directory_path { get; }
		public string plan_path => Path.Combine(this.directory_path, "plan.json");
		public prepared_update(string directory) => this.directory_path = directory;
		public void cancel() => File.WriteAllText(Path.Combine(this.directory_path, "cancel"), "cancel");
		public void Dispose() {
			foreach (string name in new[] { "candidate.dll", "plan.json", "install.cmd", "install.ps1", "ready", "cancel", "ack", "done", "result.json" }) {
				try { File.Delete(Path.Combine(this.directory_path, name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			}
			try { Directory.Delete(this.directory_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
		}
	}
	static class update_installer {
		public static void validate_destination(string executable, string destination) {
			if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("dnSpy.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cannot identify the running dnSpy executable.");
			if (!File.Exists(destination) || !Path.GetFileName(destination).Equals("CallGraph.Extension.x.dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cannot identify the installed Call Graph DLL.");
			if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Updating a symbolic-link DLL is not supported.");
			string relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(executable))!, Path.GetFullPath(destination));
			if (!(relative.StartsWith("bin" + Path.DirectorySeparatorChar + "Extensions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || relative.StartsWith("Extensions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The plugin is outside this dnSpy installation.");
			var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(destination))!);
			while (folder is not null) { if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Updating through a directory junction is not supported."); folder = folder.Parent; }
		}
		public static async Task<prepared_update> prepare_async(update_client client, update_release release, CancellationToken token) {
			string executable = Environment.ProcessPath ?? throw new InvalidDataException("Cannot locate dnSpy.");
			update_installer.validate_destination(executable, update_protocol.installed_path);
			string directory = Path.Combine(Path.GetTempPath(), "dnSpy.CallGraph.Update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
			var prepared = new prepared_update(directory);
			try {
				string candidate = Path.Combine(directory, "candidate.dll");
				await client.download_async(release, candidate, token).ConfigureAwait(false);
				await Task.Run(() => update_protocol.validate_dll(candidate, release.version), token).ConfigureAwait(false);
				update_protocol.validate_dll(update_protocol.installed_path, update_protocol.current_version);
				using var process = Process.GetCurrentProcess();
				var plan = new install_plan(candidate, update_protocol.installed_path, update_protocol.hash(candidate), update_protocol.hash(update_protocol.installed_path), update_protocol.format(release.version), executable, Environment.CurrentDirectory, update_installer.restart_arguments(Environment.GetCommandLineArgs()[1..]), process.Id, process.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"));
				await File.WriteAllTextAsync(prepared.plan_path, JsonSerializer.Serialize(plan), new UTF8Encoding(false), token).ConfigureAwait(false);
				await update_installer.write_helper_async(prepared, token).ConfigureAwait(false);
				return prepared;
			}
			catch { prepared.Dispose(); throw; }
		}
		public static string read_resource(string name) { using var reader = new StreamReader(typeof(update_installer).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing embedded resource: " + name)); return reader.ReadToEnd(); }
		internal static string[] restart_arguments(string[] arguments) => arguments.Contains("--multiple", StringComparer.Ordinal) ? arguments : arguments.Append("--multiple").ToArray();
		internal static async Task write_helper_async(prepared_update prepared, CancellationToken token) {
			await File.WriteAllTextAsync(Path.Combine(prepared.directory_path, "install.ps1"), update_installer.read_resource("CallGraph.Install.ps1"), new UTF8Encoding(true), token).ConfigureAwait(false);
			await File.WriteAllTextAsync(Path.Combine(prepared.directory_path, "install.cmd"), "@echo off\r\n\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%~dp0install.ps1\" -plan_path \"%~dp0plan.json\" >nul 2>&1\r\nset result=%errorlevel%\r\n(del /q \"%~dp0install.ps1\" \"%~f0\" 2>nul & rd \"%~dp0\" 2>nul)\r\nexit /b %result%\r\n", new UTF8Encoding(false), token).ConfigureAwait(false);
		}
		public static ProcessStartInfo launcher(string directory) {
			var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
			start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
			start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("install.cmd"); return start;
		}
		public static async Task wait_until_ready_async(prepared_update prepared, Process helper, CancellationToken token) {
			var timer = Stopwatch.StartNew();
			while (!File.Exists(Path.Combine(prepared.directory_path, "ready"))) {
				token.ThrowIfCancellationRequested();
				if (helper.HasExited || timer.Elapsed > TimeSpan.FromSeconds(20)) {
					string log = Path.Combine(prepared.directory_path, "result.json");
					string detail = File.Exists(log) && new FileInfo(log).Length <= 8192 ? File.ReadAllText(log) : "";
					throw new IOException("The update installer did not start. dnSpy has not been closed. " + detail);
				}
				await Task.Delay(100, token).ConfigureAwait(false);
			}
		}
		internal static void acknowledge_startup(string? path) {
			if (string.IsNullOrEmpty(path)) return;
			string full = Path.GetFullPath(path), directory = Path.GetDirectoryName(full)!;
			if (Path.GetFileName(full) != "plan.json" || !Path.GetDirectoryName(directory)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith("dnSpy.CallGraph.Update-", StringComparison.Ordinal) || !File.Exists(full) || new FileInfo(full).Length > 65536) return;
			var plan = JsonSerializer.Deserialize<install_plan>(File.ReadAllText(full));
			if (plan is null || plan.nonce.Length != 32 || !Path.GetFullPath(plan.destination).Equals(Path.GetFullPath(update_protocol.installed_path), StringComparison.OrdinalIgnoreCase) || update_protocol.parse_version(plan.version) != update_protocol.current_version || update_protocol.hash(update_protocol.installed_path) != plan.sha256) return;
			File.WriteAllText(Path.Combine(directory, "ack"), plan.nonce);
		}
	}
}
