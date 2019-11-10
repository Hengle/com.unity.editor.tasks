// Copyright 2016-2019 Andreia Gaita
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unity.Editor.Tasks
{
	using Logging;

	public interface ITaskManager : IDisposable
	{
		event Action<IProgress> OnProgress;

		T Schedule<T>(T task) where T : ITask;
		ITask Run(Action action, string message = null);
		ITask RunInUI(Action action, string message = null);

		/// <summary>
		/// Call this from the main thread so task manager knows which thread is the main thread
		/// It uses the current synchronization context to queue tasks to the main thread
		/// </summary>
		ITaskManager Initialize();

		/// <summary>
		/// Call this from a thread different from the the main thread. This will call
		/// synchronizationContext.Send() in order to set up the task manager on the
		/// thread of the synchronizationContext.
		/// </summary>
		ITaskManager Initialize(SynchronizationContext synchronizationContext);

		TaskScheduler GetScheduler(TaskAffinity affinity);
		TaskScheduler ConcurrentScheduler { get; }
		TaskScheduler ExclusiveScheduler { get; }
		TaskScheduler LongRunningScheduler { get; }
		TaskScheduler UIScheduler { get; set; }
		CancellationToken Token { get; }
		bool InUIThread { get; }
		int UIThread { get; }
	}

	public class TaskManager : ITaskManager
	{
		private readonly ILogging logger;
		private readonly ConcurrentExclusiveSchedulerPairCustom manager;

		private readonly ProgressReporter progressReporter = new ProgressReporter();
		private CancellationTokenSource cts;
		private ThreadingHelper threadingHelper;

		public event Action<IProgress> OnProgress
		{
			add => progressReporter.OnProgress += value;
			remove => progressReporter.OnProgress -= value;
		}

		public TaskManager()
		{
			cts = new CancellationTokenSource();
			manager = new ConcurrentExclusiveSchedulerPairCustom(cts.Token);
			threadingHelper = new ThreadingHelper();
			logger = LogHelper.GetLogger<TaskManager>();
		}

	/// <summary>
	/// Run this on the thread you would like to use as the main thread
	/// </summary>
	/// <returns></returns>
	public ITaskManager Initialize()
		{
			return Initialize(ThreadingHelper.GetUIScheduler(SynchronizationContext.Current));
		}

		/// <summary>
		/// Run this on a thread different from the main thread represented by the
		/// synchronization context.
		/// </summary>
		/// <param name="synchronizationContext"></param>
		/// <returns></returns>
		public ITaskManager Initialize(SynchronizationContext synchronizationContext)
		{
			synchronizationContext.Send(s => ((ITaskManager)s).Initialize(), this);
			return this;
		}

		private ITaskManager Initialize(TaskScheduler uiTaskScheduler)
		{
			UIScheduler = uiTaskScheduler;
			threadingHelper.SetUIThread();
			LongRunningScheduler = new TaskSchedulerExcludingThread(threadingHelper.MainThread);
			return this;
		}

		public TaskScheduler GetScheduler(TaskAffinity affinity)
		{
			switch (affinity)
			{
				case TaskAffinity.Exclusive:
					return ExclusiveScheduler;
				case TaskAffinity.UI:
					return UIScheduler;
				case TaskAffinity.LongRunning:
					return LongRunningScheduler;
				case TaskAffinity.Concurrent:
				default:
					return ConcurrentScheduler;
			}
		}

		public ITask Run(Action action, string message = null)
		{
			return new ActionTask(this, action) { Message = message }.Start();
		}

		public ITask RunInUI(Action action, string message = null)
		{
			return new ActionTask(this, action) { Affinity = TaskAffinity.UI, Message = message }.Start();
		}

		public T Schedule<T>(T task)
			where T : ITask
		{
			Schedule((TaskBase)(object)task, GetScheduler(task.Affinity), true, task.Affinity.ToString());
			return task;
		}

		private void Schedule(TaskBase task, TaskScheduler scheduler, bool setupFaultHandler, string schedulerName)
		{
			if (setupFaultHandler)
			{
				// we run this exception handler in the long running scheduler so it doesn't get blocked
				// by any exclusive tasks that might be running
				task.Task.ContinueWith(tt => {
						Exception ex = tt.Exception.GetBaseException();
						while (ex.InnerException != null) ex = ex.InnerException;
						logger.Trace(ex, $"Exception on {schedulerName} thread: {tt.Id} {task.Name}");
					},
					cts.Token,
					TaskContinuationOptions.OnlyOnFaulted,
					GetScheduler(TaskAffinity.LongRunning)
				);
			}

			task.Progress(progressReporter.UpdateProgress);
			task.Start(scheduler);
		}

		public async Task Stop()
		{
			if (cts == null)
				throw new ObjectDisposedException(nameof(TaskManager));

			// tell all schedulers to stop scheduling new tasks
			manager.Complete();
			// tell all tasks to exit
			cts.Cancel();
			cts = null;
			// wait for everything to shut down within 500ms
			await Task.WhenAny(manager.Completion, Task.Delay(500));
		}

		private bool disposed = false;

		private void Dispose(bool disposing)
		{
			if (disposed) return;
			disposed = true;
			if (disposing)
			{
				Stop().FireAndForget();
			}
		}

		public void Dispose()
		{
			Dispose(true);
		}

		public TaskScheduler UIScheduler { get; set; }
		public TaskScheduler ConcurrentScheduler => manager.ConcurrentScheduler;
		public TaskScheduler ExclusiveScheduler => manager.ExclusiveScheduler;
		public TaskScheduler LongRunningScheduler { get; private set; }
		public CancellationToken Token => cts.Token;
		public bool InUIThread => threadingHelper.InUIThread;
		public int UIThread => threadingHelper.MainThread;
	}
}
