using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Updates;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Settings;

namespace CallGraph.Validation {
	static partial class program {
		static void check_release_compatibility() {
			var values = new Dictionary<string, object?> { ["GraphDepth"] = 6, ["MaximumLines"] = 30, ["WrapLongLines"] = false, ["OnlyCurrentAssembly"] = true };
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>(values));
			check("snake_case retains existing dnSpy settings keys", settings.graph_depth == 6 && settings.maximum_lines == 30 && !settings.wrap_long_lines && settings.only_current_assembly);
			settings.change_graph_depth(3);
			check("saving depth does not create a new incompatible snake_case settings key", Equals(values["GraphDepth"], 3) && !values.ContainsKey("graph_depth"));
			const string old_document = "{\"Version\":1,\"Methods\":{\"method\":{\"Alias\":\"saved alias\",\"Note\":\"saved note\",\"Group\":\"saved group\",\"Compact\":true}},\"Positions\":{\"root\":{\"method\":{\"X\":12,\"Y\":34,\"Manual\":true}}}}";
			var document = JsonSerializer.Deserialize<research_document>(old_document)!;
			check("existing research notes groups and positions survive identifier renaming", document.version == 1 && document.methods["method"] == new research_annotation("saved alias", "saved note", "saved group", true) && document.positions["root"]["method"] == new research_position(12, 34, true));
			using (var saved = JsonDocument.Parse(JsonSerializer.Serialize(document))) {
				check("research JSON retains its original schema on save", saved.RootElement.GetProperty("Methods").GetProperty("method").GetProperty("Alias").GetString() == "saved alias" && saved.RootElement.GetProperty("Positions").GetProperty("root").GetProperty("method").GetProperty("Manual").GetBoolean() && !saved.RootElement.TryGetProperty("methods", out _));
			}
			var plan = new install_plan("candidate.dll", "target.dll", "new hash", "old hash", update_protocol.format(update_protocol.current_version), "dnSpy.exe", "working directory", new[] { "argument with spaces" }, 123, 456, "nonce");
			string serialized_plan = JsonSerializer.Serialize(plan);
			using (var saved = JsonDocument.Parse(serialized_plan)) {
				check("installer JSON stays compatible with its PowerShell helper", saved.RootElement.GetProperty("Candidate").GetString() == plan.candidate && saved.RootElement.GetProperty("ProcessStartTicks").GetInt64() == 456 && saved.RootElement.GetProperty("OriginalSha256").GetString() == "old hash" && !saved.RootElement.TryGetProperty("process_start_ticks", out _));
			}
			check("installer plan round-trips constructor parameters and original arguments", JsonSerializer.Deserialize<install_plan>(serialized_plan) is install_plan restored && restored.arguments.SequenceEqual(plan.arguments) && restored.working_directory == plan.working_directory && restored.nonce == plan.nonce);
			var assembly = typeof(update_service).Assembly;
			var public_version = update_protocol.current_version;
			check("release file and product versions agree with the updater's public version", update_protocol.parse_version(assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version) == public_version && update_protocol.parse_version(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion) == public_version);
			string release_tag = "v" + update_protocol.format(public_version); var published = update_protocol.parse_feed(release_tag + "\nhttps://github.com/" + update_protocol.repository + "/releases/download/" + release_tag + "/CallGraph.Extension.x.dll");
			update_protocol.validate_dll(assembly.Location, published.version);
			check("release DLL passes the production updater's payload checks using its own release feed", published.is_dll && published.version == public_version);
			check("release payload is not offered as an update to the same installed public version", !new update_state(depth_services.empty<ISettingsService>()).should_offer(published));
			var help_menus = assembly.GetTypes().Where(type => type.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == "ExportMenuItemAttribute" && attribute.NamedArguments.Any(argument => argument.MemberName == "OwnerGuid" && Equals(argument.TypedValue.Value, MenuConstants.APP_MENU_HELP_GUID)))).ToArray();
			check("release Help menu exposes only what's-new, without update check or preview", help_menus.Length == 1 && help_menus[0] == typeof(call_graph_notes_menu));
			check("release DLL contains no validation types or profiling callbacks", !assembly.GetTypes().Any(type => type.Namespace == "CallGraph.Validation") && typeof(reachable_call_analysis).GetProperty("WalkCompleted", BindingFlags.Instance | BindingFlags.NonPublic) is null && typeof(context_call_evaluator).GetProperty("EvaluationCompleted", BindingFlags.Instance | BindingFlags.NonPublic) is null);
			var culture = CultureInfo.CurrentUICulture;
			try {
				foreach (string language in new[] { "ru-RU", "en-US" }) {
					CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
					string notes = release_notes_window.markdown;
					check("release changelog covers changes by section in " + language, notes.Length < 6000 && notes.Split('\n').Count(line => line.StartsWith("### ", StringComparison.Ordinal)) >= 4 && notes.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)) >= 12);
					check("release changelog matches the public version in " + language, notes.Split('\n')[0].TrimEnd('\r') == "# Call Graph v" + update_protocol.format(update_protocol.current_version));
				}
			}
			finally { CultureInfo.CurrentUICulture = culture; }
			var notices = new List<string>(); int requests = 0;
			var bar = depth_services.empty<IAppInfoBar>(new Dictionary<string, object?> { ["$callback:Show"] = new Func<object?[]?, object?>(arguments => { notices.Add((string)arguments![0]!); return depth_services.empty<IInfoBarElement>(); }) });
			var window = new Window();
			var app = depth_services.empty<IAppWindow>(new Dictionary<string, object?> { ["$get_InfoBar"] = bar, ["$get_MainWindow"] = window });
			var state = new update_state(depth_services.empty<ISettingsService>()); state.mark_notes_seen();
			var next = new Version(update_protocol.current_version.Major, update_protocol.current_version.Minor, update_protocol.current_version.Build + 1);
			string version = "v" + update_protocol.format(next);
			var service = new update_service(app, state, () => { requests++; return new update_client(new update_http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(version + "\nhttps://github.com/" + update_protocol.repository + "/releases/download/" + version + "/CallGraph.Extension.x.dll") })); });
			service.start(); service.start();
			Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
			pump_ui(() => notices.Count == 0);
			check("startup checks automatically exactly once without a manual menu", requests == 1 && notices.Count == 1 && notices[0].Contains(version, StringComparison.Ordinal));
			window.Close();
		}
	}
}
