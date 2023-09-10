using System;

using UnityEngine;

using Dreamwing.Pooling;

namespace Dreamwing.Tasks
{
	public abstract class TaskBase
	{
		public int TaskId;
		public TaskBase NextTask;
		public float StartedAt;
		public bool Cancelled;

		public Action<bool> CompleteCallback;

		public virtual void Recycle()
		{
			TaskId = 0;
			NextTask = null;
			StartedAt = 0.0f;
			Cancelled = false;
			CompleteCallback = null;
		}

		public virtual void Init(int taskId, float startedAt)
		{
			TaskId = taskId;
			StartedAt = startedAt;
		}


		public virtual bool IsValid()
		{
			return true;
		}

		public abstract bool Update(float time);
		public virtual void Cleanup() { }

		public virtual void Restart()
		{
			StartedAt = 0.0f;
		}

		public virtual void Cancel()
		{
			Cancelled = true;

			CallCallback(true);
			Cleanup();
		}

		public void CallCallback(bool cancelled)
		{
			if (CompleteCallback == null)
				return;

			try
			{
				CompleteCallback(cancelled);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
		}

	}

	public interface ITaskHandle
	{
		void OnComplete(Action<bool> callback);
		bool IsCurrent();
		void Cancel();
	}

	public struct TaskHandle : ITaskHandle
	{
		private int TaskId;
		private TaskBase Task;

		public TaskHandle(TaskBase task)
		{
			if (task != null)
			{
				TaskId = task.TaskId;
				Task = task;
			}
			else
			{
				TaskId = 0;
				Task = null;
			}
		}

		public void OnComplete(Action<bool> callback)
		{
			if (Task == null || TaskId != Task.TaskId)
				return;

			Task.CompleteCallback = callback;
		}

		public bool IsCurrent()
		{
			return Task != null && Task.TaskId == TaskId;
		}

		public void Cancel()
		{
			if (Task == null || TaskId != Task.TaskId)
				return;

			Task.Cancel();
		}

	}

	public class DelayedCallback : TaskBase
	{
		public static PocoPool<DelayedCallback> Pool = new PocoPool<DelayedCallback>();

		public Action Callback;

		public void Init(Action callback)
		{
			Callback = callback;
		}

		public override bool Update(float time)
		{
			Callback();
			return true;
		}

		public override void Recycle()
		{
			base.Recycle();

			Callback = null;

			Pool.Return(this);
		}
	}


	public class TaskManager
	{
		public static TaskManager Realtime;
		public static TaskManager Gametime;

		bool IsRealtime;
		TaskBase CurrentTasks;
		TaskBase FutureTasks;
		int NextTaskId = 1;

		public static void Init()
		{
			Realtime = new TaskManager(true);
			Gametime = new TaskManager(false);
		}

		public static void Update()
		{
			Realtime.DoUpdate();
			Gametime.DoUpdate();
		}


		public TaskManager(bool realtime)
		{
			IsRealtime = realtime;
		}

		public float GetTime()
		{
			return IsRealtime ? Time.unscaledTime : Time.time;
		}

		public TaskHandle StartTask(TaskBase task)
		{
			task.Init(NextTaskId++, GetTime());

			bool done = false;
			try
			{
				done = task.Update(0.0f);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				done = true;
			}

			if (done)
			{
				try
				{
					task.Cleanup();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}

				return new TaskHandle(null);
			}

			task.NextTask = CurrentTasks;
			CurrentTasks = task;

			return new TaskHandle(task);
		}

		public TaskHandle ScheduleTask(TaskBase task, TimeSpan time)
		{
			return ScheduleTask(task, (float)time.TotalSeconds);
		}

		public TaskHandle ScheduleTask(TaskBase task, float time)
		{
			task.Init(NextTaskId++, GetTime() + time);

			if (FutureTasks == null || FutureTasks.StartedAt > task.StartedAt)
			{
				task.NextTask = FutureTasks;
				FutureTasks = task;
			}
			else
			{
				TaskBase currTask = FutureTasks;
				while (currTask.NextTask != null && currTask.NextTask.StartedAt <= task.StartedAt)
				{
					currTask = currTask.NextTask;
				}

				task.NextTask = currTask.NextTask;
				currTask.NextTask = task;
			}

			return new TaskHandle(task);
		}

		public TaskHandle CallInNextFrame(Action callback)
		{
			DelayedCallback task = DelayedCallback.Pool.Get();
			task.Init(callback);
			return ScheduleTask(task, 0.0f);
		}

		public TaskHandle CallIn(TimeSpan time, Action callback)
		{
			return CallIn((float)time.TotalSeconds, callback);
		}

		public TaskHandle CallIn(float seconds, Action callback)
		{
			DelayedCallback task = DelayedCallback.Pool.Get();
			task.Init(callback);
			return ScheduleTask(task, seconds);
		}

		public void DoUpdate()
		{
			float time = GetTime();
			RunCurrentTasks(time);
			CheckFutureTasks(time);
		}

		private void RunCurrentTasks(float frameTime)
		{
			TaskBase lastTask = null;
			TaskBase currTask = CurrentTasks;

			while (currTask != null)
			{
				bool cancelled = false;
				bool done = false;

				bool valid = false;
				try
				{
					valid = currTask.IsValid();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}

				if (currTask.Cancelled || !valid)
				{
					done = true;
					cancelled = true;
				}
				else
				{
					if (currTask.StartedAt <= 0.0f)
						currTask.StartedAt = GetTime();

					float time = frameTime - currTask.StartedAt;
					try
					{
						done = currTask.Update(time);
					}
					catch (Exception e)
					{
						Debug.LogException(e);
						done = true;
					}

				}

				if (done)
				{
					TaskBase toRecycle = currTask;

					if (lastTask != null)
					{
						lastTask.NextTask = currTask.NextTask;
						currTask = currTask.NextTask;
					}
					else
					{
						CurrentTasks = currTask.NextTask;
						currTask = currTask.NextTask;
					}

					if (valid && !cancelled)
					{
						toRecycle.CallCallback(cancelled);
						try
						{
							toRecycle.Cleanup();
						}
						catch (Exception e)
						{
							Debug.LogException(e);
						}
					}

					try
					{
						toRecycle.Recycle();
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}

				}
				else
				{
					lastTask = currTask;
					currTask = currTask.NextTask;
				}
			}
		}

		private void CheckFutureTasks(float frameTime)
		{
			while (FutureTasks != null && FutureTasks.StartedAt <= frameTime)
			{
				TaskBase task = FutureTasks;
				FutureTasks = FutureTasks.NextTask;

				bool valid = false;
				try
				{
					valid = task.IsValid();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}

				if (task.Cancelled || !valid)
				{
					try
					{
						task.Recycle();
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}

					continue;
				}

				task.StartedAt = frameTime;

				bool done = false;
				try
				{
					done = task.Update(0.0f);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
					done = true;
				}

				if (done)
				{
					try
					{
						task.Cleanup();
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}

				}
				else
				{
					task.NextTask = CurrentTasks;
					CurrentTasks = task;
				}
			}
		}

	}

}
