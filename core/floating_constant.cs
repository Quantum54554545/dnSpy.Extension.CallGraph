using System;
using System.Numerics;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	static class floating_constant {
		static (BigInteger number, int exponent) decode(double value) {
			ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
			int exponent = (int)((bits >> 52) & 2047);
			BigInteger number = (bits & 0xFFFFFFFFFFFFFUL) | (exponent == 0 ? 0UL : 1UL << 52);
			if ((bits >> 63) != 0) number = -number;
			return (number: number, exponent == 0 ? -1074 : exponent - 1075);
		}
		static bool equal((BigInteger number, int exponent) a, (BigInteger number, int exponent) b) {
			int shift = Math.Min(a.exponent, b.exponent);
			return a.number << (a.exponent - shift) == b.number << (b.exponent - shift);
		}
		public static double? binary(Code code, double a, double b) {
			double result = code switch { Code.Add => a + b, Code.Sub => a - b, Code.Mul => a * b, Code.Div => a / b, _ => double.NaN };
			if (code is not Code.Add and not Code.Sub and not Code.Mul and not Code.Div) return null;
			if (!double.IsFinite(a) || !double.IsFinite(b) || b == 0 && code == Code.Div) return result;
			if (!double.IsFinite(result)) return null;
			var left = floating_constant.decode(a); var right = floating_constant.decode(b); var actual = floating_constant.decode(result);
			if (code == Code.Div) return floating_constant.equal((actual.number * right.number, actual.exponent + right.exponent), left) ? result : null;
			if (code == Code.Mul) return floating_constant.equal((left.number * right.number, left.exponent + right.exponent), actual) ? result : null;
			int shift = Math.Min(left.exponent, right.exponent);
			BigInteger number = (left.number << (left.exponent - shift)) + (code == Code.Sub ? -right.number : right.number) * (BigInteger.One << (right.exponent - shift));
			return floating_constant.equal((number: number, exponent: shift), actual) ? result : null;
		}
		public static bool? compare(Code code, double a, double b) {
			bool unordered = double.IsNaN(a) || double.IsNaN(b);
			return code switch {
				Code.Ceq or Code.Beq or Code.Beq_S => a == b,
				Code.Bne_Un or Code.Bne_Un_S => a != b,
				Code.Cgt or Code.Bgt or Code.Bgt_S => a > b,
				Code.Clt or Code.Blt or Code.Blt_S => a < b,
				Code.Cgt_Un or Code.Bgt_Un or Code.Bgt_Un_S => unordered || a > b,
				Code.Clt_Un or Code.Blt_Un or Code.Blt_Un_S => unordered || a < b,
				Code.Bge or Code.Bge_S => a >= b, Code.Ble or Code.Ble_S => a <= b,
				Code.Bge_Un or Code.Bge_Un_S => unordered || a >= b,
				Code.Ble_Un or Code.Ble_Un_S => unordered || a <= b, _ => null,
			};
		}
	}
}
