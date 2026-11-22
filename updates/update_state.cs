using System;
using System.IO;
using System.Text.Json;
using dnSpy.Contracts.Settings;
using System.Text.Json.Serialization;

namespace dnSpy.CallGraph.Updates {
	sealed class update_preferences {
		[JsonPropertyName("IgnoredVersion")]
		public string ignored_version { get; set; } = "";
		[JsonPropertyName("SeenVersion")]
		public string seen_version { get; set; } = "";
	}
	sealed class update_state {
		internal static readonly Guid settings_guid = new("A7A21789-C48A-4692-9AB6-17580202E847");
		readonly ISettingsSection section;
		readonly string key;
		public update_preferences preferences { get; }
		public update_state(ISettingsService settings, string? installation = null, string? legacy_path = null) {
			this.key = update_protocol.installation_key(installation ?? update_protocol.installed_path);
			this.section = settings.GetOrCreateSection(update_state.settings_guid);
			var legacy = new update_preferences();
			string previous = legacy_path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy.CallGraph", "Updates", this.key, "preferences.json");
			try { if (File.Exists(previous) && new FileInfo(previous).Length <= 8192) legacy = JsonSerializer.Deserialize<update_preferences>(File.ReadAllText(previous)) ?? legacy; } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
			this.preferences = new update_preferences { ignored_version = this.section.Attribute<string>("IgnoredVersion_" + this.key) ?? legacy.ignored_version, seen_version = this.section.Attribute<string>("SeenVersion_" + this.key) ?? legacy.seen_version };
			this.save();
		}
		public bool should_offer(update_release release) => release.version > update_protocol.current_version && this.preferences.ignored_version != update_protocol.format(release.version);
		public bool should_show_notes => this.preferences.seen_version != update_protocol.format(update_protocol.current_version);
		public void ignore(update_release release) { this.preferences.ignored_version = update_protocol.format(release.version); this.save(); }
		public void mark_notes_seen() { this.preferences.seen_version = update_protocol.format(update_protocol.current_version); this.save(); }
		void save() { this.section.Attribute("IgnoredVersion_" + this.key, this.preferences.ignored_version); this.section.Attribute("SeenVersion_" + this.key, this.preferences.seen_version); }
	}
}
