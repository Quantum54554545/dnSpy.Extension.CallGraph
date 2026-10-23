using System;
using System.Collections.Generic;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class IntegerExpression {
		readonly struct ExpressionKey : IEquatable<ExpressionKey> {
			public readonly string Operation;
			public readonly long Value;
			public readonly IntegerExpression? Left, Right;
			public ExpressionKey(string operation, long value, IntegerExpression? left, IntegerExpression? right) {
				Operation = operation; Value = value; Left = left; Right = right;
			}
			public bool Equals(ExpressionKey other) => Operation == other.Operation && Value == other.Value &&
				ReferenceEquals(Left, other.Left) && ReferenceEquals(Right, other.Right);
			public override bool Equals(object? obj) => obj is ExpressionKey key && Equals(key);
			public override int GetHashCode() => HashCode.Combine(Operation, Value, Left, Right);
		}

		internal sealed class Factory {
			readonly Dictionary<ExpressionKey, IntegerExpression> expressions = new Dictionary<ExpressionKey, IntegerExpression>();
			public IntegerExpression Constant(long value, bool wide = false) => Make(wide ? "i8" : "i4", wide ? value : unchecked((int)value));
			public IntegerExpression Variable(int identity) => Make("var", identity);
			IntegerExpression Make(string operation, long value = 0, IntegerExpression? left = null, IntegerExpression? right = null) {
				var key = new ExpressionKey(operation, value, left, right);
				if (!expressions.TryGetValue(key, out var result))
					expressions.Add(key, result = new IntegerExpression(operation, value, left, right));
				return result;
			}

			public IntegerExpression? Binary(Code code, IntegerExpression? left, IntegerExpression? right) {
				if (left is null || right is null)
					return null;
				bool native_unknown = left.BitWidth == 0 || right.BitWidth == 0;
				if (native_unknown && code is Code.Shl or Code.Shr or Code.Shr_Un) return right.IsZero ? left : null;
				bool wide = left.BitWidth == 64 || code is not Code.Shl and not Code.Shr and not Code.Shr_Un && right.BitWidth == 64;
				if (left.IsConstant && right.IsConstant) {
					long a = left.Value, b = right.Value;
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
						if (native_unknown && !comparison) return value >= 0 && value <= int.MaxValue ? Make("native", value) : null;
						return Constant(value, wide && !comparison);
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
					if (op == "^" || op == "-") return Constant(0);
					if (op == "&" || op == "|") return left;
				}
				if ((op == "+" || op == "^" || op == "|") && right.IsZero) return left;
				if ((op == "+" || op == "^" || op == "|") && left.IsZero) return right;
				if (op == "-" && right.IsZero) return left;
				if (op == "-" && left.Operation == "+" && ReferenceEquals(left.Right, right)) return left.Left;
				if (op == "+" && left.Operation == "-" && ReferenceEquals(left.Right, right)) return left.Left;
				if (op == "*" && (left.IsZero || right.IsZero)) return Constant(0);
				if (op == "&" && right.Operation == "i4" && right.Value == -1) return left;
				if (op == "^" && left.Operation == "^") {
					if (ReferenceEquals(left.Right, right)) return left.Left;
					if (ReferenceEquals(left.Left, right)) return left.Right;
				}
				if (op == "&" && left.Operation == "&" && ReferenceEquals(left.Right, right)) return left;
				if (op == "|" && !wide && !native_unknown) {
					var rotated = TryRotate(left, right) ?? TryRotate(right, left);
					if (rotated is not null) return rotated;
				}
				if (op == "<<" || op == ">>>" || op == ">>") right = Binary(Code.And, right, Constant(wide ? 63 : 31))!;
				if (left.Depth > 96 || right.Depth > 96) return null;
				return Make(op, 0, left, right);
			}

			IntegerExpression MaskShift(IntegerExpression value) => Binary(Code.And, value, Constant(31))!;
			IntegerExpression? TryRotate(IntegerExpression left, IntegerExpression right) {
				if (left.Operation != "<<" || right.Operation != ">>>" || !ReferenceEquals(left.Left, right.Left))
					return null;
				var a = left.Right!;
				var b = right.Right!;
				if (IsComplement(a, b)) return Rotate("rol", left.Left!, a);
				if (IsComplement(b, a)) return Rotate("ror", left.Left!, b);
				return null;
			}
			static bool IsComplement(IntegerExpression shift, IntegerExpression complement) {
				var value = complement;
				if (value.Operation == "&" && value.Right?.Value == 31) value = value.Left!;
				return value.Operation == "-" && value.Left?.IsConstant == true && value.Left.Value == 32 &&
					ReferenceEquals(value.Right, shift);
			}
			IntegerExpression Rotate(string operation, IntegerExpression value, IntegerExpression amount) {
				if ((value.Operation == "rol" && operation == "ror" || value.Operation == "ror" && operation == "rol") &&
					ReferenceEquals(value.Right, amount)) return value.Left!;
				return Make(operation, 0, value, amount);
			}
			public IntegerExpression? Unary(Code code, IntegerExpression? value) {
				if (value is null) return null;
				if (value.IsConstant && value.BitWidth == 0) return Make("native", code == Code.Not ? ~value.Value : unchecked(-value.Value));
				if (value.IsConstant) return Constant(code == Code.Not ? ~value.Value : unchecked(-value.Value), value.BitWidth == 64);
				string operation = code == Code.Not ? "~" : "neg";
				if (value.Operation == operation) return value.Left;
				return value.Depth < 96 ? Make(operation, 0, value) : null;
			}
			public IntegerExpression? Convert(Code code, IntegerExpression? value, int native_size = 0) {
				if (value is null || value.Depth >= 96) return null;
				if (code is Code.Conv_I or Code.Conv_U) {
					if (native_size == 0) return value.IsConstant && value.Value >= 0 && value.Value <= int.MaxValue ? Make("native", value.Value) : null;
					code = native_size == 4 ? Code.Conv_I4 : code == Code.Conv_I ? Code.Conv_I8 : Code.Conv_U8;
				}
				if (value.IsConstant) {
					long number = value.Value;
					return code switch {
						Code.Conv_I1 => Constant(unchecked((sbyte)number)), Code.Conv_U1 => Constant(unchecked((byte)number)),
						Code.Conv_I2 => Constant(unchecked((short)number)), Code.Conv_U2 => Constant(unchecked((ushort)number)),
						Code.Conv_I4 or Code.Conv_U4 => Constant(unchecked((int)number)),
						Code.Conv_I8 => Constant(number, true),
						Code.Conv_U8 => Constant(value.BitWidth == 32 ? unchecked((uint)number) : number, true), _ => null,
					};
				}
				if (code is Code.Conv_I4 or Code.Conv_U4 && value.BitWidth == 32 || code is Code.Conv_I8 or Code.Conv_U8 && value.BitWidth == 64) return value;
				return Make(code switch { Code.Conv_I1 => "conv.i1", Code.Conv_U1 => "conv.u1", Code.Conv_I2 => "conv.i2", Code.Conv_U2 => "conv.u2", Code.Conv_I4 or Code.Conv_U4 => "conv.i4", Code.Conv_I8 => "conv.i8", Code.Conv_U8 => "conv.u8", _ => "unknown conversion" }, 0, value);
			}
		}

		public readonly string Operation;
		public readonly long Value;
		public readonly IntegerExpression? Left, Right;
		public readonly int Depth;
		public int BitWidth { get; }
		public bool IsConstant => Operation == "i4" || Operation == "i8" || Operation == "native";
		public bool IsZero => IsConstant && Value == 0;
		IntegerExpression(string operation, long value, IntegerExpression? left, IntegerExpression? right) {
			Operation = operation; Value = value; Left = left; Right = right;
			Depth = 1 + Math.Max(left?.Depth ?? 0, right?.Depth ?? 0);
			BitWidth = operation is "i8" or "conv.i8" or "conv.u8" ? 64 : operation.StartsWith("conv.", StringComparison.Ordinal) || operation is "i4" or "var" ? 32 : operation == "native" || left?.BitWidth == 0 || right?.BitWidth == 0 ? 0 : Math.Max(left?.BitWidth ?? 32, right?.BitWidth ?? 32);
		}
	}
}
