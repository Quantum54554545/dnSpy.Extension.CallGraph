using System;
using System.Collections;
using System.Collections.Generic;

namespace dnSpy.CallGraph.Core {
	sealed class bounded_snippet_cache<t_key> : IEnumerable<KeyValuePair<t_key, highlighted_snippet>> where t_key : notnull {
		readonly int maximum_entries;
		readonly int maximum_characters;
		readonly Dictionary<t_key, highlighted_snippet> entries = new();
		readonly Dictionary<t_key, LinkedListNode<t_key>> positions = new();
		readonly LinkedList<t_key> order = new();
		public bounded_snippet_cache(int maximum_entries, int maximum_characters) {
			if (maximum_entries <= 0 || maximum_characters <= 0) throw new ArgumentOutOfRangeException();
			this.maximum_entries = maximum_entries; this.maximum_characters = maximum_characters;
		}
		public int count => this.entries.Count;
		public long character_count { get; private set; }
		public IEnumerable<t_key> keys => this.entries.Keys;
		public bool contains_key(t_key key) => this.entries.ContainsKey(key);
		public bool try_get_value(t_key key, out highlighted_snippet value) {
			if (!this.entries.TryGetValue(key, out value!)) return false;
			var node = this.positions[key]; this.order.Remove(node); this.order.AddLast(node); return true;
		}
		public highlighted_snippet this[t_key key] {
			set {
				this.remove(key);
				var retained = value.limit_characters(this.maximum_characters);
				this.entries.Add(key, retained); this.positions.Add(key, this.order.AddLast(key)); this.character_count += retained.text.Length;
				while (this.entries.Count > this.maximum_entries || this.character_count > this.maximum_characters) this.remove(this.order.First!.Value);
			}
		}
		public bool remove(t_key key) {
			if (!this.entries.Remove(key, out var value)) return false;
			this.character_count -= value.text.Length; this.order.Remove(this.positions[key]); this.positions.Remove(key); return true;
		}
		public void clear() { this.entries.Clear(); this.positions.Clear(); this.order.Clear(); this.character_count = 0; }
		public IEnumerator<KeyValuePair<t_key, highlighted_snippet>> GetEnumerator() => this.entries.GetEnumerator();
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
