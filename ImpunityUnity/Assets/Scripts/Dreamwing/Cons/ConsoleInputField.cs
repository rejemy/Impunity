using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.EventSystems;
using Dreamwing.UI;

namespace Dreamwing.Cons
{
	public class ConsoleInputField : CustomInputField
	{
		public static int MaxCommandHistory = 100;

		private LinkedList<string> CommandHistory = new LinkedList<string>();
		private LinkedListNode<string> CommandHistoryCurrPos;
		private string CurrUnfinishedCommand;

		public Action<string> OnCommand;

		protected override void Start()
		{
			base.Start();
			//onEndEdit.AddListener(OnSubmit);
		}


		protected override EditState KeyPressed(Event evt)
		{
			if (isFocused)
			{
				switch (evt.keyCode)
				{
					case KeyCode.UpArrow:
						{
							OnUpArrowKey();
							return EditState.Continue;
						}
					case KeyCode.DownArrow:
						{
							OnDownArrowKey();
							return EditState.Continue;
						}
					case KeyCode.Return:
						{
							OnSubmit();
							return EditState.Continue;
						}
				}
			}

			return base.KeyPressed(evt);
		}

		private void OnUpArrowKey()
		{
			if (CommandHistory.Count == 0)
				return;

			string cmd = null;
			if (CommandHistoryCurrPos == null)
			{
				CurrUnfinishedCommand = text.Trim();
				CommandHistoryCurrPos = CommandHistory.Last;
				cmd = CommandHistoryCurrPos.Value;
			}
			else
			{
				if (CommandHistoryCurrPos.Previous == null)
					return;

				CommandHistoryCurrPos = CommandHistoryCurrPos.Previous;
				cmd = CommandHistoryCurrPos.Value;
			}

			text = cmd;
			SetCaretPos(cmd.Length);
		}

		private void OnDownArrowKey()
		{
			if (CommandHistory.Count == 0 || CommandHistoryCurrPos == null)
				return;

			string cmd = null;
			if (CommandHistoryCurrPos.Next == null)
			{
				cmd = CurrUnfinishedCommand;
				CommandHistoryCurrPos = null;
			}
			else
			{
				CommandHistoryCurrPos = CommandHistoryCurrPos.Next;
				cmd = CommandHistoryCurrPos.Value;
			}

			text = cmd;
			SetCaretPos(cmd.Length);
		}

		public void SetCaretPos(int pos)
		{
			caretPosition = pos;
		}

		private void AddToHistory(string commandLine)
		{
			if (CommandHistory.Count == 0 || CommandHistory.Last.Value != commandLine)
			{
				CommandHistory.AddLast(commandLine);
				while (CommandHistory.Count > MaxCommandHistory)
					CommandHistory.RemoveFirst();
			}
		}

		public void ClearHistory()
		{
			CommandHistoryCurrPos = null;
			CurrUnfinishedCommand = null;
			CommandHistory.Clear();
		}

		private void OnSubmit()
		{
			if (!Cons.IsOpen || EventSystem.current == null)
				return;

			string cmd = text;
			text = string.Empty;

			cmd = cmd.Trim();

			//EventSystem.current.SetSelectedGameObject(null);
			//EventSystem.current.SetSelectedGameObject(gameObject);

			AddToHistory(cmd);
			CommandHistoryCurrPos = null;

			if (cmd.Length > 0)
				OnCommand?.Invoke(cmd);
		}

	}
}
