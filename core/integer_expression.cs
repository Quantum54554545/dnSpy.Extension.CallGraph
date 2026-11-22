using System;
using System.Collections.Generic;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class integer_expression {
		readonly struct expression_key : IEquatable<expression_key> {
			public readonly string operation;
			public readonly long value;
			public readonly integer_expression? left, right;
			public expression_key(string operation, long value, integer_expression? left, integer_expression? right) {
				this.operation = operation; this.value = value; this.left = left; this.right = right;
			}
			public bool Equals(expression_key other) => this.operation == other.operation && this.value == other.value &&
				ReferenceEquals(this.left, other.left) && ReferenceEquals(this.right, other.right);
			public override bool Equals(object? obj) => obj is expression_key key && Equals(key);
			public override int GetHashCode() => HashCode.Combine(this.operation, this.value, this.left, this.right);
		}

		internal sealed class factory {
			readonly Dictionary<expression_key, integer_expression> expressions = new Dictionary<expression_key, integer_expression>();
			public integer_expression constant(long value, bool wide = false) => this.make(wide ? "i8" : "i4", wide ? value : unchecked((int)value));
			public integer_expression variable(int identity) => this.make("var", identity);
			integer_expression make(string operation, long value = 0, integer_expression? left = null, integer_expression? right = null) {
				var key = new expression_key(operation, value, left, right);
				if (!this.expressions.TryGetValue(key, out var result))
					this.expressions.Add(key, result = new integer_expression(operation, value, left, right));
				return result;
			}

			public integer_expression? binary(Code code, integer_expression? left, integer_expression? right) {
				if (left is null || right is null)
					return null;
				bool native_unknown = left.bit_width == 0 || right.bit_width == 0;
				if (native_unknown && code is Code.Shl or Code.Shr or Code.Shr_Un) return right.is_zero ? left : null;
				bool wide = left.bit_width == 64 || code is not Code.Shl and not Code.Shr and not Code.Shr_Un && right.bit_width == 64;
				if (left.is_constant && right.is_constant) {
					long a = left.value, b = right.value;
					if (native_unknown && (a < 0 || b < 0) && code is Code.Div_Un or Code.Rem_Un or Code.Cgt_Un or Code.Clt_Un) return null;
					try {
						long value = code switch {
							Code.Add => unchecked(a + b), Code.Sub => unchecked(a - b), Code.Mul => unchecked(a * b),
							Code.Xor => a ^ b, Code.And => a & b, Code.Or => a | b,
							Code.Shl => wide ? a << (int)b : (int)a << (int)b,
							Code.Shr => wide ? a >> (int)b : (int)a >> (int)b,
							Code.Shr_Un => wide ? (long)((ulong)a >> (int)b) : (long)((uint)a >> (int)b),
							Code.Div => wide ? a / b : (int)a / (int)b,
							Code.Div_Un => wide ? (long)((ulong)a / (ulong)b) : (long)((uint)a / (uint)b),
							Code.Rem => wide ? a % b : (int)a % (int)b,
							Code.Rem_Un => wide ? (long)((ulong)a % (ulong)b) : (long)((uint)a % (uint)b),
							Code.Ceq => a == b ? 1 : 0, Code.Cgt => a > b ? 1 : 0, Code.Clt => a < b ? 1 : 0,
							Code.Cgt_Un => (wide ? (ulong)a > (ulong)b : (uint)a > (uint)b) ? 1 : 0,
							Code.Clt_Un => (wide ? (ulong)a < (ulong)b : (uint)a < (uint)b) ? 1 : 0,
							_ => throw new InvalidOperationException(),
						};
						bool comparison = code is Code.Ceq or Code.Cgt or Code.Clt or Code.Cgt_Un or Code.Clt_Un;
						if (native_unknown && !comparison) return value >= 0 && value <= int.MaxValue ? this.make("native", value) : null;
						return this.constant(value, wide && !comparison);
					}
					catch (Exception) { return null; }
				}
				string op = code switch {
					Code.Add => "+", Code.Sub => "-", Code.Mul => "*", Code.Xor => "^", Code.And => "&", Code.Or => "|",
					Code.Shl => "<<", Code.Shr_Un => ">>>", Code.Shr => ">>", _ => "",
				};
				if (op.Length == 0)
					return null;
				if (ReferenceEquals(left, right)) {
					if (op == "^" || op == "-") return this.constant(0);
					if (op == "&" || op == "|") return left;
				}
				if ((op == "+" || op == "^" || op == "|") && right.is_zero) return left;
				if ((op == "+" || op == "^" || op == "|") && left.is_zero) return right;
				if (op == "-" && right.is_zero) return left;
				if (op == "-" && left.operation == "+" && ReferenceEquals(left.right, right)) return left.left;
				if (op == "+" && left.operation == "-" && ReferenceEquals(left.right, right)) return left.left;
				if (op == "*" && (left.is_zero || right.is_zero)) return this.constant(0);
				if (op == "&" && right.operation == "i4" && right.value == -1) return left;
				if (op == "^" && left.operation == "^") {
					if (ReferenceEquals(left.right, right)) return left.left;
					if (ReferenceEquals(left.left, right)) return left.right;
				}
				if (op == "&" && left.operation == "&" && ReferenceEquals(left.right, right)) return left;
				if (op == "|" && !wide && !native_unknown) {
					var rotated = this.try_rotate(left, right) ?? this.try_rotate(right, left);
					if (rotated is not null) return rotated;
				}
				if (op == "<<" || op == ">>>" || op == ">>") right = this.binary(Code.And, right, this.constant(wide ? 63 : 31))!;
				if (left.depth > 96 || right.depth > 96) return null;
				return this.make(op, 0, left, right);
			}

			integer_expression mask_shift(integer_expression value) => this.binary(Code.And, value, this.constant(31))!;
			integer_expression? try_rotate(integer_expression left, integer_expression right) {
				if (left.operation != "<<" || right.operation != ">>>" || !ReferenceEquals(left.left, right.left))
					return null;
				var a = left.right!;
				var b = right.right!;
				if (factory.is_complement(a, b)) return this.rotate("rol", left.left!, a);
				if (factory.is_complement(b, a)) return this.rotate("ror", left.left!, b);
				return null;
			}
			static bool is_complement(integer_expression shift, integer_expression complement) {
				var value = complement;
				if (value.operation == "&" && value.right?.value == 31) value = value.left!;
				return value.operation == "-" && value.left?.is_constant == true && value.left.value == 32 &&
					ReferenceEquals(value.right, shift);
			}
			integer_expression rotate(string operation, integer_expression value, integer_expression amount) {
				if ((value.operation == "rol" && operation == "ror" || value.operation == "ror" && operation == "rol") &&
					ReferenceEquals(value.right, amount)) return value.left!;
				return this.make(operation, 0, value, amount);
			}
			public integer_expression? unary(Code code, integer_expression? value) {
				if (value is null) return null;
				if (value.is_constant && value.bit_width == 0) return this.make("native", code == Code.Not ? ~value.value : unchecked(-value.value));
				if (value.is_constant) return this.constant(code == Code.Not ? ~value.value : unchecked(-value.value), value.bit_width == 64);
				string operation = code == Code.Not ? "~" : "neg";
				if (value.operation == operation) return value.left;
				return value.depth < 96 ? this.make(operation, 0, value) : null;
			}
			public integer_expression? convert(Code code, integer_expression? value, int native_size = 0) {
				if (value is null || value.depth >= 96) return null;
				if (code is Code.Conv_I or Code.Conv_U) {
					if (native_size == 0) return value.is_constant && value.value >= 0 && value.value <= int.MaxValue ? this.make("native", value.value) : null;
					code = native_size == 4 ? Code.Conv_I4 : code == Code.Conv_I ? Code.Conv_I8 : Code.Conv_U8;
				}
				if (value.is_constant) {
					long number = value.value;
					return code switch {
						Code.Conv_I1 => this.constant(unchecked((sbyte)number)), Code.Conv_U1 => this.constant(unchecked((byte)number)),
						Code.Conv_I2 => this.constant(unchecked((short)number)), Code.Conv_U2 => this.constant(unchecked((ushort)number)),
						Code.Conv_I4 or Code.Conv_U4 => this.constant(unchecked((int)number)),
						Code.Conv_I8 => this.constant(number, true),
						Code.Conv_U8 => this.constant(value.bit_width == 32 ? unchecked((uint)number) : number, true), _ => null,
					};
				}
				if (code is Code.Conv_I4 or Code.Conv_U4 && value.bit_width == 32 || code is Code.Conv_I8 or Code.Conv_U8 && value.bit_width == 64) return value;
				return this.make(code switch { Code.Conv_I1 => "conv.i1", Code.Conv_U1 => "conv.u1", Code.Conv_I2 => "conv.i2", Code.Conv_U2 => "conv.u2", Code.Conv_I4 or Code.Conv_U4 => "conv.i4", Code.Conv_I8 => "conv.i8", Code.Conv_U8 => "conv.u8", _ => "unknown conversion" }, 0, value);
			}
		}

		public readonly string operation;
		public readonly long value;
		public readonly integer_expression? left, right;
		public readonly int depth;
		public int bit_width { get; }
		public bool is_constant => this.operation == "i4" || this.operation == "i8" || this.operation == "native";
		public bool is_zero => this.is_constant && this.value == 0;
		integer_expression(string operation, long value, integer_expression? left, integer_expression? right) {
			this.operation = operation; this.value = value; this.left = left; this.right = right;
			this.depth = 1 + Math.Max(left?.depth ?? 0, right?.depth ?? 0);
			this.bit_width = operation is "i8" or "conv.i8" or "conv.u8" ? 64 : operation.StartsWith("conv.", StringComparison.Ordinal) || operation is "i4" or "var" ? 32 : operation == "native" || left?.bit_width == 0 || right?.bit_width == 0 ? 0 : Math.Max(left?.bit_width ?? 32, right?.bit_width ?? 32);
		}
	}
}
