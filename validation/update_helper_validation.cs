using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using dnSpy.CallGraph.Updates;

namespace CallGraph.Validation {
	static partial class program {
		static void check_update_helper(string root) {
			foreach (string scenario in new[] { "install", "cancel", "tampered", "locked", "changed", "restart-failed", "identity", "unacknowledged" }) {
				string directory = Path.Combine(root, "helper " + scenario + " & % unicode-я"); Directory.CreateDirectory(directory);
				var prepared = new prepared_update(directory); string destination = Path.Combine(directory, "CallGraph.Extension.x.dll"); string candidate = Path.Combine(directory, "candidate.dll");
				File.WriteAllText(destination, "original DLL"); File.WriteAllText(candidate, "verified replacement DLL"); string original_hash = update_protocol.hash(destination), candidate_hash = update_protocol.hash(candidate);
				string gate = Path.Combine(directory, "exit-parent"), restarted = Path.Combine(directory, "restarted.txt"), probe = Path.Combine(directory, "restart-probe.ps1");
				string argument = "space & percent% unicode-я \"quoted\" trailing\\";
				File.WriteAllText(probe, "param([string]$Value)\n[IO.File]::WriteAllText($env:CALLGRAPH_RESTART_MARKER, $Value)\nif ($env:CALLGRAPH_TEST_ACK -eq '1' -and $env:CALLGRAPH_UPDATE_PLAN) { $plan = Get-Content -LiteralPath $env:CALLGRAPH_UPDATE_PLAN -Raw | ConvertFrom-Json; [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($env:CALLGRAPH_UPDATE_PLAN)) 'ack'), $plan.Nonce) }\n", new UTF8Encoding(true));
				string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
				var parent_start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
				foreach (string value in new[] { "-NoProfile", "-NonInteractive", "-Command", "while (-not [IO.File]::Exists($env:CALLGRAPH_TEST_GATE)) { Start-Sleep -Milliseconds 100 }" }) parent_start.ArgumentList.Add(value);
				parent_start.Environment["CALLGRAPH_TEST_GATE"] = gate;
				using var parent = Process.Start(parent_start)!;
				string executable = powershell;
				if (scenario == "restart-failed") { executable = Path.Combine(directory, "disposable-host.exe"); File.Copy(powershell, executable); }
				var plan = new install_plan(candidate, destination, candidate_hash, original_hash, "0.4.3", executable, directory, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", probe, "-Value", argument }, parent.Id, parent.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"), 2);
				if (scenario == "identity") plan = plan with { process_start_ticks = plan.process_start_ticks + 1 };
				File.WriteAllText(prepared.plan_path, JsonSerializer.Serialize(plan)); update_installer.write_helper_async(prepared, default).GetAwaiter().GetResult();
				if (scenario == "tampered") File.AppendAllText(candidate, "tamper");
				var start = update_installer.launcher(directory); start.Environment["CALLGRAPH_RESTART_MARKER"] = restarted; start.Environment["CALLGRAPH_TEST_ACK"] = scenario == "install" ? "1" : "0";
				using var helper = Process.Start(start)!;
				FileStream? held = null;
				try {
					if (scenario is not "tampered" and not "identity") {
						update_installer.wait_until_ready_async(prepared, helper, default).GetAwaiter().GetResult();
						program.check("updater leaves original DLL intact until its own parent exits: " + scenario, !parent.HasExited && update_protocol.hash(destination) == original_hash);
						if (scenario == "cancel") prepared.cancel();
						else {
							if (scenario == "locked") held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
							if (scenario == "changed") File.WriteAllText(destination, "another installer changed the original DLL");
							if (scenario == "restart-failed") File.Delete(executable);
							File.WriteAllText(gate, "exit");
						}
					}
					if (!helper.WaitForExit(20000)) throw new TimeoutException("Disposable update helper did not finish: " + scenario);
					string result_path = Path.Combine(directory, "result.json"); using var result = JsonDocument.Parse(File.Exists(result_path) ? File.ReadAllText(result_path) : "{\"Status\":\"Installed\"}"); string status = result.RootElement.GetProperty("Status").GetString()!;
					if (scenario == "install") {
						program.check("hidden production updater atomically installs verified DLL", status == "Installed" && update_protocol.hash(destination) == candidate_hash);
						program.check("successful plugin-load acknowledgement deletes the rollback DLL", !Directory.GetFiles(directory, "*.bak").Any());
					}
					else if (scenario == "changed") program.check("updater does not overwrite a concurrent installation change", status == "Failed" && File.ReadAllText(destination) == "another installer changed the original DLL");
					else program.check("updater preserves or restores original DLL on " + scenario, status == (scenario == "cancel" ? "Cancelled" : "Failed") && update_protocol.hash(destination) == original_hash);
					if (scenario is "install" or "locked" or "changed" or "unacknowledged") {
						var timer = Stopwatch.StartNew(); while (!File.Exists(restarted) && timer.ElapsedMilliseconds < 5000) Thread.Sleep(50);
						program.check("updater restarts only its recorded host and preserves quoted arguments: " + scenario, File.Exists(restarted) && File.ReadAllText(restarted) == argument);
					}
					else if (scenario == "restart-failed") program.check("restart failure rolls back without retaining the rejected binary", !Directory.GetFiles(directory, "*.failed-*.bak").Any());
					else program.check("cancelled or invalid update never closes or restarts another process: " + scenario, !parent.HasExited && !File.Exists(restarted));
					program.check("updater never creates helper.log", !File.Exists(Path.Combine(directory, "helper.log")));
					program.check("update helper removes staged payload and command-line plan: " + scenario, !File.Exists(candidate) && !File.Exists(prepared.plan_path));
				}
				finally {
					held?.Dispose(); File.WriteAllText(gate, "exit"); if (!parent.WaitForExit(5000)) parent.Kill();
					if (!helper.HasExited) { prepared.cancel(); helper.WaitForExit(5000); }
				}
			}
		}
	}
}
