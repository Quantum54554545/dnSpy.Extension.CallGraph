using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	static class VirtualDispatchResolver {
		public static MethodDef? Resolve(MethodDef declaration, TypeDef type, Func<ITypeDefOrRef?, TypeDef?> resolve_type) {
			var hierarchy = Hierarchy(type, resolve_type).ToArray();
			if (declaration.DeclaringType.IsInterface) {
				foreach (var current in hierarchy) {
					var explicit_methods = current.Methods.Where(method => !method.IsStatic && method.Overrides.Any(item => SameMethod(item.MethodDeclaration, declaration))).Take(2).ToArray();
					if (explicit_methods.Length > 1) return null;
					if (explicit_methods.Length == 1) return ResolveSlot(explicit_methods[0], hierarchy, resolve_type);
				}
				int owner = Array.FindIndex(hierarchy, current => current.Interfaces.Any(item => HasInterface(item.Interface, declaration.DeclaringType, resolve_type)));
				if (owner < 0) return null;
				for (int index = owner; index < hierarchy.Length; index++) {
					var matches = hierarchy[index].Methods.Where(method => method.IsPublic && method.IsVirtual && !method.IsStatic && Matches(method, declaration)).Take(2).ToArray();
					if (matches.Length > 1) return null;
					if (matches.Length == 1) return ResolveSlot(matches[0], hierarchy, resolve_type);
				}
				return !declaration.IsAbstract && declaration.HasBody ? declaration : null;
			}
			return hierarchy.Contains(declaration.DeclaringType) ? ResolveSlot(declaration, hierarchy, resolve_type) : null;
		}

		static MethodDef? ResolveSlot(MethodDef slot, TypeDef[] hierarchy, Func<ITypeDefOrRef?, TypeDef?> resolve_type) {
			if (slot.IsFinal || !slot.IsVirtual) return slot.IsAbstract ? null : slot;
			var root = SlotRoot(slot, resolve_type);
			foreach (var current in hierarchy) {
				var explicit_methods = current.Methods.Where(method => !method.IsStatic && method.Overrides.Any(item => SameMethod(item.MethodDeclaration, slot) || SameMethod(item.MethodDeclaration, root))).Take(2).ToArray();
				if (explicit_methods.Length > 1) return null;
				if (explicit_methods.Length == 1) return explicit_methods[0].IsAbstract ? null : explicit_methods[0];
				var matches = current.Methods.Where(method => !method.IsStatic && method.IsVirtual && Matches(method, slot) && SameMethod(SlotRoot(method, resolve_type), root)).Take(2).ToArray();
				if (matches.Length > 1) return null;
				if (matches.Length == 1) return matches[0].IsAbstract ? null : matches[0];
			}
			return slot.IsAbstract ? null : slot;
		}

		static MethodDef SlotRoot(MethodDef method, Func<ITypeDefOrRef?, TypeDef?> resolve_type) {
			var seen = new HashSet<MethodDef>();
			while (!method.IsNewSlot && method.IsVirtual && seen.Count < 256 && seen.Add(method)) {
				var parent = Hierarchy(resolve_type(method.DeclaringType.BaseType), resolve_type)
					.Select(type => type.Methods.FirstOrDefault(candidate => candidate.IsVirtual && !candidate.IsStatic && Matches(candidate, method))).FirstOrDefault(candidate => candidate is not null);
				if (parent is null) break;
				method = parent;
			}
			return method;
		}

		static IEnumerable<TypeDef> Hierarchy(TypeDef? type, Func<ITypeDefOrRef?, TypeDef?> resolve_type) {
			var seen = new HashSet<TypeDef>();
			while (type is not null && seen.Count < 256 && seen.Add(type)) { yield return type; type = resolve_type(type.BaseType); }
		}
		static bool HasInterface(ITypeDefOrRef reference, TypeDef target, Func<ITypeDefOrRef?, TypeDef?> resolve_type) {
			var pending = new Queue<ITypeDefOrRef>(); var seen = new HashSet<TypeDef>(); pending.Enqueue(reference);
			while (pending.Count != 0 && seen.Count < 256) {
				var current = resolve_type(pending.Dequeue());
				if (current is null || !seen.Add(current)) continue;
				if (ReferenceEquals(current, target)) return true;
				foreach (var item in current.Interfaces) pending.Enqueue(item.Interface);
			}
			return false;
		}
		static bool Matches(IMethod left, IMethod right) => left.Name == right.Name && new SigComparer().Equals(left.MethodSig, right.MethodSig);
		static bool SameMethod(IMethod left, IMethod right) => ReferenceEquals(left, right) || Matches(left, right) && new SigComparer().Equals(left.DeclaringType, right.DeclaringType);
	}
}
