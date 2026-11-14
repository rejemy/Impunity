using System;
using System.Collections.Generic;
using System.Text;

namespace Dreamwing.Cons
{
	// Lays out short items as a column-major grid (like ls), padding with tabs.
	// The console's text generator puts tab stops at a fixed interval, so padding
	// to a stop lines the columns up exactly even in a proportional font. Each row
	// is indented by one tab stop.
	public static class ColumnLayout
	{
		// Guards the tab math against a width that lands a hair under a stop in
		// float but on it in the generator (a tab from a stop goes to the next one)
		private const float StopEpsilon = 0.001f;

		public static string Format(IList<string> items, Func<string, float> measureWidth,
			float tabWidth, float lineWidth, float minGap)
		{
			int count = items.Count;
			if (count == 0)
				return "";

			float[] widths = new float[count];
			float widest = 0.0f;
			for (int i = 0; i < count; i++)
			{
				widths[i] = measureWidth(items[i]);
				widest = Math.Max(widest, widths[i]);
			}

			int columnStops = (int)Math.Ceiling((widest + minGap) / tabWidth);
			float columnWidth = columnStops * tabWidth;

			int columns = (int)((lineWidth - tabWidth) / columnWidth);
			columns = Math.Max(1, Math.Min(columns, count));
			int rows = (count + columns - 1) / columns;
			// Rebalance so the last column isn't left nearly empty
			columns = (count + rows - 1) / rows;

			StringBuilder text = new StringBuilder();
			for (int row = 0; row < rows; row++)
			{
				if (row > 0)
					text.Append('\n');
				text.Append('\t');

				for (int column = 0; column < columns; column++)
				{
					int index = column * rows + row;
					if (index >= count)
						break;

					text.Append(items[index]);

					if ((column + 1) * rows + row >= count)
						break;

					// Stops are counted from the line start, indent included
					int stopAfterItem = (int)Math.Floor((tabWidth + column * columnWidth + widths[index]) / tabWidth + StopEpsilon);
					int nextColumnStop = 1 + (column + 1) * columnStops;
					text.Append('\t', nextColumnStop - stopAfterItem);
				}
			}

			return text.ToString();
		}

		// Lays out (name, text) rows as two columns: each row indented by one tab
		// stop, its name, then tabs to the stop past the widest name, then its text
		public static string FormatRows(IList<(string Name, string Text)> rows, Func<string, float> measureWidth,
			float tabWidth, float minGap)
		{
			if (rows.Count == 0)
				return "";

			float[] widths = new float[rows.Count];
			float widest = 0.0f;
			for (int i = 0; i < rows.Count; i++)
			{
				widths[i] = measureWidth(rows[i].Name);
				widest = Math.Max(widest, widths[i]);
			}

			// Stops are counted from the line start, indent included
			int textStop = 1 + (int)Math.Ceiling((widest + minGap) / tabWidth);

			StringBuilder text = new StringBuilder();
			for (int i = 0; i < rows.Count; i++)
			{
				if (i > 0)
					text.Append('\n');

				text.Append('\t').Append(rows[i].Name);
				int stopAfterName = (int)Math.Floor((tabWidth + widths[i]) / tabWidth + StopEpsilon);
				text.Append('\t', textStop - stopAfterName).Append(rows[i].Text);
			}

			return text.ToString();
		}

		// The same, padded with spaces, for text read in a fixed-width font or by a
		// tool (captured console output)
		public static string FormatRowsPlain(IList<(string Name, string Text)> rows)
		{
			int widest = 0;
			foreach ((string name, string _) in rows)
				widest = Math.Max(widest, name.Length);

			StringBuilder text = new StringBuilder();
			for (int i = 0; i < rows.Count; i++)
			{
				if (i > 0)
					text.Append('\n');

				text.Append("  ").Append(rows[i].Name.PadRight(widest + 2)).Append(rows[i].Text);
			}

			return text.ToString();
		}
	}
}
