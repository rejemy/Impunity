using System;
using System.Collections.Generic;
using System.Text;

namespace Dreamwing.Cons
{
	public static class CommandTokenizer
	{
		public static List<string> Tokenize(string commandLine)
		{
			List<string> tokens = new List<string>();

			int pos = 0;
			while (pos < commandLine.Length)
			{
				pos = ParseToken(commandLine, pos, out string token);
				tokens.Add(token);

				pos = FindNextToken(commandLine, pos);
			}

			return tokens;
		}

		private static bool IsWhitespace(char c)
		{
			return (c == ' ' || c == '\t');
		}

		private static int FindNextToken(string commandLine, int pos)
		{
			while (pos < commandLine.Length && IsWhitespace(commandLine[pos]))
			{
				pos++;
			}

			return pos;
		}

		private static int ParseToken(string commandLine, int pos, out string token)
		{
			if (commandLine[pos] == '"' || commandLine[pos] == '\'')
			{
				return ParseString(commandLine, pos, out token);
			}

			int startPos = pos;
			while (pos < commandLine.Length && !IsWhitespace(commandLine[pos]))
			{
				pos++;
			}

			string tok = commandLine.Substring(startPos, pos - startPos);
			token = tok;

			return pos;
		}

		private static int ParseString(string commandLine, int pos, out string str)
		{
			StringBuilder sb = new StringBuilder();

			char delimiter = commandLine[pos];
			pos++;

			int startPos = pos;
			bool closed = false;
			while (pos < commandLine.Length)
			{
				char next = commandLine[pos];
				if (next == delimiter)
				{
					closed = true;
					pos++;
					break;
				}
				else if (next == '\\')
				{
					pos = ParseEscape(commandLine, pos, out char parsedChar);
					sb.Append(parsedChar);
				}
				else
				{
					sb.Append(next);
					pos++;
				}
			}

			if (!closed)
				throw new Exception("Unclosed string");

			str = sb.ToString();

			return pos;
		}

		private static int ParseEscape(string commandLine, int pos, out char parsedEsc)
		{
			pos++;

			if (pos >= commandLine.Length)
			{
				parsedEsc = commandLine[pos];
				return pos;
			}

			char next = commandLine[pos];
			switch (next)
			{
				case '\\':
					{
						parsedEsc = '\\';
						pos++;
						break;
					}
				case '"':
					{
						parsedEsc = '"';
						pos++;
						break;
					}
				case '\'':
					{
						parsedEsc = '\'';
						pos++;
						break;
					}
				case 'n':
					{
						parsedEsc = '\n';
						pos++;
						break;
					}
				case 't':
					{
						parsedEsc = '\t';
						pos++;
						break;
					}
				default:
					{
						parsedEsc = '\\';
						break;
					}
			}

			return pos;
		}

	}

}