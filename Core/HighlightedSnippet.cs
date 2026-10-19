using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.CallGraph.Core {
	sealed class SnippetLimitReachedException : Exception { }

	readonly struct ColoredTextSpan {
		public int Start { get; }
		public int Length { get; }
		public TextColor Color { get; }
		public object? Reference { get; }

		public ColoredTextSpan(int start, int length, TextColor color, object? reference = null) {
			Start = start;
			Length = length;
			Color = color;
			Reference = reference;
		}
	}

	sealed class HighlightedSnippet {
		public string Text { get; }
		public IReadOnlyList<ColoredTextSpan> Spans { get; }

		public HighlightedSnippet(string text, IReadOnlyList<ColoredTextSpan> spans) {
			Text = text ?? string.Empty;
			Spans = spans ?? Array.Empty<ColoredTextSpan>();
		}

		public static HighlightedSnippet Plain(string text, TextColor color = TextColor.Text) =>
			new HighlightedSnippet(text, text.Length == 0
				? Array.Empty<ColoredTextSpan>()
				: new[] { new ColoredTextSpan(0, text.Length, color) });

		public HighlightedSnippet LimitLines(int maximum_lines) {
			if (maximum_lines <= 0)
				return this;
			int line = 1;
			int end = Text.Length;
			for (int i = 0; i < Text.Length; i++) {
				if (Text[i] != '\n')
					continue;
				if (++line > maximum_lines) {
					end = i;
					break;
				}
			}
			if (end == Text.Length)
				return this;
			int total_lines = Text.Length == 0 ? 0 : 1 + Text.Count(character => character == '\n');
			int hidden_lines = Math.Max(1, total_lines - maximum_lines);
			while (end > 0 && (Text[end - 1] == '\r' || Text[end - 1] == '\n'))
				end--;
			string suffix = Environment.NewLine + UiText.MoreLines(hidden_lines);
			var spans = Spans
				.Where(span => span.Start < end)
				.Select(span => new ColoredTextSpan(span.Start, Math.Min(span.Length, end - span.Start), span.Color, span.Reference))
				.ToList();
			spans.Add(new ColoredTextSpan(end, suffix.Length, TextColor.Comment));
			return new HighlightedSnippet(Text.Substring(0, end) + suffix, spans);
		}
	}

		sealed class HighlightedDecompilerOutput : IDecompilerOutput {
		readonly StringBuilder builder = new StringBuilder();
		readonly List<ColoredTextSpan> spans = new List<ColoredTextSpan>();
		readonly Indenter indenter = new Indenter(4, 4, true);
		readonly int maximum_lines;
		bool add_indent = true;
		bool line_has_visible_text;
		int completed_visible_lines;
		bool truncated;

		public int Length => builder.Length;
		public int NextPosition => builder.Length + (add_indent ? indenter.String.Length : 0);
		public bool UsesCustomData => false;

		public HighlightedDecompilerOutput(int maximum_lines = 0) => this.maximum_lines = Math.Max(0, maximum_lines);

		public void IncreaseIndent() => indenter.IncreaseIndent();
		public void DecreaseIndent() => indenter.DecreaseIndent();

		public void WriteLine() {
			
			
			
			if (!line_has_visible_text) {
				add_indent = true;
				return;
			}
			builder.AppendLine();
			completed_visible_lines++;
			add_indent = true;
			line_has_visible_text = false;
		}

		public void Write(string text, object color) => AddText(text, 0, text.Length, null, color);
		public void Write(string text, int index, int length, object color) => AddText(text, index, length, null, color);
		public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) =>
			AddText(text, 0, text.Length, reference, color);
		public void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) =>
			AddText(text, index, length, reference, color);

		public void AddCustomData<TData>(string id, TData data) { }

		void AddText(string text, int index, int length, object? reference, object color) {
			if (length == 0)
				return;
			var text_color = color is TextColor detected_color ? detected_color : TextColor.Text;
			if (IsComment(text_color))
				return;
			if (maximum_lines > 0 && completed_visible_lines >= maximum_lines) {
				truncated = true;
				throw new SnippetLimitReachedException();
			}
			if (add_indent) {
				add_indent = false;
				Append(indenter.String, TextColor.Text, null);
			}
			string fragment = index == 0 && length == text.Length ? text : text.Substring(index, length);
			Append(fragment, text_color, reference);
			line_has_visible_text = true;
		}

		static bool IsComment(TextColor color) =>
			color == TextColor.Comment ||
			(color >= TextColor.XmlDocCommentAttributeName && color <= TextColor.XmlDocCommentText);

		void Append(string text, TextColor color, object? reference) {
			if (text.Length == 0)
				return;
			int start = builder.Length;
			builder.Append(text);
			if (spans.Count != 0) {
				var previous = spans[spans.Count - 1];
				if (previous.Color == color && ReferenceEquals(previous.Reference, reference) && previous.Start + previous.Length == start) {
					spans[spans.Count - 1] = new ColoredTextSpan(previous.Start, previous.Length + text.Length, color, reference);
					return;
				}
			}
			spans.Add(new ColoredTextSpan(start, text.Length, color, reference));
		}

		public HighlightedSnippet CreateSnippet() {
			if (builder.Length == 0)
				return HighlightedSnippet.Plain(UiText.NoMethodBody, TextColor.Text);
			string text = builder.ToString().TrimEnd('\r', '\n');
			var result_spans = spans
				.Where(span => span.Start < text.Length)
				.Select(span => new ColoredTextSpan(span.Start,
					Math.Min(span.Length, text.Length - span.Start), span.Color, span.Reference))
				.ToList();
			if (truncated) {
				string suffix = Environment.NewLine + UiText.MethodTruncated;
				result_spans.Add(new ColoredTextSpan(text.Length, suffix.Length, TextColor.Comment));
				text += suffix;
			}
			return new HighlightedSnippet(text, result_spans);
		}
	}
}
