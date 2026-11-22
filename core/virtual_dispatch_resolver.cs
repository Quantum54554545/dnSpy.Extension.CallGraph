using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	static class virtual_dispatch_resolver {
		public static MethodDef? resolve(MethodDef declaration, TypeDef type, Func<ITypeDefOrRef?, TypeDef?> resolve_type,
			IMethod? call_reference = null, ITypeDefOrRef? receiver_type = null) {
			var hierarchy = virtual_dispatch_resolver.hierarchy(type, resolve_type).ToArray();
			var instances = new Dictionary<TypeDef, ITypeDefOrRef>();
			ITypeDefOrRef? current_reference = receiver_type ?? type;
			foreach (var current in hierarchy) {
				instances[current] = current_reference ?? current;
				var arguments = (current_reference as TypeSpec)?.TypeSig is GenericInstSig instance ? instance.GenericArguments : null;
				current_reference = current.BaseType is TypeSpec parent ? new TypeSpecUser(call_signature.substitute(parent.TypeSig, arguments)) : current.BaseType;
			}
			MethodSig signature(IMethod method) => ReferenceEquals(method, declaration) && call_reference is not null ? call_signature.@for(call_reference) :
				method.DeclaringType is TypeDef owner && instances.TryGetValue(owner, out var instance) ? call_signature.for_type(method, instance) : call_signature.@for(method);
			bool match(IMethod left, IMethod right) => left.Name == right.Name && new SigComparer().Equals(signature(left), signature(right));
			bool same(IMethod left, IMethod right) {
				if (ReferenceEquals(left, right)) return true;
				if (!match(left, right)) return false;
				var left_owner = resolve_type(left.DeclaringType); var right_owner = resolve_type(right.DeclaringType);
				return left_owner is not null && ReferenceEquals(left_owner, right_owner);
			}
			if (declaration.DeclaringType.IsInterface) {
				foreach (var current in hierarchy) {
					var explicit_methods = current.Methods.Where(method => !method.IsStatic && method.Overrides.Any(item => same(item.MethodDeclaration, declaration))).Take(2).ToArray();
					if (explicit_methods.Length > 1) return null;
					if (explicit_methods.Length == 1) return virtual_dispatch_resolver.resolve_slot(explicit_methods[0], hierarchy, resolve_type, match, same);
				}
				int owner = Array.FindIndex(hierarchy, current => current.Interfaces.Any(item => virtual_dispatch_resolver.has_interface(item.Interface, declaration.DeclaringType, resolve_type)));
				if (owner < 0) return null;
				for (int index = owner; index < hierarchy.Length; index++) {
					var matches = hierarchy[index].Methods.Where(method => method.IsPublic && method.IsVirtual && !method.IsStatic && match(method, declaration)).Take(2).ToArray();
					if (matches.Length > 1) return null;
					if (matches.Length == 1) return virtual_dispatch_resolver.resolve_slot(matches[0], hierarchy, resolve_type, match, same);
				}
				var interfaces = new HashSet<TypeDef>(); var pending = new Queue<ITypeDefOrRef>(hierarchy.SelectMany(current => current.Interfaces).Select(item => item.Interface));
				while (pending.Count != 0 && interfaces.Count < 256) {
					var current = resolve_type(pending.Dequeue()); if (current is null || !interfaces.Add(current)) continue;
					foreach (var item in current.Interfaces) pending.Enqueue(item.Interface);
				}
				if (pending.Count != 0) return null;
				var defaults = interfaces.SelectMany(current => current.Methods.Where(method => !method.IsStatic &&
					(ReferenceEquals(method, declaration) || method.Overrides.Any(item => same(item.MethodDeclaration, declaration))))).ToArray();
				var most_specific = defaults.Where(method => !defaults.Any(other => !ReferenceEquals(other.DeclaringType, method.DeclaringType) &&
					virtual_dispatch_resolver.has_interface(other.DeclaringType, method.DeclaringType, resolve_type))).Take(2).ToArray();
				return most_specific.Length == 1 && !most_specific[0].IsAbstract && most_specific[0].HasBody ? most_specific[0] : null;
			}
			return hierarchy.Contains(declaration.DeclaringType) ? virtual_dispatch_resolver.resolve_slot(declaration, hierarchy, resolve_type, match, same) : null;
		}

		static MethodDef? resolve_slot(MethodDef slot, TypeDef[] hierarchy, Func<ITypeDefOrRef?, TypeDef?> resolve_type,
			Func<IMethod, IMethod, bool> matches, Func<IMethod, IMethod, bool> same) {
			if (slot.IsFinal || !slot.IsVirtual) return slot.IsAbstract ? null : slot;
			var root = virtual_dispatch_resolver.slot_root(slot, resolve_type, matches);
			foreach (var current in hierarchy) {
				var explicit_methods = current.Methods.Where(method => !method.IsStatic && method.Overrides.Any(item => same(item.MethodDeclaration, slot) || same(item.MethodDeclaration, root))).Take(2).ToArray();
				if (explicit_methods.Length > 1) return null;
				if (explicit_methods.Length == 1) return explicit_methods[0].IsAbstract ? null : explicit_methods[0];
				var candidates = current.Methods.Where(method => !method.IsStatic && method.IsVirtual && matches(method, slot) && same(virtual_dispatch_resolver.slot_root(method, resolve_type, matches), root)).Take(2).ToArray();
				if (candidates.Length > 1) return null;
				if (candidates.Length == 1) return candidates[0].IsAbstract ? null : candidates[0];
			}
			return slot.IsAbstract ? null : slot;
		}

		static MethodDef slot_root(MethodDef method, Func<ITypeDefOrRef?, TypeDef?> resolve_type, Func<IMethod, IMethod, bool> matches) {
			var seen = new HashSet<MethodDef>();
			while (!method.IsNewSlot && method.IsVirtual && seen.Count < 256 && seen.Add(method)) {
				var parent = virtual_dispatch_resolver.hierarchy(resolve_type(method.DeclaringType.BaseType), resolve_type)
					.Select(type => type.Methods.FirstOrDefault(candidate => candidate.IsVirtual && !candidate.IsStatic && matches(candidate, method))).FirstOrDefault(candidate => candidate is not null);
				if (parent is null) break;
				method = parent;
			}
			return method;
		}

		static IEnumerable<TypeDef> hierarchy(TypeDef? type, Func<ITypeDefOrRef?, TypeDef?> resolve_type) {
			var seen = new HashSet<TypeDef>();
			while (type is not null && seen.Count < 256 && seen.Add(type)) { yield return type; type = resolve_type(type.BaseType); }
		}
		static bool has_interface(ITypeDefOrRef reference, TypeDef target, Func<ITypeDefOrRef?, TypeDef?> resolve_type) {
			var pending = new Queue<ITypeDefOrRef>(); var seen = new HashSet<TypeDef>(); pending.Enqueue(reference);
			while (pending.Count != 0 && seen.Count < 256) {
				var current = resolve_type(pending.Dequeue());
				if (current is null || !seen.Add(current)) continue;
				if (ReferenceEquals(current, target)) return true;
				foreach (var item in current.Interfaces) pending.Enqueue(item.Interface);
			}
			return false;
		}
		static bool matches(IMethod left, IMethod right) => left.Name == right.Name && new SigComparer().Equals(left.MethodSig, right.MethodSig);
		static bool same_method(IMethod left, IMethod right) => ReferenceEquals(left, right) || virtual_dispatch_resolver.matches(left, right) && new SigComparer().Equals(left.DeclaringType, right.DeclaringType);
	}
}
