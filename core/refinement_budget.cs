using System;
using System.Diagnostics;

namespace dnSpy.CallGraph.Core {
	sealed class refinement_budget {
		readonly long started = Stopwatch.GetTimestamp();
		readonly long allocated = GC.GetAllocatedBytesForCurrentThread();
		readonly TimeSpan duration;
		readonly long allocation_limit;
		readonly int step_limit;
		int steps;
		public string? exhaustion { get; private set; }
		public refinement_budget(TimeSpan? duration = null, long allocation_limit = 256L * 1024 * 1024, int step_limit = int.MaxValue) {
			this.duration = duration ?? TimeSpan.FromSeconds(3); this.allocation_limit = allocation_limit;
			this.step_limit = step_limit;
		}
		public bool consume_step() {
			if (++this.steps > this.step_limit) this.exhaustion = "target refinement instruction slice exhausted";
			return this.exhaustion is null && ((this.steps & 63) != 0 || this.can_continue());
		}
		public bool can_continue() {
			if (this.exhaustion is not null) return false;
			if (GC.GetAllocatedBytesForCurrentThread() - this.allocated >= this.allocation_limit) this.exhaustion = "target refinement allocation budget exhausted";
			else if (Stopwatch.GetElapsedTime(this.started) >= this.duration) this.exhaustion = "target refinement time budget exhausted";
			return this.exhaustion is null;
		}
	}
}
