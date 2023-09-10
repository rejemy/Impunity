using System;

using UnityEngine;
using UnityEngine.UI;

using Dreamwing.Tasks;
using Dreamwing.Pooling;

namespace Dreamwing
{

	internal abstract class GenericEasingTask : TaskBase
	{
		protected float Duration;
		protected Func<float, float> Easer;

		protected Component EasedObject;
		protected PoolableHandle Handle;

		public void Init(Component easedObject, float duration, Func<float, float> easer)
		{
			EasedObject = easedObject;
			Duration = duration;
			Easer = easer;
			Handle = PoolableHandle.makeFrom(easedObject);
		}

		public override bool Update(float time)
		{
			if (Handle.isExpired() || !EasedObject.gameObject.activeSelf)
			{
				return true;
			}

			bool complete = false;
			if (time >= Duration)
			{
				time = Duration;
				complete = true;
			}

			float eased = Easer(time / Duration);

			try
			{
				UpdateObject(EasedObject, eased);
			}
			catch (Exception e)
			{
				Debug.Log("Exception in easer updater: " + e.Message);
			}

			return complete;
		}

		protected abstract void UpdateObject(object target, float e);

		public override void Recycle()
		{
			base.Recycle();

			Duration = 0.0f;
			Easer = null;
			EasedObject = null;
			Handle.clear();
		}
	}

	internal class FloatEasingTask : GenericEasingTask
	{
		public static PocoPool<FloatEasingTask> Pool = new PocoPool<FloatEasingTask>();

		protected Action<object, float> Updater;

		private float Start;
		private float End;

		public void Init(Component easedObject, float start, float end, float duration, Func<float, float> easer, Action<object, float> updater)
		{
			base.Init(easedObject, duration, easer);

			Updater = updater;
			Start = start;
			End = end;
		}


		protected override void UpdateObject(object target, float e)
		{
			Updater.Invoke(EasedObject, Mathf.LerpUnclamped(Start, End, e));
		}

		public override void Recycle()
		{
			base.Recycle();
			Updater = null;

			Pool.Return(this);
		}
	}

	internal class Vector2EasingTask : GenericEasingTask
	{
		public static PocoPool<Vector2EasingTask> Pool = new PocoPool<Vector2EasingTask>();

		protected Action<object, Vector2> Updater;

		private Vector2 Start;
		private Vector2 End;

		public void Init(Component easedObject, Vector2 start, Vector2 end, float duration, Func<float, float> easer, Action<object, Vector2> updater)
		{
			base.Init(easedObject, duration, easer);

			Updater = updater;
			Start = start;
			End = end;
		}


		protected override void UpdateObject(object target, float e)
		{
			Updater.Invoke(EasedObject, Vector2.LerpUnclamped(Start, End, e));
		}

		public override void Recycle()
		{
			base.Recycle();
			Updater = null;

			Pool.Return(this);
		}
	}

	internal class Vector3EasingTask : GenericEasingTask
	{
		public static PocoPool<Vector3EasingTask> Pool = new PocoPool<Vector3EasingTask>();

		protected Action<object, Vector3> Updater;

		private Vector3 Start;
		private Vector3 End;

		public void Init(Component easedObject, Vector3 start, Vector3 end, float duration, Func<float, float> easer, Action<object, Vector3> updater)
		{
			base.Init(easedObject, duration, easer);

			Updater = updater;
			Start = start;
			End = end;
		}


		protected override void UpdateObject(object target, float e)
		{
			Updater.Invoke(EasedObject, Vector3.LerpUnclamped(Start, End, e));
		}

		public override void Recycle()
		{
			base.Recycle();
			Updater = null;

			Pool.Return(this);
		}
	}

	internal class QuaternionEasingTask : GenericEasingTask
	{
		public static PocoPool<QuaternionEasingTask> Pool = new PocoPool<QuaternionEasingTask>();

		protected Action<object, Quaternion> Updater;

		private Quaternion Start;
		private Quaternion End;

		public void Init(Component easedObject, Quaternion start, Quaternion end, float duration, Func<float, float> easer, Action<object, Quaternion> updater)
		{
			base.Init(easedObject, duration, easer);

			Updater = updater;
			Start = start;
			End = end;
		}


		protected override void UpdateObject(object target, float e)
		{
			Updater.Invoke(EasedObject, Quaternion.LerpUnclamped(Start, End, e));
		}

		public override void Recycle()
		{
			base.Recycle();
			Updater = null;

			Pool.Return(this);
		}
	}

	internal class ColorEasingTask : GenericEasingTask
	{
		public static PocoPool<ColorEasingTask> Pool = new PocoPool<ColorEasingTask>();

		protected Action<object, Color> Updater;

		private Color Start;
		private Color End;

		public void Init(Component easedObject, Color start, Color end, float duration, Func<float, float> easer, Action<object, Color> updater)
		{
			base.Init(easedObject, duration, easer);

			Updater = updater;
			Start = start;
			End = end;
		}


		protected override void UpdateObject(object target, float e)
		{
			Updater.Invoke(EasedObject, Color.LerpUnclamped(Start, End, e));
		}

		public override void Recycle()
		{
			base.Recycle();
			Updater = null;

			Pool.Return(this);
		}
	}


	public static class Easing
	{
		public static TaskHandle EaseFloat(Component easedObject, float start, float end, float duration, Func<float, float> easer, Action<object, float> updater, bool realtime = true)
		{
			if (duration < 0.0f)
				duration = 0.0f;

			FloatEasingTask task = FloatEasingTask.Pool.Get();
			task.Init(easedObject, start, end, duration, easer, updater);

			if (realtime)
				return TaskManager.Realtime.StartTask(task);
			else
				return TaskManager.Gametime.StartTask(task);
		}

		public static TaskHandle EaseVector2(Component easedObject, Vector2 start, Vector2 end, float duration, Func<float, float> easer, Action<object, Vector2> updater, bool realtime = true)
		{
			if (duration < 0.0f)
				duration = 0.0f;

			Vector2EasingTask task = Vector2EasingTask.Pool.Get();
			task.Init(easedObject, start, end, duration, easer, updater);

			if (realtime)
				return TaskManager.Realtime.StartTask(task);
			else
				return TaskManager.Gametime.StartTask(task);
		}

		public static TaskHandle EaseVector3(Component easedObject, Vector3 start, Vector3 end, float duration, Func<float, float> easer, Action<object, Vector3> updater, bool realtime = true)
		{
			if (duration < 0.0f)
				duration = 0.0f;

			Vector3EasingTask task = Vector3EasingTask.Pool.Get();
			task.Init(easedObject, start, end, duration, easer, updater);

			if (realtime)
				return TaskManager.Realtime.StartTask(task);
			else
				return TaskManager.Gametime.StartTask(task);
		}

		public static TaskHandle EaseQuaternion(Component easedObject, Quaternion start, Quaternion end, float duration, Func<float, float> easer, Action<object, Quaternion> updater, bool realtime = true)
		{
			if (duration < 0.0f)
				duration = 0.0f;

			QuaternionEasingTask task = QuaternionEasingTask.Pool.Get();
			task.Init(easedObject, start, end, duration, easer, updater);

			if (realtime)
				return TaskManager.Realtime.StartTask(task);
			else
				return TaskManager.Gametime.StartTask(task);
		}

		public static TaskHandle EaseColor(Component easedObject, Color start, Color end, float duration, Func<float, float> easer, Action<object, Color> updater, bool realtime = true)
		{
			if (duration < 0.0f)
				duration = 0.0f;

			ColorEasingTask task = ColorEasingTask.Pool.Get();
			task.Init(easedObject, start, end, duration, easer, updater);

			if (realtime)
				return TaskManager.Realtime.StartTask(task);
			else
				return TaskManager.Gametime.StartTask(task);
		}


		// Tform easers
		public static TaskHandle EaseScale(Transform tform, Vector3 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector3(tform, tform.localScale, end, duration, easer, (object obj, Vector3 e) =>
			{
				((Transform)obj).localScale = e;
			}, realtime);
		}

		public static TaskHandle EaseRotation(Transform tform, Quaternion end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseQuaternion(tform, tform.localRotation, end, duration, easer, (object obj, Quaternion e) =>
			{
				((Transform)obj).localRotation = e;
			}, realtime);
		}

		public static TaskHandle EaseRotation(Transform tform, Vector3 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector3(tform, tform.localEulerAngles, end, duration, easer, (object obj, Vector3 e) =>
			{
				((Transform)obj).localEulerAngles = e;
			}, realtime);
		}

		public static TaskHandle EasePosition(Transform tform, Vector3 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector3(tform, tform.localPosition, end, duration, easer, (object obj, Vector3 e) =>
			{
				((Transform)obj).localPosition = e;
			}, realtime);
		}


		public static TaskHandle EaseAnchoredPosition(RectTransform tform, Vector2 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector2(tform, tform.anchoredPosition, end, duration, easer, (object obj, Vector2 e) =>
			{
				((RectTransform)obj).anchoredPosition = e;
			}, realtime);
		}

		public static TaskHandle EaseAnchoredPosition3D(RectTransform tform, Vector3 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector3(tform, tform.anchoredPosition3D, end, duration, easer, (object obj, Vector3 e) =>
			{
				((RectTransform)obj).anchoredPosition3D = e;
			}, realtime);
		}

		public static TaskHandle EaseAnchorMax(RectTransform tform, Vector2 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector2(tform, tform.anchorMax, end, duration, easer, (object obj, Vector2 e) =>
			{
				((RectTransform)obj).anchorMax = e;
			}, realtime);
		}

		public static TaskHandle EaseAnchorMin(RectTransform tform, Vector2 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector2(tform, tform.anchorMin, end, duration, easer, (object obj, Vector2 e) =>
			{
				((RectTransform)obj).anchorMin = e;
			}, realtime);
		}

		public static TaskHandle EaseOffsetMax(RectTransform tform, Vector2 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector2(tform, tform.offsetMax, end, duration, easer, (object obj, Vector2 e) =>
			{
				((RectTransform)obj).offsetMax = e;
			}, realtime);
		}

		public static TaskHandle EaseOffsetMin(RectTransform tform, Vector2 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector2(tform, tform.offsetMin, end, duration, easer, (object obj, Vector2 e) =>
			{
				((RectTransform)obj).offsetMin = e;
			}, realtime);
		}

		public static TaskHandle EasePivot(RectTransform tform, Vector2 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector2(tform, tform.pivot, end, duration, easer, (object obj, Vector2 e) =>
			{
				((RectTransform)obj).pivot = e;
			}, realtime);
		}

		public static TaskHandle EaseSizeDelta(RectTransform tform, Vector2 end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseVector2(tform, tform.sizeDelta, end, duration, easer, (object obj, Vector2 e) =>
			{
				((RectTransform)obj).sizeDelta = e;
			}, realtime);
		}

		public static TaskHandle EaseColor(Graphic uiGraphic, Color end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseColor(uiGraphic, uiGraphic.color, end, duration, easer, (object obj, Color e) =>
			{
				((Graphic)obj).color = e;
			}, realtime);
		}

		public static TaskHandle EaseAlpha(Graphic uiGraphic, float end, float duration, Func<float, float> easer, bool realtime = true)
		{
			Color endColor = uiGraphic.color;
			endColor.a = end;
			return EaseColor(uiGraphic, endColor, duration, easer, realtime);
		}

		public static TaskHandle EaseAlpha(CanvasGroup group, float end, float duration, Func<float, float> easer, bool realtime = true)
		{
			return EaseFloat(group, group.alpha, end, duration, easer, (object obj, float e) =>
			{
				((CanvasGroup)obj).alpha = e;
			}, realtime);
		}

	}

}