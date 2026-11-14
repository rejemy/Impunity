using System;
using System.Collections.Generic;

using NUnit.Framework;

using Dreamwing.Cons;


namespace DevTools.Tests
{
	// ColumnLayout pads with tabs, so these render its output the way the console's
	// text generator does — fixed tab stops, each tab advancing to the next stop —
	// with a fake fixed-advance font, and check where each item actually lands.
	public class ColumnLayoutTests
	{
		const float CharWidth = 5.0f;
		const float TabWidth = 12.0f;
		const float MinGap = 6.0f;

		static float Measure(string text)
		{
			return text.Length * CharWidth;
		}

		static string Format(IList<string> items, float lineWidth)
		{
			return ColumnLayout.Format(items, Measure, TabWidth, lineWidth, MinGap);
		}

		struct Placed
		{
			public string Item;
			public int Row;
			public float X;
			public float End;
		}

		static List<Placed> Render(string text)
		{
			List<Placed> placed = new List<Placed>();
			string[] rows = text.Split('\n');
			for (int row = 0; row < rows.Length; row++)
			{
				float x = 0.0f;
				int i = 0;
				string line = rows[row];
				while (i < line.Length)
				{
					if (line[i] == '\t')
					{
						x = ((int)Math.Floor(x / TabWidth + 0.001f) + 1) * TabWidth;
						i++;
						continue;
					}

					int start = i;
					while (i < line.Length && line[i] != '\t')
						i++;
					string item = line.Substring(start, i - start);
					placed.Add(new Placed { Item = item, Row = row, X = x, End = x + Measure(item) });
					x += Measure(item);
				}
			}
			return placed;
		}

		static string[] Names(int count)
		{
			string[] names = new string[count];
			for (int i = 0; i < count; i++)
				names[i] = "cmd" + new string('x', i % 7) + i;
			return names;
		}

		[Test]
		public void Format_Empty_ReturnsEmptyString()
		{
			Assert.AreEqual("", Format(new string[0], 600.0f));
		}

		[Test]
		public void Format_ManyItems_ColumnsStartAtTheSameX()
		{
			List<Placed> placed = Render(Format(Names(43), 600.0f));

			Dictionary<int, float> columnX = new Dictionary<int, float>();
			Dictionary<int, int> columnOfRowItem = new Dictionary<int, int>();
			foreach (Placed p in placed)
			{
				columnOfRowItem.TryGetValue(p.Row, out int column);
				columnOfRowItem[p.Row] = column + 1;

				if (columnX.TryGetValue(column, out float x))
					Assert.AreEqual(x, p.X, 0.0001f, "column " + column + " misaligned at '" + p.Item + "'");
				else
					columnX[column] = p.X;
			}

			Assert.Greater(columnX.Count, 1, "expected more than one column");
		}

		[Test]
		public void Format_ManyItems_KeepsEveryItemInColumnMajorOrder()
		{
			string[] names = Names(43);
			List<Placed> placed = Render(Format(names, 600.0f));

			Assert.AreEqual(names.Length, placed.Count);

			// Reading down each column (ordered by x, then row) gives back the input order
			placed.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Row.CompareTo(b.Row));
			for (int i = 0; i < names.Length; i++)
				Assert.AreEqual(names[i], placed[i].Item);
		}

		[Test]
		public void Format_ManyItems_FitsTheLineWithAGapBetweenColumns()
		{
			const float lineWidth = 600.0f;
			List<Placed> placed = Render(Format(Names(43), lineWidth));

			foreach (Placed p in placed)
			{
				Assert.LessOrEqual(p.End, lineWidth, "'" + p.Item + "' runs past the line");

				Placed? next = null;
				foreach (Placed q in placed)
				{
					if (q.Row == p.Row && q.X > p.X && (next == null || q.X < next.Value.X))
						next = q;
				}
				if (next != null)
					Assert.GreaterOrEqual(next.Value.X - p.End, MinGap, "no gap after '" + p.Item + "'");
			}
		}

		[Test]
		public void Format_ItemEndingExactlyOnATabStop_StillAligns()
		{
			// Columns are 72 wide (60 + gap, rounded up to a stop) starting at 12, so
			// the first item ends exactly on the stop at 72; a tab from a stop goes on
			// to the next one (84), which is where the second column starts
			string[] names = { "abcdefghijkl", "b", "c", "d" };
			List<Placed> placed = Render(Format(names, 600.0f));

			Assert.AreEqual(4, placed.Count);
			for (int i = 0; i < 4; i++)
			{
				Assert.AreEqual(0, placed[i].Row);
				Assert.AreEqual(TabWidth + i * 72.0f, placed[i].X, 0.0001f, "'" + placed[i].Item + "'");
			}
		}

		[Test]
		public void Format_NarrowLine_FallsBackToOneColumn()
		{
			string[] names = { "alpha", "beta", "gamma" };
			string text = Format(names, 20.0f);

			Assert.AreEqual("\talpha\n\tbeta\n\tgamma", text);
		}

		[Test]
		public void Format_FewItems_BalancesRowsAcrossColumns()
		{
			// One-char items make 12-wide columns: room for 4, but with 5 items that's
			// 2 rows either way, so use 3 columns (3 + 2) rather than 4 (4 + 1)
			string[] names = { "a", "b", "c", "d", "e" };
			List<Placed> placed = Render(Format(names, TabWidth + 4 * TabWidth));

			int rows = 0;
			foreach (Placed p in placed)
				rows = Math.Max(rows, p.Row + 1);
			Assert.AreEqual(2, rows);
			Assert.AreEqual(3, placed.FindAll(p => p.Row == 0).Count);
			Assert.AreEqual(2, placed.FindAll(p => p.Row == 1).Count);
		}

		// ---- FormatRows ---------------------------------------------------------

		[Test]
		public void FormatRows_TextColumnLinesUpPastWidestName()
		{
			var rows = new List<(string Name, string Text)> { ("give", "adds"), ("x", "short"), ("clear-all", "empties") };
			List<Placed> placed = Render(ColumnLayout.FormatRows(rows, Measure, TabWidth, MinGap));

			// Per row: the name, then the text
			Assert.AreEqual(6, placed.Count);
			float textX = placed[1].X;
			for (int row = 0; row < 3; row++)
			{
				Placed name = placed[row * 2];
				Placed text = placed[row * 2 + 1];
				Assert.AreEqual(row, text.Row);
				Assert.AreEqual(TabWidth, name.X, 0.0001f, "indent");
				Assert.AreEqual(textX, text.X, 0.0001f, "'" + text.Item + "'");
			}

			// "clear-all" is 45 wide from 12: ends at 57, so text needs >= 63 -> 72
			Assert.AreEqual(72.0f, textX, 0.0001f);
		}

		[Test]
		public void FormatRowsPlain_PadsWithSpaces()
		{
			var rows = new List<(string Name, string Text)> { ("give", "adds"), ("x", "short") };

			Assert.AreEqual("  give  adds\n  x     short", ColumnLayout.FormatRowsPlain(rows));
		}
	}
}
