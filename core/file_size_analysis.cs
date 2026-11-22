using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;

namespace dnSpy.CallGraph.Core {
	sealed record size_range(long offset, long length) { public long end => this.offset + this.length; }
	sealed record size_method_reference(string path, Guid mvid, int token, string type_name, string name, uint rva) {
		public string title => file_size_analysis.label(this.type_name + "::" + this.name) + $" · 0x{this.token:X8}";
	}
	sealed class size_item {
		readonly Dictionary<string, size_item> groups = new(StringComparer.Ordinal);
		readonly List<size_range> spans = new();
		public string name { get; }
		public string kind { get; }
		public string key { get; }
		public string detail { get; }
		public IReadOnlyList<size_method_reference> method_references { get; }
		public size_item? parent { get; }
		public IReadOnlyList<size_item> children { get; private set; } = Array.Empty<size_item>();
		public IReadOnlyList<size_range> ranges => this.spans;
		public long bytes { get; private set; }
		public string size_text => file_size_analysis.format_size(this.bytes);
		public string bytes_text => this.bytes.ToString("N0");
		public string percent_text => (this.parent?.bytes > 0 ? 100.0 * this.bytes / this.parent.bytes : 100).ToString("F2") + "%";
		public size_item(string name, string kind, string detail = "", size_item? parent = null, IReadOnlyList<size_method_reference>? method_references = null, string key = "") { this.name = name; this.kind = kind; this.key = key; this.detail = detail; this.parent = parent; this.method_references = method_references ?? Array.Empty<size_method_reference>(); }
		internal static size_item collection(string name, IEnumerable<size_item> items, size_item? parent = null) {
			var children = items.Where(item => item.bytes > 0).OrderByDescending(item => item.bytes).ThenBy(item => item.name, StringComparer.Ordinal).ToArray();
			return new size_item(name, ui_text.pick("Group", "Группа"), parent: parent) { children = children, bytes = checked(children.Sum(item => item.bytes)) };
		}
		internal size_item group(string key, string name, string kind, string detail = "", IReadOnlyList<size_method_reference>? method_references = null) {
			if (!this.groups.TryGetValue(key, out var item)) this.groups[key] = item = new size_item(name, kind, detail, this, method_references, key);
			return item;
		}
		internal void add(long offset, long length) {
			if (length <= 0) return;
			if (this.spans.Count != 0 && this.spans[^1].end == offset) this.spans[^1] = this.spans[^1] with { length = this.spans[^1].length + length };
			else this.spans.Add(new size_range(offset, length));
		}
		internal void finish(CancellationToken token) {
			token.ThrowIfCancellationRequested();
			foreach (var item in this.groups.Values) item.finish(token);
			this.children = this.groups.Values.Where(item => item.bytes != 0).OrderByDescending(item => item.bytes).ThenBy(item => item.name, StringComparer.Ordinal).ToArray();
			this.bytes = checked(this.spans.Sum(span => span.length) + this.children.Sum(item => item.bytes)); this.groups.Clear();
		}
	}
	sealed record file_size_report(size_item root, string path, long file_length, IReadOnlyList<string> warnings, int method_count, int shared_body_count);
	static class file_size_analysis {
		internal sealed record part(string key, string name, string kind, string detail = "", IReadOnlyList<size_method_reference>? method_references = null);
		internal sealed record claim(long start, long end, part[] path, bool physical);
		sealed record section(long offset, long length, long rva, long virtual_size, part part);
		const int claim_limit = 500000;
		public static string format_size(long bytes) {
			string[] units = { "B", "KiB", "MiB", "GiB", "TiB" }; double value = bytes; int unit = 0;
			while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
			return value.ToString(unit == 0 ? "N0" : "N2") + " " + units[unit];
		}
		internal static string label(string value) {
			var result = new StringBuilder(Math.Min(value.Length, 300));
			foreach (char character in value) { if (result.Length >= 300) { result.Append('…'); break; } result.Append(char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format ? '·' : character); }
			return result.ToString();
		}
		public static file_size_report analyze(string path, CancellationToken token = default) {
			token.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
			long length = stream.Length; var warnings = new List<string>(); var claims = new List<claim>(); var sections = new List<section>(); int methods = 0, shared = 0;
			string text(string en, string ru) => ui_text.pick(en, ru);
			void warn(string value) { if (warnings.Count < 100) warnings.Add(value); }
			void add(long offset, long count, bool physical, params part[] parts) {
				if (offset < 0 || count <= 0) return;
				if (offset >= length) { warn(text("Region outside file: ", "Область за концом файла: ") + parts[^1].name); return; }
				long end = offset + Math.Min(count, length - offset);
				if (count > length - offset) warn(text("Truncated region: ", "Усечённая область: ") + parts[^1].name);
				if (claims.Count >= claim_limit) throw new InvalidDataException(text("Classification limit reached; remaining bytes are retained without attribution.", "Достигнут лимит классификации; оставшиеся байты сохранены без атрибуции."));
				claims.Add(new claim(offset, end, parts, physical));
			}
			long map(long rva, long count) {
				var matches = sections.Where(item => rva >= item.rva && rva - item.rva < item.length && count <= item.length - (rva - item.rva)).Take(2).ToArray();
				if (matches.Length != 1) throw new BadImageFormatException(text("Unmapped or ambiguous RVA: ", "Неоднозначный или отсутствующий RVA: ") + rva.ToString("X"));
				return checked(matches[0].offset + rva - matches[0].rva);
			}
			byte[] read(long offset, int count) {
				if (offset < 0 || count < 0 || offset > length - count) throw new BadImageFormatException("Truncated file range");
				token.ThrowIfCancellationRequested(); var data = new byte[count]; stream.Position = offset; stream.ReadExactly(data); return data;
			}
			void directory(DirectoryEntry entry, params part[] parts) { if (entry.Size > 0 && entry.RelativeVirtualAddress != 0) add(map((uint)entry.RelativeVirtualAddress, (uint)entry.Size), (uint)entry.Size, false, parts); }
			void attempt(Action action, string context) { try { action(); } catch (Exception error) when (error is BadImageFormatException or IOException or ArgumentException or InvalidOperationException or OverflowException) { warn(context + ": " + error.Message); } }
			try {
				using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
				var headers = pe.PEHeaders; var optional = headers.PEHeader ?? throw new BadImageFormatException("No PE optional header");
				long actual_headers = headers.CoffHeaderStartOffset + 20L + headers.CoffHeader.SizeOfOptionalHeader + headers.SectionHeaders.Length * 40L;
				add(0, Math.Max(actual_headers, (uint)optional.SizeOfHeaders), true, new part("headers", text("PE headers", "Заголовки PE"), "PE"));
				int section_number = 0;
				foreach (var header in headers.SectionHeaders) {
					token.ThrowIfCancellationRequested(); var item = new section((uint)header.PointerToRawData, (uint)header.SizeOfRawData, (uint)header.VirtualAddress, (uint)header.VirtualSize,
						new part("section:" + section_number++, label(header.Name), text("Section", "Секция"), $"RVA 0x{(uint)header.VirtualAddress:X8} · VirtualSize {(uint)header.VirtualSize:N0} · RawSize {(uint)header.SizeOfRawData:N0}"));
					sections.Add(item); add(item.offset, item.length, true, item.part);
					if (item.virtual_size > item.length) warn(text("Virtual zero-fill is not part of the disk size: ", "Виртуальное заполнение нулями не входит в размер на диске: ") + item.part.name + " · " + format_size(item.virtual_size - item.length));
					if (item.length > item.virtual_size && item.virtual_size != 0) add(item.offset + item.virtual_size, item.length - item.virtual_size, false, new part("raw-tail", text("Raw section tail / alignment", "Хвост секции / выравнивание"), "PE"));
				}
				var pe_directories = new[] {
					(optional.ExportTableDirectory, "Export"), (optional.ImportTableDirectory, "Import"), (optional.ResourceTableDirectory, "Win32 resources"),
					(optional.ExceptionTableDirectory, "Exception / unwind"), (optional.BaseRelocationTableDirectory, "Relocations"), (optional.DebugTableDirectory, "Debug directory"),
					(optional.ThreadLocalStorageTableDirectory, "TLS"), (optional.LoadConfigTableDirectory, "Load config"), (optional.BoundImportTableDirectory, "Bound import"),
					(optional.ImportAddressTableDirectory, "IAT"), (optional.DelayImportTableDirectory, "Delay import"), (optional.CorHeaderTableDirectory, "CLR header") };
				foreach (var (entry, name) in pe_directories) attempt(() => directory(entry, new part("pe-dir:" + name, name, text("PE directory", "Каталог PE"))), name);
				if (optional.CertificateTableDirectory.Size > 0) add((uint)optional.CertificateTableDirectory.RelativeVirtualAddress, (uint)optional.CertificateTableDirectory.Size, false, new part("certificate", text("Authenticode certificate table", "Таблица сертификатов Authenticode"), "PE"));
				attempt(() => { foreach (var entry in pe.ReadDebugDirectory()) { token.ThrowIfCancellationRequested(); if (entry.DataSize > 0) add((uint)entry.DataPointer, (uint)entry.DataSize, false, new part("debug-data:" + entry.DataPointer, entry.Type.ToString(), text("Debug data", "Отладочные данные"))); } }, "Debug");
				if (headers.CorHeader is not null) {
					var cor = headers.CorHeader;
					attempt(() => directory(cor.StrongNameSignatureDirectory, new part("strong-name", text("Strong-name signature", "Подпись strong name"), "CLR")), "Strong name");
					attempt(() => directory(cor.ManagedNativeHeaderDirectory, new part("managed-native", text("Managed native header / ReadyToRun", "Managed native header / ReadyToRun"), "CLR")), "Managed native");
					attempt(() => directory(cor.VtableFixupsDirectory, new part("vtable", "VTable fixups", "CLR")), "VTable");
					attempt(() => directory(cor.CodeManagerTableDirectory, new part("code-manager", "Code manager table", "CLR")), "Code manager");
					attempt(() => directory(cor.ExportAddressTableJumpsDirectory, new part("export-jumps", "Export address jumps", "CLR")), "Export jumps");
					attempt(() => directory(cor.ResourcesDirectory, new part("resources", text("Managed resources", "Managed-ресурсы"), "CLR")), "Managed resources directory");
					var metadata_part = new part("metadata", text("CLR metadata", "Метаданные CLR"), "CLR");
					attempt(() => {
						long start = map((uint)cor.MetadataDirectory.RelativeVirtualAddress, (uint)cor.MetadataDirectory.Size); long end = start + (uint)cor.MetadataDirectory.Size;
						add(start, (uint)cor.MetadataDirectory.Size, false, metadata_part);
						var prefix = read(start, 16); if (BitConverter.ToUInt32(prefix, 0) != 0x424A5342) throw new BadImageFormatException("Invalid metadata signature");
						long cursor = start + 16 + BitConverter.ToUInt32(prefix, 12); cursor = start + ((cursor - start + 3) & ~3L);
						if (cursor > end - 4) throw new BadImageFormatException("Invalid metadata version length");
						int streams = BitConverter.ToUInt16(read(cursor, 4), 2); cursor += 4;
						for (int position = 0; position < streams; position++) {
							if (cursor > end - 8) throw new BadImageFormatException("Truncated metadata stream header");
							var data = read(cursor, 8); cursor += 8; var name = new StringBuilder(); int consumed = 0;
							while (true) { if (cursor + consumed >= end || consumed >= 32) throw new BadImageFormatException("Invalid metadata stream name"); byte character = read(cursor + consumed++, 1)[0]; if (character == 0) break; name.Append((char)character); }
							cursor += (consumed + 3) & ~3; long offset = BitConverter.ToUInt32(data, 0), count = BitConverter.ToUInt32(data, 4);
							if (offset > end - start || count > end - start - offset) throw new BadImageFormatException("Metadata stream exceeds directory");
							add(start + offset, count, false, metadata_part, new part("stream:" + position, label(name.ToString()), text("Metadata stream", "Поток метаданных")));
						}
					}, "Metadata streams");
					attempt(() => {
						var reader = pe.GetMetadataReader(MetadataReaderOptions.None);
						Guid mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
						long metadata_start = map((uint)cor.MetadataDirectory.RelativeVirtualAddress, (uint)cor.MetadataDirectory.Size);
						foreach (TableIndex table in Enum.GetValues<TableIndex>()) {
							int count = reader.GetTableRowCount(table); if (count == 0) continue;
							long offset = metadata_start + reader.GetTableMetadataOffset(table); long bytes = checked((long)reader.GetTableRowSize(table) * count);
							var stream_claim = claims.LastOrDefault(item => !item.physical && item.path.Length == 2 && item.path[0].key == "metadata" && item.start <= offset && item.end >= offset + bytes);
							if (stream_claim is null) { warn("Metadata table outside a recognized stream: " + table); continue; }
							add(offset, bytes, false, stream_claim.path.Append(new part("table:" + table, table + " · " + count.ToString("N0"), text("Metadata table", "Таблица метаданных"), $"{count:N0} × {reader.GetTableRowSize(table):N0} bytes")).ToArray());
						}
						var bodies = new Dictionary<int, List<(string ns, string type, int type_token, string name, int token)>>();
						foreach (var type_handle in reader.TypeDefinitions) {
							token.ThrowIfCancellationRequested(); var type = reader.GetTypeDefinition(type_handle); string type_name = label(reader.GetString(type.Name)); var names = new List<string> { type_name }; var visited = new HashSet<TypeDefinitionHandle> { type_handle }; var owner = type.GetDeclaringType(); string ns = reader.GetString(type.Namespace);
							while (!owner.IsNil && visited.Add(owner)) { var parent = reader.GetTypeDefinition(owner); names.Add(label(reader.GetString(parent.Name))); ns = reader.GetString(parent.Namespace); owner = parent.GetDeclaringType(); }
							names.Reverse(); type_name = string.Join("+", names);
							foreach (var handle in type.GetMethods()) {
								token.ThrowIfCancellationRequested(); var method = reader.GetMethodDefinition(handle); int rva = method.RelativeVirtualAddress;
								if (rva == 0 || (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL) continue;
								if (!bodies.TryGetValue(rva, out var owners)) bodies[rva] = owners = new();
								owners.Add((ns, type_name, MetadataTokens.GetToken(type_handle), reader.GetString(method.Name), MetadataTokens.GetToken(handle))); methods++;
							}
						}
						foreach (var pair in bodies) {
							token.ThrowIfCancellationRequested();
							attempt(() => {
								var body = pe.GetMethodBody(pair.Key); long offset = map((uint)pair.Key, body.Size); var method = pair.Value[0];
								var body_part = new part("bodies", text("Managed method bodies", "Тела managed-методов"), "IL");
								var references = pair.Value.Select(item => new size_method_reference(path, mvid, item.token, item.type, item.name, (uint)pair.Key)).ToArray();
								string info = $"RVA 0x{(uint)pair.Key:X8} · IL {body.GetILContent().Length:N0} · " + text("Header + IL + EH; excludes metadata", "Заголовок + IL + EH; без метаданных");
								if (pair.Value.Count == 1) add(offset, body.Size, false, body_part, new part("ns:" + method.ns, method.ns.Length == 0 ? text("<global namespace>", "<глобальное пространство>") : label(method.ns), text("Namespace", "Пространство имён")), new part("type:" + method.type_token, method.type, text("Type", "Тип")), new part("method:" + method.token, label(method.name) + $" · 0x{method.token:X8}", text("Method", "Метод"), info, references));
								else { shared++; add(offset, body.Size, false, body_part, new part("shared:" + pair.Key, text("Shared body", "Общее тело") + " · " + pair.Value.Count, "IL", info + "\n" + text("Physical body is shared; select an owner to navigate.", "Общее физическое тело; выберите владельца для перехода."), references)); }
							}, "Method RVA 0x" + ((uint)pair.Key).ToString("X8"));
						}
						attempt(() => {
							if (cor.ResourcesDirectory.Size == 0) return; long start = map((uint)cor.ResourcesDirectory.RelativeVirtualAddress, (uint)cor.ResourcesDirectory.Size); long size = (uint)cor.ResourcesDirectory.Size;
							var resource_part = new part("resources", text("Managed resources", "Managed-ресурсы"), "CLR"); add(start, size, false, resource_part);
							foreach (var handle in reader.ManifestResources) { token.ThrowIfCancellationRequested(); var resource = reader.GetManifestResource(handle); if (!resource.Implementation.IsNil) continue; long offset = resource.Offset;
								if (offset < 0 || offset > size - 4) { warn("Resource offset outside directory"); continue; } uint bytes = BitConverter.ToUInt32(read(start + offset, 4)); if (bytes > size - offset - 4) { warn("Truncated resource: " + label(reader.GetString(resource.Name))); continue; }
								add(start + offset, bytes + 4L, false, resource_part, new part("resource:" + MetadataTokens.GetToken(handle), label(reader.GetString(resource.Name)), text("Resource", "Ресурс"), text("Payload plus 4-byte length prefix", "Данные и 4-байтовый префикс длины")));
							}
						}, "Managed resources");
					}, "Managed metadata / bodies");
				}
			} catch (Exception error) when (error is BadImageFormatException or IOException or ArgumentException or InvalidOperationException or OverflowException) { warn(text("Partial classification: ", "Частичная классификация: ") + error.Message); }
			var root = partition(Path.GetFileName(path), length, claims, token, warnings);
			if (root.bytes != length) throw new InvalidDataException("File-size accounting invariant failed");
			return new file_size_report(root, path, length, warnings.ToArray(), methods, shared);
		}
		internal static size_item partition(string name, long length, IReadOnlyList<claim> claims, CancellationToken token, List<string> warnings) {
			var root = new size_item(name, ui_text.pick("File", "Файл"));
			var valid = claims.Where(item => item.start >= 0 && item.end > item.start && item.end <= length && item.path.Length != 0).ToArray();
			var events = valid.SelectMany((item, index) => new[] { (offset: item.start, index, start: true), (offset: item.end, index, start: false) }).OrderBy(item => item.offset).ThenBy(item => item.start).ToArray();
			var active = new HashSet<int>(); long cursor = 0; int position = 0; bool overlap = false;
			long section_end = valid.Where(item => item.physical).Select(item => item.end).DefaultIfEmpty(0).Max();
			while (cursor < length) {
				token.ThrowIfCancellationRequested();
				while (position < events.Length && events[position].offset == cursor) { var change = events[position++]; if (change.start) active.Add(change.index); else active.Remove(change.index); }
				long end = position < events.Length ? events[position].offset : length; if (end <= cursor) throw new InvalidDataException("Invalid range sweep");
				var physical = active.Select(index => valid[index]).Where(item => item.physical).Take(2).ToArray();
				var owner = root;
				if (physical.Length == 1) foreach (var part in physical[0].path) owner = owner.group(part.key, part.name, part.kind, part.detail, part.method_references);
				else owner = owner.group(physical.Length > 1 ? "physical-overlap" : cursor >= section_end ? "overlay" : "unmapped", physical.Length > 1 ? ui_text.pick("Overlapping PE regions", "Пересечение областей PE") : cursor >= section_end ? ui_text.pick("Overlay / outside PE sections", "Оверлей / вне секций PE") : ui_text.pick("Gaps outside PE regions", "Промежутки вне областей PE"), "PE");
				var semantic = active.Select(index => valid[index]).Where(item => !item.physical).Take(65).ToArray();
				var chosen = semantic.OrderByDescending(item => item.path.Length).FirstOrDefault();
				bool conflict = physical.Length > 1 || semantic.Length > 64 || chosen is not null && semantic.Any(item => item.path.Length > chosen.path.Length || !item.path.Select(part => part.key).SequenceEqual(chosen.path.Take(item.path.Length).Select(part => part.key)));
				if (conflict) { overlap = true; owner = owner.group("overlap", ui_text.pick("Shared / overlapping attribution", "Общая / пересекающаяся атрибуция"), "PE", ui_text.pick("Bytes counted once; conflicting claims are not assigned to a single owner.", "Байты учтены один раз; конфликтующие области не приписаны одному владельцу.") + "\n" + string.Join("\n", semantic.Take(12).Select(item => string.Join(" / ", item.path.Select(part => part.name))))); }
				else if (chosen is not null) foreach (var part in chosen.path) owner = owner.group(part.key, part.name, part.kind, part.detail, part.method_references);
				if (!conflict && (chosen is null || chosen.path[^1].key is "metadata" or "resources" || chosen.path[^1].key.StartsWith("stream:", StringComparison.Ordinal))) owner = owner.group("remainder", ui_text.pick("Remaining bytes / headers / unclassified data", "Остаток / заголовки / неклассифицированные данные"), "Raw");
				owner.add(cursor, end - cursor); cursor = end;
			}
			if (overlap && warnings.Count < 100) warnings.Add(ui_text.pick("Overlapping ranges detected; shared bytes are counted once, separately from individual owners.", "Найдены пересечения областей; общие байты учтены один раз, отдельно от отдельных владельцев."));
			root.finish(token); return root;
		}
	}
}
