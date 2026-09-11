using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation.Scripting
{
	// =========================================================
	// 触发器脚本表达式引擎（词法 + 语法 + 求值）
	//
	// 为什么自己写而不是用 Godot 的 Expression：
	//   1. 必须**确定性**——Godot Expression 走 float/double，且引擎版本间行为不保证一致；
	//   2. 逻辑层不允许依赖 Godot API（check_sim_thread.py 会拦）；
	//   3. 需要 Fix64 定点语义与可复现的错误行为。
	//
	// 类型只有三种：Bool / Number(Fix64) / String。
	// 数值字面量用 decimal 解析后转 Fix64，避免 double 引入的舍入差异。
	// =========================================================

	public enum SimExprType { Bool, Number, String }

	/// <summary>表达式值：标签联合。刻意做成 struct 避免求值期分配。</summary>
	public readonly struct SimExprValue
	{
		public readonly SimExprType Type;
		private readonly FP _number;
		private readonly bool _bool;
		private readonly string _string;

		private SimExprValue(SimExprType type, FP number, bool boolean, string text)
		{
			Type = type;
			_number = number;
			_bool = boolean;
			_string = text;
		}

		public static SimExprValue FromNumber(FP v) => new SimExprValue(SimExprType.Number, v, false, null);
		public static SimExprValue FromBool(bool v) => new SimExprValue(SimExprType.Bool, FP.Zero, v, null);
		public static SimExprValue FromString(string v) => new SimExprValue(SimExprType.String, FP.Zero, false, v ?? "");

		public static readonly SimExprValue Zero = FromNumber(FP.Zero);
		public static readonly SimExprValue True = FromBool(true);
		public static readonly SimExprValue False = FromBool(false);

		public FP AsNumber() => Type switch
		{
			SimExprType.Number => _number,
			SimExprType.Bool => _bool ? FP.One : FP.Zero,
			_ => ParseStringAsNumber(_string),
		};

		public bool AsBool() => Type switch
		{
			SimExprType.Bool => _bool,
			SimExprType.Number => _number != FP.Zero,
			_ => !string.IsNullOrEmpty(_string),
		};

		public string AsString() => Type switch
		{
			SimExprType.String => _string,
			SimExprType.Bool => _bool ? "true" : "false",
			_ => FormatNumber(_number),
		};

		/// <summary>定点数转字符串：固定 3 位小数，去掉多余的 0（确定性，不受区域设置影响）。</summary>
		public static string FormatNumber(FP v)
		{
			long raw = (long)(v * (FP)1000m);
			bool negative = raw < 0;
			if (negative) raw = -raw;
			long whole = raw / 1000;
			long frac = raw % 1000;

			var sb = new StringBuilder();
			if (negative) sb.Append('-');
			sb.Append(whole.ToString(CultureInfo.InvariantCulture));
			if (frac != 0)
			{
				sb.Append('.');
				sb.Append(frac.ToString("D3", CultureInfo.InvariantCulture).TrimEnd('0'));
			}
			return sb.ToString();
		}

		private static FP ParseStringAsNumber(string s)
		{
			if (string.IsNullOrEmpty(s)) return FP.Zero;
			return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d)
				? (FP)d
				: FP.Zero;
		}
	}

	// ---------------------------------------------------------
	// 词法
	// ---------------------------------------------------------

	internal enum SimTokKind
	{
		End, Number, String, Ident,
		Plus, Minus, Star, Slash, Percent,
		LParen, RParen, Comma,
		Eq, Ne, Lt, Le, Gt, Ge,
		And, Or, Not,
	}

	internal readonly struct SimTok
	{
		public readonly SimTokKind Kind;
		public readonly FP Number;
		public readonly string Text;
		public readonly int Position;

		public SimTok(SimTokKind kind, FP number, string text, int position)
		{
			Kind = kind; Number = number; Text = text; Position = position;
		}
	}

	/// <summary>表达式语法错误。作者写错时抛出，由调用方转成编辑器/日志里的可读信息。</summary>
	public sealed class SimExprException : Exception
	{
		public int Position { get; }
		public SimExprException(string message, int position) : base($"{message}（位置 {position}）")
		{
			Position = position;
		}
	}

	internal static class SimLexer
	{
		public static List<SimTok> Tokenize(string src)
		{
			var tokens = new List<SimTok>();
			if (string.IsNullOrEmpty(src))
			{
				tokens.Add(new SimTok(SimTokKind.End, FP.Zero, null, 0));
				return tokens;
			}

			int i = 0;
			while (i < src.Length)
			{
				char c = src[i];
				if (char.IsWhiteSpace(c)) { i++; continue; }

				int start = i;

				// 数字字面量：整数或小数。用 decimal 解析保证跨端一致。
				if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
				{
					int j = i;
					while (j < src.Length && (char.IsDigit(src[j]) || src[j] == '.')) j++;
					string numText = src.Substring(i, j - i);
					if (!decimal.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
						throw new SimExprException($"无法解析数字 '{numText}'", start);
					tokens.Add(new SimTok(SimTokKind.Number, (FP)d, numText, start));
					i = j;
					continue;
				}

				// 字符串字面量：单引号或双引号
				if (c == '"' || c == '\'')
				{
					char quote = c;
					i++;
					var sb = new StringBuilder();
					bool closed = false;
					while (i < src.Length)
					{
						char s = src[i];
						if (s == '\\' && i + 1 < src.Length)
						{
							char esc = src[i + 1];
							sb.Append(esc switch
							{
								'n' => '\n',
								't' => '\t',
								'\\' => '\\',
								'"' => '"',
								'\'' => '\'',
								_ => esc,
							});
							i += 2;
							continue;
						}
						if (s == quote) { closed = true; i++; break; }
						sb.Append(s);
						i++;
					}
					if (!closed) throw new SimExprException("字符串字面量没有收尾引号", start);
					tokens.Add(new SimTok(SimTokKind.String, FP.Zero, sb.ToString(), start));
					continue;
				}

				// 标识符 / 关键字
				if (char.IsLetter(c) || c == '_')
				{
					int j = i;
					while (j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] == '_')) j++;
					string ident = src.Substring(i, j - i);
					SimTokKind kind = ident switch
					{
						"and" => SimTokKind.And,
						"or" => SimTokKind.Or,
						"not" => SimTokKind.Not,
						"true" => SimTokKind.Number,
						"false" => SimTokKind.Number,
						_ => SimTokKind.Ident,
					};
					if (ident == "true") tokens.Add(new SimTok(SimTokKind.Number, FP.One, ident, start));
					else if (ident == "false") tokens.Add(new SimTok(SimTokKind.Number, FP.Zero, ident, start));
					else tokens.Add(new SimTok(kind, FP.Zero, ident, start));
					i = j;
					continue;
				}

				// 运算符
				switch (c)
				{
					case '+': tokens.Add(new SimTok(SimTokKind.Plus, FP.Zero, "+", i)); i++; continue;
					case '-': tokens.Add(new SimTok(SimTokKind.Minus, FP.Zero, "-", i)); i++; continue;
					case '*': tokens.Add(new SimTok(SimTokKind.Star, FP.Zero, "*", i)); i++; continue;
					case '/': tokens.Add(new SimTok(SimTokKind.Slash, FP.Zero, "/", i)); i++; continue;
					case '%': tokens.Add(new SimTok(SimTokKind.Percent, FP.Zero, "%", i)); i++; continue;
					case '(': tokens.Add(new SimTok(SimTokKind.LParen, FP.Zero, "(", i)); i++; continue;
					case ')': tokens.Add(new SimTok(SimTokKind.RParen, FP.Zero, ")", i)); i++; continue;
					case ',': tokens.Add(new SimTok(SimTokKind.Comma, FP.Zero, ",", i)); i++; continue;
					case '=':
						if (i + 1 < src.Length && src[i + 1] == '=')
						{ tokens.Add(new SimTok(SimTokKind.Eq, FP.Zero, "==", i)); i += 2; }
						else
						{ tokens.Add(new SimTok(SimTokKind.Eq, FP.Zero, "=", i)); i++; }
						continue;
					case '!':
						if (i + 1 < src.Length && src[i + 1] == '=')
						{ tokens.Add(new SimTok(SimTokKind.Ne, FP.Zero, "!=", i)); i += 2; continue; }
						tokens.Add(new SimTok(SimTokKind.Not, FP.Zero, "!", i)); i++; continue;
					case '<':
						if (i + 1 < src.Length && src[i + 1] == '=')
						{ tokens.Add(new SimTok(SimTokKind.Le, FP.Zero, "<=", i)); i += 2; }
						else
						{ tokens.Add(new SimTok(SimTokKind.Lt, FP.Zero, "<", i)); i++; }
						continue;
					case '>':
						if (i + 1 < src.Length && src[i + 1] == '=')
						{ tokens.Add(new SimTok(SimTokKind.Ge, FP.Zero, ">=", i)); i += 2; }
						else
						{ tokens.Add(new SimTok(SimTokKind.Gt, FP.Zero, ">", i)); i++; }
						continue;
					case '&':
						if (i + 1 < src.Length && src[i + 1] == '&') { tokens.Add(new SimTok(SimTokKind.And, FP.Zero, "&&", i)); i += 2; continue; }
						throw new SimExprException("'&' 需写成 '&&'", i);
					case '|':
						if (i + 1 < src.Length && src[i + 1] == '|') { tokens.Add(new SimTok(SimTokKind.Or, FP.Zero, "||", i)); i += 2; continue; }
						throw new SimExprException("'|' 需写成 '||'", i);
					default:
						throw new SimExprException($"无法识别的字符 '{c}'", i);
				}
			}

			tokens.Add(new SimTok(SimTokKind.End, FP.Zero, null, src.Length));
			return tokens;
		}
	}

	// ---------------------------------------------------------
	// 语法树
	// ---------------------------------------------------------

	internal abstract class SimNode
	{
		public abstract SimExprValue Eval(ISimExprHost host);
	}

	internal sealed class NumberNode : SimNode
	{
		private readonly FP _value;
		public NumberNode(FP value) { _value = value; }
		public override SimExprValue Eval(ISimExprHost host) => SimExprValue.FromNumber(_value);
	}

	internal sealed class StringNode : SimNode
	{
		private readonly string _value;
		public StringNode(string value) { _value = value; }
		public override SimExprValue Eval(ISimExprHost host) => SimExprValue.FromString(_value);
	}

	/// <summary>变量引用：先查地图脚本变量，再查内置只读量（tick/队伍/玩家）。</summary>
	internal sealed class VariableNode : SimNode
	{
		private readonly string _name;
		public string Name => _name;
		public VariableNode(string name) { _name = name; }
		public override SimExprValue Eval(ISimExprHost host) => host.ReadVariable(_name);
	}

	internal sealed class UnaryNode : SimNode
	{
		private readonly SimTokKind _op;
		private readonly SimNode _operand;
		public UnaryNode(SimTokKind op, SimNode operand) { _op = op; _operand = operand; }

		public override SimExprValue Eval(ISimExprHost host)
		{
			var v = _operand.Eval(host);
			return _op switch
			{
				SimTokKind.Minus => SimExprValue.FromNumber(-v.AsNumber()),
				SimTokKind.Not => SimExprValue.FromBool(!v.AsBool()),
				_ => v,
			};
		}
	}

	internal sealed class BinaryNode : SimNode
	{
		private readonly SimTokKind _op;
		private readonly SimNode _left;
		private readonly SimNode _right;
		public BinaryNode(SimTokKind op, SimNode left, SimNode right) { _op = op; _left = left; _right = right; }

		public override SimExprValue Eval(ISimExprHost host)
		{
			// 短路：&& / || 右侧按需求值（避免无谓的世界查询）
			if (_op == SimTokKind.And)
				return SimExprValue.FromBool(_left.Eval(host).AsBool() && _right.Eval(host).AsBool());
			if (_op == SimTokKind.Or)
				return SimExprValue.FromBool(_left.Eval(host).AsBool() || _right.Eval(host).AsBool());

			var a = _left.Eval(host);
			var b = _right.Eval(host);

			switch (_op)
			{
				case SimTokKind.Plus:
					// 字符串拼接：任一侧是字符串且另一侧也是字符串时拼接
					if (a.Type == SimExprType.String || b.Type == SimExprType.String)
						return SimExprValue.FromString(a.AsString() + b.AsString());
					return SimExprValue.FromNumber(a.AsNumber() + b.AsNumber());
				case SimTokKind.Minus:
					return SimExprValue.FromNumber(a.AsNumber() - b.AsNumber());
				case SimTokKind.Star:
					return SimExprValue.FromNumber(a.AsNumber() * b.AsNumber());
				case SimTokKind.Slash:
				{
					FP divisor = b.AsNumber();
					if (divisor == FP.Zero)
						throw new SimExprException("除数为 0", 0);
					return SimExprValue.FromNumber(a.AsNumber() / divisor);
				}
				case SimTokKind.Percent:
				{
					FP divisor = b.AsNumber();
					if (divisor == FP.Zero)
						throw new SimExprException("取模的除数为 0", 0);
					return SimExprValue.FromNumber(a.AsNumber() % divisor);
				}
				case SimTokKind.Eq:
					if (a.Type == SimExprType.String || b.Type == SimExprType.String)
						return SimExprValue.FromBool(string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal));
					return SimExprValue.FromBool(a.AsNumber() == b.AsNumber());
				case SimTokKind.Ne:
					if (a.Type == SimExprType.String || b.Type == SimExprType.String)
						return SimExprValue.FromBool(!string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal));
					return SimExprValue.FromBool(a.AsNumber() != b.AsNumber());
				case SimTokKind.Lt: return SimExprValue.FromBool(a.AsNumber() < b.AsNumber());
				case SimTokKind.Le: return SimExprValue.FromBool(a.AsNumber() <= b.AsNumber());
				case SimTokKind.Gt: return SimExprValue.FromBool(a.AsNumber() > b.AsNumber());
				case SimTokKind.Ge: return SimExprValue.FromBool(a.AsNumber() >= b.AsNumber());
				default:
					throw new SimExprException($"未实现的运算符 {_op}", 0);
			}
		}
	}

	internal sealed class CallNode : SimNode
	{
		private readonly string _name;
		private readonly List<SimNode> _args;
		public string Name => _name;
		public int ArgCount => _args.Count;

		public CallNode(string name, List<SimNode> args) { _name = name; _args = args; }

		public override SimExprValue Eval(ISimExprHost host)
		{
			var values = new SimExprValue[_args.Count];
			for (int i = 0; i < _args.Count; i++)
				values[i] = _args[i].Eval(host);
			return host.CallBuiltin(_name, values);
		}
	}

	// ---------------------------------------------------------
	// 语法分析（递归下降）
	//
	// 优先级（低 → 高）：
	//   or → and → 比较 → 加减 → 乘除模 → 一元 → 基本
	// =========================================================

	internal sealed class SimParser
	{
		private readonly List<SimTok> _tokens;
		private int _index;

		private SimParser(List<SimTok> tokens) { _tokens = tokens; }

		public static SimNode Parse(string source)
		{
			var parser = new SimParser(SimLexer.Tokenize(source));
			SimNode node = parser.ParseOr();
			parser.Expect(SimTokKind.End, "表达式结尾");
			return node;
		}

		private SimTok Current => _tokens[_index];

		private SimTok Advance() => _tokens[_index++];

		private void Expect(SimTokKind kind, string what)
		{
			if (Current.Kind != kind)
				throw new SimExprException($"应为 {what}，实际是 '{Describe(Current)}'", Current.Position);
			_index++;
		}

		private static string Describe(SimTok t) => t.Kind switch
		{
			SimTokKind.End => "表达式结尾",
			SimTokKind.Number => SimExprValue.FormatNumber(t.Number),
			SimTokKind.String => $"\"{t.Text}\"",
			_ => t.Text ?? t.Kind.ToString(),
		};

		private SimNode ParseOr()
		{
			SimNode left = ParseAnd();
			while (Current.Kind == SimTokKind.Or)
			{
				Advance();
				left = new BinaryNode(SimTokKind.Or, left, ParseAnd());
			}
			return left;
		}

		private SimNode ParseAnd()
		{
			SimNode left = ParseComparison();
			while (Current.Kind == SimTokKind.And)
			{
				Advance();
				left = new BinaryNode(SimTokKind.And, left, ParseComparison());
			}
			return left;
		}

		private SimNode ParseComparison()
		{
			SimNode left = ParseAdditive();
			while (Current.Kind is SimTokKind.Eq or SimTokKind.Ne or SimTokKind.Lt
				or SimTokKind.Le or SimTokKind.Gt or SimTokKind.Ge)
			{
				SimTokKind op = Advance().Kind;
				left = new BinaryNode(op, left, ParseAdditive());
			}
			return left;
		}

		private SimNode ParseAdditive()
		{
			SimNode left = ParseMultiplicative();
			while (Current.Kind is SimTokKind.Plus or SimTokKind.Minus)
			{
				SimTokKind op = Advance().Kind;
				left = new BinaryNode(op, left, ParseMultiplicative());
			}
			return left;
		}

		private SimNode ParseMultiplicative()
		{
			SimNode left = ParseUnary();
			while (Current.Kind is SimTokKind.Star or SimTokKind.Slash or SimTokKind.Percent)
			{
				SimTokKind op = Advance().Kind;
				left = new BinaryNode(op, left, ParseUnary());
			}
			return left;
		}

		private SimNode ParseUnary()
		{
			if (Current.Kind is SimTokKind.Minus or SimTokKind.Not)
			{
				SimTokKind op = Advance().Kind;
				return new UnaryNode(op, ParseUnary());
			}
			return ParsePrimary();
		}

		private SimNode ParsePrimary()
		{
			SimTok tok = Current;
			switch (tok.Kind)
			{
				case SimTokKind.Number:
					Advance();
					return new NumberNode(tok.Number);

				case SimTokKind.String:
					Advance();
					return new StringNode(tok.Text);

				case SimTokKind.LParen:
				{
					Advance();
					SimNode inner = ParseOr();
					Expect(SimTokKind.RParen, "')'");
					return inner;
				}

				case SimTokKind.Ident:
				{
					Advance();
					if (Current.Kind == SimTokKind.LParen)
					{
						Advance();
						var args = new List<SimNode>();
						if (Current.Kind != SimTokKind.RParen)
						{
							args.Add(ParseOr());
							while (Current.Kind == SimTokKind.Comma)
							{
								Advance();
								args.Add(ParseOr());
							}
						}
						Expect(SimTokKind.RParen, "')'");
						return new CallNode(tok.Text, args);
					}
					return new VariableNode(tok.Text);
				}

				default:
					throw new SimExprException($"表达式不完整，遇到 '{Describe(tok)}'", tok.Position);
			}
		}
	}

	// ---------------------------------------------------------
	// 宿主接口 + 编译缓存
	// ---------------------------------------------------------

	/// <summary>
	/// 表达式求值宿主：提供变量读取与内置函数。
	/// 模拟层实现它（可访问 SimWorld / PlayerData），从而让表达式引擎保持纯逻辑。
	///
	/// 约定：**队伍号在表达式里就是普通数字**。`player(1)` 只是把"队伍 1"写得更可读，
	/// 求值结果就是 1；因此 `player(1) == 1` 为真，作者不必区分两种写法。
	/// </summary>
	public interface ISimExprHost
	{
		SimExprValue ReadVariable(string name);
		SimExprValue CallBuiltin(string name, SimExprValue[] args);
	}

	/// <summary>
	/// 触发器表达式门面：编译 + 缓存 + 求值。
	/// 编译结果按源字符串缓存——地图里同一个条件每 tick 求值时不会重复解析。
	/// </summary>
	public static class SimExpr
	{
		private static readonly Dictionary<string, SimNode> _cache = new(StringComparer.Ordinal);
		private static readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

		/// <summary>清空编译缓存（换地图/热重载时调用）。</summary>
		public static void ClearCache()
		{
			_cache.Clear();
			_errors.Clear();
		}

		/// <summary>
		/// 预编译并校验表达式。返回 null 表示成功，否则返回错误信息。
		/// 编辑器保存地图前调用它，可以一次性报出所有语法错误。
		/// </summary>
		public static string Validate(string source)
		{
			if (string.IsNullOrWhiteSpace(source))
				return null; // 空表达式视为"恒真"，由上层处理

			if (_errors.TryGetValue(source, out string cachedError))
				return cachedError;

			try
			{
				_cache[source] = SimParser.Parse(source);
				return null;
			}
			catch (SimExprException ex)
			{
				_errors[source] = ex.Message;
				return ex.Message;
			}
		}

		/// <summary>求值。表达式为空时返回 fallback（默认恒真）。</summary>
		public static SimExprValue Evaluate(string source, ISimExprHost host, bool emptyResult = true)
		{
			if (string.IsNullOrWhiteSpace(source))
				return SimExprValue.FromBool(emptyResult);

			if (!_cache.TryGetValue(source, out SimNode node))
			{
				node = SimParser.Parse(source);
				_cache[source] = node;
			}
			return node.Eval(host);
		}

		/// <summary>求值成布尔（条件用）。出错时返回 fallback 而非抛出，保证一帧出错不会打断模拟。</summary>
		public static bool EvaluateBool(string source, ISimExprHost host, bool fallback = false, Action<string> onError = null)
		{
			try
			{
				return Evaluate(source, host).AsBool();
			}
			catch (SimExprException ex)
			{
				onError?.Invoke($"条件求值失败: {ex.Message}  表达式: {source}");
				return fallback;
			}
		}

		/// <summary>求值成定点数（数量/坐标用）。</summary>
		public static FP EvaluateNumber(string source, ISimExprHost host, FP fallback = default, Action<string> onError = null)
		{
			try
			{
				return Evaluate(source, host).AsNumber();
			}
			catch (SimExprException ex)
			{
				onError?.Invoke($"数值求值失败: {ex.Message}  表达式: {source}");
				return fallback;
			}
		}

		/// <summary>求值成字符串。</summary>
		public static string EvaluateString(string source, ISimExprHost host, string fallback = "", Action<string> onError = null)
		{
			try
			{
				return Evaluate(source, host).AsString();
			}
			catch (SimExprException ex)
			{
				onError?.Invoke($"文本求值失败: {ex.Message}  表达式: {source}");
				return fallback;
			}
		}
	}
}
