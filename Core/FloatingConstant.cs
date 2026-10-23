using System;
using System.Numerics;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	static class FloatingConstant {
		static (BigInteger Number, int Exponent) Decode(double value) {
			ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
			int exponent = (int)((bits >> 52) & 2047);
			BigInteger number = (bits & 0xFFFFFFFFFFFFFUL) | (exponent == 0 ? 0UL : 1UL << 52);
			if ((bits >> 63) != 0) number = -number;
			return (number, exponent == 0 ? -1074 : exponent - 1075);
		}
		static bool Equal((BigInteger Number, int Exponent) a, (BigInteger Number, int Exponent) b) {
			int shift = Math.Min(a.Exponent, b.Exponent);
			return a.Number << (a.Exponent - shift) == b.Number << (b.Exponent - shift);
		}
		public static double? Binary(Code code, double a, double b) {
			double result = code switch { Code.Add => a + b, Code.Sub => a - b, Code.Mul => a * b, Code.Div => a / b, _ => double.NaN };
			if (code is not Code.Add and not Code.Sub and not Code.Mul and not Code.Div) return null;
			if (!double.IsFinite(a) || !double.IsFinite(b) || b == 0 && code == Code.Div) return result;
			if (!double.IsFinite(result)) return null;
			var left = Decode(a); var right = Decode(b); var actual = Decode(result);
			if (code == Code.Div) return Equal((actual.Number * right.Number, actual.Exponent + right.Exponent), left) ? result : null;
			if (code == Code.Mul) return Equal((left.Number * right.Number, left.Exponent + right.Exponent), actual) ? result : null;
			int shift = Math.Min(left.Exponent, right.Exponent);
			BigInteger number = (left.Number << (left.Exponent - shift)) + (code == Code.Sub ? -right.Number : right.Number) * (BigInteger.One << (right.Exponent - shift));
			return Equal((number, shift), actual) ? result : null;
		}
		public static bool? Compare(Code code, double a, double b) {
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
