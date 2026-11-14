using NUnit.Framework;

using UnityEngine;

using DevTools;

namespace DevTools.Tests
{
	// UiProbe paths: what `ui dump` prints and `ui click` resolves.
	public class UiProbeTests
	{
		private GameObject Root;

		[SetUp]
		public void SetUp()
		{
			Root = new GameObject("Outside");
		}

		[TearDown]
		public void TearDown()
		{
			Object.DestroyImmediate(Root);
		}

		private GameObject Child(GameObject parent, string name)
		{
			var go = new GameObject(name, typeof(RectTransform));
			go.transform.SetParent(parent.transform, false);
			return go;
		}

		[Test]
		public void PathOf_UnderRootCanvas_StartsAtTheCanvas()
		{
			GameObject canvas = Child(Root, "PlayerUI1");
			canvas.AddComponent<Canvas>();
			GameObject button = Child(Child(canvas, "Dialog"), "OkButton");

			Assert.AreEqual("PlayerUI1/Dialog/OkButton", UiProbe.PathOf(button.transform));
		}

		[Test]
		public void PathOf_RepeatedSiblingNames_IndexesOnlyThose()
		{
			GameObject canvas = Child(Root, "UI");
			canvas.AddComponent<Canvas>();
			GameObject content = Child(canvas, "Content");
			Child(content, "Choice");
			Child(content, "Header");
			GameObject second = Child(content, "Choice");

			Assert.AreEqual("UI/Content/Choice[1]", UiProbe.PathOf(second.transform));
			Assert.AreEqual("UI/Content/Header", UiProbe.PathOf(content.transform.GetChild(1)));
		}

		[Test]
		public void PathOf_NestedCanvas_RunsToTheRootCanvas()
		{
			GameObject canvas = Child(Root, "UI");
			canvas.AddComponent<Canvas>();
			GameObject inner = Child(canvas, "Panel");
			inner.AddComponent<Canvas>();
			GameObject button = Child(inner, "Button");

			Assert.AreEqual("UI/Panel/Button", UiProbe.PathOf(button.transform));
		}
	}
}
