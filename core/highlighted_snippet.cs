using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.CallGraph.Core {
	sealed class snippet_limit_reached_exception : Exception { }

	readonly struct colored_text_span {
		public int start { get; }
		public int length { get; }
		public TextColor color { get; }
		public object? reference { get; }

		public colored_text_span(int start, int length, TextColor color, object? reference = null) {
			this.start = start;
			this.length = length;
			this.color = color;
			this.reference = reference;
		}
	}

	sealed class highlighted_snippet {
		public string text { get; }
		public IReadOnlyList<colored_text_span> spans { get; }
		public bool is_truncated { get; }

		public highlighted_snippet(string text, IReadOnlyList<colored_text_span> spans, bool is_truncated = false) {
			this.text = text ?? string.Empty;
			this.spans = spans ?? Array.Empty<colored_text_span>();
			this.is_truncated = is_truncated;
		}

		public static highlighted_snippet plain(string text, TextColor color = TextColor.Text) =>
			new highlighted_snippet(text, text.Length == 0
				? Array.Empty<colored_text_span>()
				: new[] { new colored_text_span(0, text.Length, color) });
		public highlighted_snippet limit_characters(int maximum_characters) {
			if (this.text.Length <= maximum_characters) return this;
			int end = Math.Max(0, maximum_characters); if (end > 0 && char.IsHighSurrogate(this.text[end - 1])) end--;
			return new highlighted_snippet(this.text.Substring(0, end), this.spans.Where(span => span.start >= 0 && span.start < end).Select(span => new colored_text_span(span.start, Math.Min(span.length, end - span.start), span.color, span.reference)).ToArray(), true);
		}

		public highlighted_snippet limit_lines(int maximum_lines) {
			if (maximum_lines <= 0)
				return this;
			int line = 1;
			int end = this.text.Length;
			for (int i = 0; i < this.text.Length; i++) {
				if (this.text[i] != '\n')
					continue;
				if (++line > maximum_lines) {
					end = i;
					break;
				}
			}
			if (end == this.text.Length)
				return this;
			int total_lines = this.text.Length == 0 ? 0 : 1 + this.text.Count(character => character == '\n');
			int hidden_lines = Math.Max(1, total_lines - maximum_lines);
			while (end > 0 && (this.text[end - 1] == '\r' || this.text[end - 1] == '\n'))
				end--;
			string suffix = Environment.NewLine + ui_text.more_lines(hidden_lines);
			var spans = this.spans
				.Where(span => span.start < end)
				.Select(span => new colored_text_span(span.start, Math.Min(span.length, end - span.start), span.color, span.reference))
				.ToList();
			spans.Add(new colored_text_span(end, suffix.Length, TextColor.Comment));
			return new highlighted_snippet(this.text.Substring(0, end) + suffix, spans, true);
		}
		public highlighted_snippet preview(int maximum_rows, double width, bool wrap) {
			if (maximum_rows <= 0) return this;
			int columns = Math.Max(1, (int)((width - 22) / 6.95));
			int rows = 1, column = 0, end = this.text.Length; bool clipped_horizontally = false;
			for (int position = 0; position < this.text.Length; position++) {
				char character = this.text[position]; if (character == '\r') continue;
				if (character == '\n') { rows++; column = 0; }
				else { int advance = character == '\t' ? 4 : 1; if (wrap && column != 0 && column + advance > columns) { rows++; column = 0; } column += advance; if (!wrap && column > columns) clipped_horizontally = true; }
				if (rows > maximum_rows) { end = position; break; }
			}
			if (end == this.text.Length) return clipped_horizontally && !this.is_truncated ? new highlighted_snippet(this.text, this.spans, true) : this;
			while (end > 0 && (this.text[end - 1] == '\r' || this.text[end - 1] == '\n')) end--;
			if (end > 0 && char.IsHighSurrogate(this.text[end - 1])) end--;
			string suffix = Environment.NewLine + ui_text.preview_truncated;
			var spans = this.spans.Where(span => span.start >= 0 && span.start < end).Select(span => new colored_text_span(span.start, Math.Min(span.length, end - span.start), span.color, span.reference)).ToList();
			spans.Add(new colored_text_span(end, suffix.Length, TextColor.Comment)); return new highlighted_snippet(this.text.Substring(0, end) + suffix, spans, true);
		}
	}

		sealed class highlighted_decompiler_output : IDecompilerOutput {
		readonly StringBuilder builder = new StringBuilder();
		readonly List<colored_text_span> spans = new List<colored_text_span>();
		readonly Indenter indenter = new Indenter(4, 4, true);
		readonly int maximum_lines;
		readonly int maximum_characters;
		bool add_indent = true;
		bool line_has_visible_text;
		int completed_visible_lines;
		bool truncated;

		public int Length => this.builder.Length;
		public int NextPosition => this.builder.Length + (this.add_indent ? this.indenter.String.Length : 0);
		public bool UsesCustomData => false;

		public highlighted_decompiler_output(int maximum_lines = 0, int maximum_characters = 16000) { this.maximum_lines = Math.Max(0, maximum_lines); this.maximum_characters = Math.Max(1, maximum_characters); }

		public void IncreaseIndent() => this.indenter.IncreaseIndent();
		public void DecreaseIndent() => this.indenter.DecreaseIndent();

		public void WriteLine() {

			if (!this.line_has_visible_text) {
				this.add_indent = true;
				return;
			}
			this.builder.AppendLine();
			this.completed_visible_lines++;
			this.add_indent = true;
			this.line_has_visible_text = false;
		}

		public void Write(string text, object color) => this.add_text(text, 0, text.Length, null, color);
		public void Write(string text, int index, int length, object color) => this.add_text(text, index, length, null, color);
		public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) =>
			this.add_text(text, 0, text.Length, reference, color);
		public void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) =>
			this.add_text(text, index, length, reference, color);

		public void AddCustomData<t_data>(string id, t_data data) { }

		void add_text(string text, int index, int length, object? reference, object color) {
			if (length == 0)
				return;
			var text_color = color is TextColor detected_color ? detected_color : TextColor.Text;
			if (highlighted_decompiler_output.is_comment(text_color))
				return;
			int end = index + length;
			for (int position = index; position < end;) {
				int start = position; while (position < end && text[position] is not ('\r' or '\n')) position++;
				if (position > start) this.add_fragment(text, start, position - start, reference, text_color);
				if (position < end) { char newline = text[position++]; if (newline == '\r' && position < end && text[position] == '\n') position++; WriteLine(); }
			}
		}
		void add_fragment(string text, int index, int length, object? reference, TextColor text_color) {
			if (this.maximum_lines > 0 && this.completed_visible_lines >= this.maximum_lines || this.builder.Length >= this.maximum_characters) {
				this.truncated = true;
				throw new snippet_limit_reached_exception();
			}
			if (this.add_indent) {
				this.add_indent = false;
				string indentation = this.indenter.String; this.append(indentation.Substring(0, Math.Min(indentation.Length, this.maximum_characters - this.builder.Length)), TextColor.Text, null);
			}
			int available = Math.Min(length, this.maximum_characters - this.builder.Length);
			if (available > 0 && available < length && char.IsHighSurrogate(text[index + available - 1])) available--;
			string fragment = index == 0 && available == text.Length ? text : text.Substring(index, available);
			this.append(fragment, text_color, reference);
			this.line_has_visible_text = true;
			if (available < length) { this.truncated = true; throw new snippet_limit_reached_exception(); }
		}

		static bool is_comment(TextColor color) =>
			color == TextColor.Comment ||
			(color >= TextColor.XmlDocCommentAttributeName && color <= TextColor.XmlDocCommentText);

		void append(string text, TextColor color, object? reference) {
			if (text.Length == 0)
				return;
			int start = this.builder.Length;
			this.builder.Append(text);
			if (this.spans.Count != 0) {
				var previous = this.spans[this.spans.Count - 1];
				if (previous.color == color && ReferenceEquals(previous.reference, reference) && previous.start + previous.length == start) {
					this.spans[this.spans.Count - 1] = new colored_text_span(previous.start, previous.length + text.Length, color, reference);
					return;
				}
			}
			this.spans.Add(new colored_text_span(start, text.Length, color, reference));
		}

		public highlighted_snippet create_snippet() {
			if (this.builder.Length == 0 && !this.truncated)
				return highlighted_snippet.plain(ui_text.no_method_body, TextColor.Text);
			string text = this.builder.ToString().TrimEnd('\r', '\n');
			var result_spans = this.spans
				.Where(span => span.start < text.Length)
				.Select(span => new colored_text_span(span.start,
					Math.Min(span.length, text.Length - span.start), span.color, span.reference))
				.ToList();
			if (this.truncated) {
				string suffix = Environment.NewLine + ui_text.method_truncated;
				result_spans.Add(new colored_text_span(text.Length, suffix.Length, TextColor.Comment));
				text += suffix;
			}
			return new highlighted_snippet(text, result_spans, this.truncated);
		}
	}
}
