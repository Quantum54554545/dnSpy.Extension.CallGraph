using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using dnlib.DotNet;
using System.Text.Json.Serialization;

namespace dnSpy.CallGraph.Core {
	sealed record research_annotation(
		[property: JsonPropertyName("Alias")] string alias = "",
		[property: JsonPropertyName("Note")] string note = "",
		[property: JsonPropertyName("Group")] string group = "",
		[property: JsonPropertyName("Compact")] bool compact = false
	);
	sealed record research_position(
		[property: JsonPropertyName("X")] double x,
		[property: JsonPropertyName("Y")] double y,
		[property: JsonPropertyName("Manual")] bool manual = false
	);
	sealed class research_document {
		[JsonPropertyName("Version")]
		public int version { get; set; } = 1;
		[JsonPropertyName("Methods")]
		public Dictionary<string, research_annotation> methods { get; set; } = new();
		[JsonPropertyName("Positions")]
		public Dictionary<string, Dictionary<string, research_position>> positions { get; set; } = new();
	}
	sealed class research_store {
		static readonly Lazy<research_store> default_store = new(() => new research_store());
		public static research_store @default => research_store.default_store.Value;
		readonly string path;
		research_document document = new();
		bool readable = true;
		public string? last_error { get; private set; }
		public research_store(string? path = null) {
			this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy.CallGraph", "research-v1.json");
			try { if (File.Exists(this.path)) { this.document = JsonSerializer.Deserialize<research_document>(File.ReadAllText(this.path)) ?? throw new InvalidDataException("Empty research document"); if (this.document.version != 1 || this.document.methods is null || this.document.positions is null) throw new InvalidDataException("Unsupported research document"); } }
			catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { this.readable = false; this.last_error = error.Message; }
		}
		public static string key(MethodDef method) => $"{method.Module?.Mvid}:{method.MDToken.Raw:X8}:{call_graph_index.signature(method)}";
		public research_annotation get(MethodDef method) => this.document.methods.GetValueOrDefault(research_store.key(method)) ?? new research_annotation();
		public void set(MethodDef method, research_annotation value) => this.document.methods[research_store.key(method)] = value;
		public IReadOnlyDictionary<string, research_annotation> snapshot() => new Dictionary<string, research_annotation>(this.document.methods);
		public void remove_group(string name) { foreach (var key in new List<string>(this.document.methods.Keys)) if (this.document.methods[key].group == name) this.document.methods[key] = this.document.methods[key] with { group = "" }; }
		public IReadOnlyDictionary<string, research_position> positions(MethodDef root) => this.document.positions.GetValueOrDefault(research_store.key(root)) ?? new Dictionary<string, research_position>();
		public void set_positions(MethodDef root, Dictionary<string, research_position> positions) => this.document.positions[research_store.key(root)] = positions;
		public bool save() {
			if (!this.readable) return false;
			string temporary = this.path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(this.path))!);
				File.WriteAllText(temporary, JsonSerializer.Serialize(this.document, new JsonSerializerOptions { WriteIndented = true }));
				File.Move(temporary, this.path, true); this.last_error = null; return true;
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException) { this.last_error = error.Message; return false; }
			finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
		}
	}
}
