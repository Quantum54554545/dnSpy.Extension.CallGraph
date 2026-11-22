using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CallGraph.Validation {
	static class fixtures {
		public static int field;
		[MethodImpl(MethodImplOptions.NoInlining)] public static void leaf() { }
		[MethodImpl(MethodImplOptions.NoInlining)] public static void other_leaf() { }
		public static void direct() { fixtures.leaf(); fixtures.leaf(); }
		public static void branch(bool flag) { if (flag) fixtures.leaf(); else fixtures.other_leaf(); }
		public static void known_branch() => fixtures.branch(true);
		public static void @virtual() { @base value = new derived(); value.run(); }
		public static void new_slot() { @base value = new hidden(); value.run(); }
		public static void unknown_virtual(@base value) => value.run();
		public static void inherited_interface(i_inherited value) => value.run();
		public static void @interface() { i_run value = new @explicit(); value.run(); }
		public static void generic_interface() { i_value<int> value = new int_value(); value.go(42); }
		public static void unknown_generic_interface(i_value<int> value) => value.go(42);
		public static void generic_instance() { i_value<int> value = new generic_value<int>(); value.go(42); }
		public static void constrained() { var value = new global::CallGraph.Validation.value(); fixtures.invoke_struct(ref value); }
		public static void invoke_struct<type_parameter>(ref type_parameter value) where type_parameter : struct, i_run => value.run();
		public static void @delegate() { Action action = fixtures.leaf; action(); }
		public static void unknown_delegate(Action action) { action(); fixtures.leaf(); }
		public static void reflection() => typeof(fixtures).GetMethod(nameof(fixtures.leaf), BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
		public static void reflection_only() => typeof(fixtures).GetMethod(nameof(fixtures.reflection_only_target), BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
		public static void reflection_only_target() { }
		public static void reflection_name_only() => _ = typeof(fixtures).GetMethod(nameof(fixtures.reflection_only_target), BindingFlags.Public | BindingFlags.Static);
		public static unsafe void pointer() { delegate* managed<void> target = &fixtures.leaf; target(); }
		public static unsafe void pointer_and_sibling() { delegate* managed<void> target = &fixtures.leaf; target(); fixtures.other_leaf(); }
		public static unsafe void partial_pointer(bool flag, delegate* managed<void> unknown) { delegate* managed<void> target = flag ? &fixtures.leaf : unknown; target(); }
		public static unsafe void unknown_pointer(delegate* managed<void> target) { target(); fixtures.leaf(); }
		public static unsafe void field_pointer() => ((delegate* managed<void>)pointer_state.function)();
		public static unsafe void mutated_pointer() => ((delegate* managed<void>)fixtures.select_pointer(41))();
		public static unsafe nint select_pointer(int key) => (((key ^ 123) * 3 + 5) & 3) switch {
			0 => (nint)(delegate* managed<void>)&fixtures.other_leaf,
			1 => (nint)(delegate* managed<void>)&fixtures.other_leaf,
			_ => (nint)(delegate* managed<void>)&fixtures.leaf
		};
		public static void exception() { try { fixtures.@throw(); } catch (InvalidOperationException) { fixtures.leaf(); } finally { fixtures.other_leaf(); } }
		public static void @throw() => throw new InvalidOperationException();
		public static void initializer() { fixtures.field = initialized.value; }
		public static async Task async() { await Task.Yield(); fixtures.leaf(); }
		public static void schedule() => Task.Run(fixtures.leaf);
		public static void schedule_via_wrapper() => fixtures.schedule();
		public static void async_via_wrapper() => fixtures.async();
		public static void recursive_a(int count) { fixtures.field = count; if (count > 0) fixtures.recursive_b(count - 1); fixtures.leaf(); }
		public static void recursive_b(int count) { if (count > 0) fixtures.recursive_a(count - 1); fixtures.other_leaf(); }
		public static void unknown_recursion(int count) => fixtures.recursive_a(count);
		public static void loader_failure() => throw new BadImageFormatException();
		public static void after_loader_failure() { fixtures.loader_failure(); fixtures.leaf(); fixtures.other_leaf(); }
		public static void after_failed_initializer() { failed_initializer.enter(); fixtures.other_leaf(); }
		public static void crypto_guard() { if (crypto_state.data is null) throw new InvalidOperationException(); fixtures.leaf(); fixtures.crypto_inner(); }
		public static void crypto_inner() { fixtures.other_leaf(); }
		[DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")] public static extern uint get_current_process_id();
		public static void native_boundary() { fixtures.get_current_process_id(); fixtures.other_leaf(); }
		public static void default_interface() { i_default_base value = new default_receiver(); value.run(); }
		public static void reflection_declared_only() => typeof(reflection_child).GetMethod(nameof(reflection_parent.inherited_only), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.Invoke(null, null);
		public static Action recursive_factory(int count) => count == 0 ? fixtures.leaf : fixtures.recursive_factory(count - 1);
		public static void recursive_delegate() => fixtures.recursive_factory(1)();
		public static void unknown_recursive_delegate(int count) => fixtures.recursive_factory(count)();
		public static Action mutual_factory_a(int count) => count == 0 ? fixtures.leaf : fixtures.mutual_factory_b(count - 1);
		public static Action mutual_factory_b(int count) => count == 0 ? fixtures.other_leaf : fixtures.mutual_factory_a(count - 1);
		public static void mutual_delegate() => fixtures.mutual_factory_a(2)();
		public static void unknown_mutual_delegate(int count) => fixtures.mutual_factory_a(count)();
		public static unsafe nint recursive_pointer_factory(int count) => count == 0 ? (nint)(delegate* managed<void>)&fixtures.leaf : fixtures.recursive_pointer_factory(count - 1);
		public static unsafe void recursive_pointer(int count) => ((delegate* managed<void>)fixtures.recursive_pointer_factory(count))();
		public static Action never_returns() => fixtures.never_returns();
		public static void non_returning_delegate() => fixtures.never_returns()();
		public static unsafe void recursive_heap_factory(int count, pointer_box box) { if (count == 0) box.function = (nint)(delegate* managed<void>)&fixtures.leaf; else fixtures.recursive_heap_factory(count - 1, box); }
		public static unsafe void recursive_heap(int count) { var box = new pointer_box(); fixtures.recursive_heap_factory(count, box); ((delegate* managed<void>)box.function)(); }
	}
	sealed class pointer_box { public nint function; }
	static class failed_initializer {
		static failed_initializer() => throw new BadImageFormatException();
		public static void enter() => fixtures.leaf();
	}
	static class pointer_state {
		public static readonly nint function;
		static unsafe pointer_state() => pointer_state.function = (nint)(delegate* managed<void>)&fixtures.leaf;
	}
	static class crypto_state {
		public static readonly byte[]? data;
		static crypto_state() { crypto_state.data = null; }
	}
	class @base { public virtual void run() => fixtures.leaf(); }
	sealed class derived : @base { public override void run() => fixtures.other_leaf(); }
	class hidden : @base { public new virtual void run() => fixtures.other_leaf(); }
	sealed class hidden_derived : hidden { public override void run() => fixtures.other_leaf(); }
	interface i_run { void run(); }
	interface i_inherited { void run(); }
	class inherited_base { public void run() => fixtures.leaf(); }
	sealed class inherited_receiver : inherited_base, i_inherited { }
	sealed class @explicit : i_run { void i_run.run() => fixtures.leaf(); }
	interface i_value<type_parameter> { void go(type_parameter value); }
	sealed class int_value : i_value<int> { public void go(int value) => fixtures.leaf(); }
	sealed class generic_value<type_parameter> : i_value<type_parameter> { public void go(type_parameter value) => fixtures.other_leaf(); }
	struct value : i_run { public void run() => fixtures.leaf(); }
	interface i_default_base { void run() => fixtures.leaf(); }
	interface i_default_derived : i_default_base { void i_default_base.run() => fixtures.other_leaf(); }
	sealed class default_receiver : i_default_derived { }
	class reflection_parent { public static void inherited_only() => fixtures.leaf(); }
	sealed class reflection_child : reflection_parent { }
	static class initialized {
		public static readonly int value;
		static initialized() { fixtures.leaf(); initialized.value = 42; }
	}
}
