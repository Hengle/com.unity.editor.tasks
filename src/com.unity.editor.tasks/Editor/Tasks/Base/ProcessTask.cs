// Copyright 2016-2019 Andreia Gaita
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Unity.Editor.Tasks
{
	using Extensions;
	using Unity.Editor.Tasks.Helpers;

	/// <summary>
	/// An external process managed by the <see cref="IProcessManager" /> and
	/// wrapped by a <see cref="IProcessTask" />
	/// </summary>
	public interface IProcess
	{
		/// <summary>
		/// Event raised when the process exits
		/// </summary>
		event Action<IProcess> OnEndProcess;
		/// <summary>
		/// Event raised after the process is finished, with any output that the process sent to stderr.
		/// </summary>
		event Action<string> OnErrorData;
		/// <summary>
		/// Event raised when the process is started.
		/// </summary>
		event Action<IProcess> OnStartProcess;
		/// <summary>
		/// Stops the process.
		/// </summary>
		void Stop();
		/// <summary>
		/// The StandardInput of the process is redirected to this stream writer.
		/// </summary>
		StreamWriter StandardInput { get; }
		/// <summary>
		/// The underlying process id.
		/// </summary>
		int ProcessId { get; }
		/// <summary>
		/// The process name.
		/// </summary>
		string ProcessName { get; }
		/// <summary>
		/// The process arguments.
		/// </summary>
		string ProcessArguments { get; }
		/// <summary>
		/// The underlying process object.
		/// </summary>
		Process Process { get; set; }
	}

	/// <summary>
	/// A task that runs an external process.
	/// </summary>
	public interface IProcessTask : ITask, IProcess
	{
		/// <summary>
		/// The environment for the process. This is a wrapper of <see cref="IEnvironment" /> that also includes a working directory,
		/// and configures environment variables of the process.
		/// </summary>
		IProcessEnvironment ProcessEnvironment { get; }

		/// <summary>
		/// Helper that configures the underlying process object with the passed in <paramref name="psi"/> object,
		/// and optionally sets an output processor, if one wasn't set in the constructor, or if a subclass wants
		/// to handle it here instead.
		/// </summary>
		/// <param name="psi">The StartInfo object for the process.</param>
		void Configure(ProcessStartInfo psi);

		/// <summary>
		/// An overloaded <see cref="ITask.Start" /> method that returns IProcessTask, to make it easier to chain.
		/// </summary>
		/// <returns>The started task.</returns>
		new IProcessTask Start();

		/// <summary>
		/// If you call this on a running process task, it will trigger the task to finish, raising
		/// OnEnd and OnEndProcess, without stopping the underlying process. Process manager won't
		/// stop a released process on shutdown. This will effectively leak the process, but if you
		/// need to run a background process that won't be stopped if the domain goes down, call this.
		/// </summary>
		void Detach();
		bool LongRunning { get; }
	}

	/// <summary>
	/// A task that runs an external process and returns the process output.
	/// </summary>
	/// <typeparam name="T">The output of the process, processed via an IOutputProcessor.</typeparam>
	public interface IProcessTask<T> : ITask<T>, IProcessTask
	{
		/// <summary>
		/// Helper that configures the underlying process object with the passed in <paramref name="psi"/> object,
		/// and optionally sets an output processor, if one wasn't set in the constructor, or if a subclass wants
		/// to handle it here instead.
		/// </summary>
		/// <param name="psi">The StartInfo object for the process.</param>
		/// <param name="processor">The output processor to use to process the process output.</param>
		void Configure(ProcessStartInfo psi, IOutputProcessor<T> processor = null);

		/// <summary>
		/// An overloaded <see cref="ITask.Start" /> method that returns IProcessTask, to make it easier to chain.
		/// </summary>
		/// <returns>The started task.</returns>
		new IProcessTask<T> Start();

		/// <inheritdoc />
		event Action<T> OnOutput;
	}


	/// <summary>
	/// A task that runs an external process and returns the process output, converting it in the process to
	/// a different type. This is mainly for creating lists of data, where <typeparamref name="TData"/> is
	/// the type of a single item, and <typeparamref name="T" /> is a List&lt;TData&gt;.
	/// The base <see cref="ITask&lt;TData, T&gt;" /> provides a <see cref="ITask&lt;TData, T&gt;.OnData" />
	/// event that is called whenever the output processor raised the OnEntry event.
	/// </summary>
	/// <typeparam name="TData"></typeparam>
	/// <typeparam name="T"></typeparam>
	public interface IProcessTask<TData, T> : ITask<TData, T>, IProcessTask
	{
		/// <summary>
		/// Helper that configures the underlying process object with the passed in <paramref name="psi"/> object,
		/// and optionally sets an output processor, if one wasn't set in the constructor, or if a subclass wants
		/// to handle it here instead.
		/// </summary>
		/// <param name="psi">The StartInfo object for the process.</param>
		/// <param name="processor">The output processor to use to process the process output.</param>
		void Configure(ProcessStartInfo psi, IOutputProcessor<TData, T> processor = null);

		/// <summary>
		/// An overloaded <see cref="ITask.Start" /> method that returns IProcessTask, to make it easier to chain.
		/// </summary>
		/// <returns>The started task.</returns>
		new IProcessTask<TData, T> Start();
	}


	/// <summary>
	/// A task that runs an external process and returns the process output.
	/// </summary>
	/// <typeparam name="T">The output of the process, processed via an IOutputProcessor.</typeparam>
	public class ProcessTask<T> : TaskBase<T>, IProcessTask<T>, IDisposable
	{
		private Exception thrownException = null;
		private BaseProcessWrapper wrapper;

		/// <inheritdoc />
		public event Action<IProcess> OnEndProcess;
		/// <inheritdoc />
		public event Action<string> OnErrorData;
		/// <inheritdoc />
		public event Action<IProcess> OnStartProcess;
		/// <inheritdoc />
		public event Action<T> OnOutput;

		/// <summary>
		/// Runs a Process with the passed arguments
		/// </summary>
		/// <param name="executable"></param>
		/// <param name="arguments"></param>
		/// <param name="outputProcessor"></param>
		/// <param name="taskManager"></param>
		/// <param name="processEnvironment"></param>
		public ProcessTask(ITaskManager taskManager,
			IProcessEnvironment processEnvironment,
			string executable = null,
			string arguments = null,
			IOutputProcessor<T> outputProcessor = null
			)
			 : this(taskManager, taskManager?.Token ?? default, processEnvironment, executable, arguments, outputProcessor)
		{}

		/// <summary>
		/// Runs a Process with the passed arguments
		/// </summary>
		/// <param name="taskManager"></param>
		/// <param name="token"></param>
		/// <param name="processEnvironment"></param>
		/// <param name="executable"></param>
		/// <param name="arguments"></param>
		/// <param name="outputProcessor"></param>
		public ProcessTask(ITaskManager taskManager,
			CancellationToken token,
			IProcessEnvironment processEnvironment,
			string executable = null,
			string arguments = null,
			IOutputProcessor<T> outputProcessor = null
			)
			 : base(taskManager, token)
		{
			OutputProcessor = outputProcessor;
			ProcessEnvironment = processEnvironment;
			ProcessArguments = arguments;
			ProcessName = executable;
		}

		/// <inheritdoc />
		public virtual void Configure(ProcessStartInfo psi, IOutputProcessor<T> processor = null)
		{
			OutputProcessor = processor ?? OutputProcessor;
			ConfigureOutputProcessor();

			this.EnsureNotNull(OutputProcessor, nameof(OutputProcessor));

			Process = new Process { StartInfo = psi, EnableRaisingEvents = true };
			ProcessName = psi.FileName;
			Name = ProcessArguments;
			OutputProcessor.OnEntry += s => OnOutput?.Invoke(s);
		}

		/// <inheritdoc />
		void IProcessTask.Configure(ProcessStartInfo psi) => Configure(psi, null);

		/// <inheritdoc />
		public new IProcessTask<T> Start()
		{
			base.Start();
			return this;
		}

		/// <inheritdoc />
		IProcessTask IProcessTask.Start()
		{
			return Start();
		}

		/// <inheritdoc />
		public void Stop()
		{
			wrapper?.Stop();
		}

		public virtual void Detach()
		{
			wrapper?.Detach();
		}

		/// <inheritdoc />
		public override string ToString()
		{
			return $"{Task?.Id ?? -1} {Name} {GetType()} {ProcessName} {ProcessArguments}";
		}

		/// <summary>
		/// Called when the process has been started.
		/// </summary>
		protected virtual void RaiseOnStartProcess()
		{
			OnStartProcess?.Invoke(this);
			OnStartProcess = null;
		}

		/// <summary>
		/// Call after OnEnd, when the process has finished.
		/// </summary>
		protected virtual void RaiseOnEndProcess()
		{
			OnEndProcess?.Invoke(this);
			OnEndProcess = null;
		}

		/// <inheritdoc />
		protected virtual void ConfigureOutputProcessor()
		{}

		protected virtual BaseProcessWrapper GetWrapper(string taskName,
			Process process,
			IOutputProcessor outputProcessor,
			bool longRunning,
			Action onStart,
			Action onEnd,
			Action<Exception, string> onError,
			CancellationToken token)
		{
			return new ProcessWrapper(taskName, process, outputProcessor,
				longRunning, onStart, onEnd, onError, token);
		}

		/// <inheritdoc />
		protected override T RunWithReturn(bool success)
		{
			var result = base.RunWithReturn(success);

			try
			{
				wrapper = GetWrapper(Name, Process, OutputProcessor, LongRunning,
					RaiseOnStartProcess,
					() => {
						try
						{
							if (OutputProcessor != null)
								result = OutputProcessor.Result;

							if (typeof(T) == typeof(string) && result == null && !Process.StartInfo.CreateNoWindow)
								result = (T)(object)"Process running";

							if (!String.IsNullOrEmpty(Errors))
								RaiseOnErrorData();
						}
						catch (Exception ex)
						{
							if (thrownException == null)
								thrownException = new ProcessException(ex.Message, ex);
							else
								thrownException = new ProcessException(thrownException.GetExceptionMessage(), ex);
						}

						if (thrownException != null && !RaiseFaultHandlers(thrownException))
						{
							RaiseOnEndProcess();
							Exception.Rethrow();
						}
						RaiseOnEndProcess();
					},
					(ex, error) => {
						thrownException = ex;
						Errors = error;
					},
					Token);

			}
			catch (Exception ex)
			{
				if (!RaiseFaultHandlers(ex))
				{
					Exception.Rethrow();
				}
			}

			wrapper.Run();

			return result;
		}

		protected virtual void RaiseOnErrorData()
		{
			OnErrorData?.Invoke(Errors);
		}

		private bool disposed;
		/// <inheritdoc />
		protected virtual void Dispose(bool disposing)
		{
			if (disposed) return;
			if (disposing)
			{
				wrapper?.Dispose();
				disposed = true;
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <inheritdoc />
		public IProcessEnvironment ProcessEnvironment { get; private set; }
		/// <inheritdoc />
		public Process Process { get; set; }
		/// <inheritdoc />
		public int ProcessId => wrapper.ProcessId;
		/// <inheritdoc />
		public override bool Successful => base.Successful && wrapper.ExitCode == 0;
		/// <inheritdoc />
		public StreamWriter StandardInput => wrapper?.Input;
		/// <inheritdoc />
		public virtual string ProcessName { get; protected set; }
		/// <inheritdoc />
		public virtual string ProcessArguments { get; }

		/// <inheritdoc />
		public bool LongRunning { get; set; }

		/// <inheritdoc />
		protected IOutputProcessor<T> OutputProcessor { get; set; }
	}

	/// <summary>
	/// A helper process task that returns a list of data from the output of the process.
	/// </summary>
	/// <typeparam name="T">The type of the items on the returned list.</typeparam>
	public class ProcessTaskWithListOutput<T> : DataTaskBase<T, List<T>>, IProcessTask<T, List<T>>, IDisposable
	{
		private Exception thrownException = null;
		private BaseProcessWrapper wrapper;

		/// <inheritdoc />
		public event Action<IProcess> OnEndProcess;
		/// <inheritdoc />
		public event Action<string> OnErrorData;
		/// <inheritdoc />
		public event Action<IProcess> OnStartProcess;
		/// <inheritdoc />
		public event Action<T> OnOutput;

		/// <summary>
		/// Runs a Process with the passed arguments
		/// </summary>
		/// <param name="taskManager"></param>
		/// <param name="processEnvironment"></param>
		/// <param name="executable"></param>
		/// <param name="arguments"></param>
		/// <param name="outputProcessor"></param>
		public ProcessTaskWithListOutput(
			ITaskManager taskManager,
			IProcessEnvironment processEnvironment,
			string executable = null,
			string arguments = null,
			IOutputProcessor<T, List<T>> outputProcessor = null)
			 : this(taskManager, taskManager?.Token ?? default, processEnvironment, executable, arguments, outputProcessor)
		{}

		/// <summary>
		/// Runs a Process with the passed arguments
		/// </summary>
		/// <param name="taskManager"></param>
		/// <param name="token"></param>
		/// <param name="processEnvironment"></param>
		/// <param name="executable"></param>
		/// <param name="arguments"></param>
		/// <param name="outputProcessor"></param>
		public ProcessTaskWithListOutput(
			ITaskManager taskManager,
			CancellationToken token,
			IProcessEnvironment processEnvironment,
			string executable = null,
			string arguments = null,
			IOutputProcessor<T, List<T>> outputProcessor = null)
			 : base(taskManager, token)
		{
			this.OutputProcessor = outputProcessor;
			ProcessEnvironment = processEnvironment;
			ProcessArguments = arguments;
			ProcessName = executable;
		}

		/// <inheritdoc />
		public virtual void Configure(ProcessStartInfo psi, IOutputProcessor<T, List<T>> processor = null)
		{
			psi.EnsureNotNull(nameof(psi));

			OutputProcessor = processor ?? OutputProcessor;

			ConfigureOutputProcessor();

			Process = new Process { StartInfo = psi, EnableRaisingEvents = true };
			ProcessName = psi.FileName;
			OutputProcessor.OnEntry += s => OnOutput?.Invoke(s);
		}

		/// <inheritdoc />
		void IProcessTask.Configure(ProcessStartInfo psi) => Configure(psi, null);

		IProcessTask IProcessTask.Start()
		{
			Start();
			return this;
		}

		/// <inheritdoc />
		public new IProcessTask<T, List<T>> Start()
		{
			base.Start();
			return this;
		}

		/// <inheritdoc />
		public void Stop()
		{
			wrapper?.Stop();
		}

		/// <inheritdoc />
		public virtual void Detach()
		{
			wrapper?.Detach();
		}

		/// <inheritdoc />
		public override string ToString()
		{
			return $"{Task?.Id ?? -1} {Name} {GetType()} {ProcessName} {ProcessArguments}";
		}

		/// <summary>
		/// Called when the process has been started.
		/// </summary>
		protected virtual void RaiseOnStartProcess()
		{
			OnStartProcess?.Invoke(this);
			OnStartProcess = null;
		}

		/// <summary>
		/// Call after OnEnd, when the process has finished.
		/// </summary>
		protected virtual void RaiseOnEndProcess()
		{
			OnEndProcess?.Invoke(this);
			OnEndProcess = null;
		}

		/// <inheritdoc />
		protected virtual void ConfigureOutputProcessor()
		{
			if (OutputProcessor == null && (typeof(T) != typeof(string)))
			{
				throw new InvalidOperationException("ProcessTask without an output processor must be defined as IProcessTask<string>");
			}
			OutputProcessor.OnEntry += x => RaiseOnData(x);
		}

		/// <inheritdoc />
		protected virtual BaseProcessWrapper GetWrapper(string taskName,
			Process process,
			IOutputProcessor<T, List<T>> outputProcessor,
			bool longRunning,
			Action onStart,
			Action onEnd,
			Action<Exception, string> onError,
			CancellationToken token)
		{
			return new ProcessWrapper(taskName, process, outputProcessor,
				longRunning, onStart, onEnd, onError, token);
		}

		/// <inheritdoc />
		protected override List<T> RunWithReturn(bool success)
		{
			var result = base.RunWithReturn(success);

			try
			{
				wrapper = GetWrapper(Name, Process, OutputProcessor, LongRunning,
					onStart: RaiseOnStartProcess,
					onEnd: () => {
						try
						{
							if (OutputProcessor != null)
								result = OutputProcessor.Result;
							if (result == null)
								result = new List<T>();

							if (!String.IsNullOrEmpty(Errors))
								OnErrorData?.Invoke(Errors);
						}
						catch (Exception ex)
						{
							if (thrownException == null)
								thrownException = new ProcessException(ex.Message, ex);
							else
								thrownException = new ProcessException(thrownException.GetExceptionMessage(), ex);
						}

						if (thrownException != null && !RaiseFaultHandlers(thrownException))
						{
							RaiseOnEndProcess();
							Exception.Rethrow();
						}
						RaiseOnEndProcess();
					},
					onError: (ex, error) => {
						thrownException = ex;
						Errors = error;
					},
					token: Token);

			}
			catch (Exception ex)
			{
				if (!RaiseFaultHandlers(ex))
				{
					Exception.Rethrow();
				}
			}

			wrapper.Run();

			return result;
		}

		private bool disposed;
		/// <inheritdoc />
		protected virtual void Dispose(bool disposing)
		{
			if (disposed) return;
			if (disposing)
			{
				wrapper?.Dispose();
				disposed = true;
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}


		/// <inheritdoc />
		public IProcessEnvironment ProcessEnvironment { get; private set; }
		/// <inheritdoc />
		public Process Process { get; set; }
		/// <inheritdoc />
		public int ProcessId => wrapper.ProcessId;
		/// <inheritdoc />
		public override bool Successful => base.Successful && wrapper.ExitCode == 0;
		/// <inheritdoc />
		public StreamWriter StandardInput => wrapper?.Input;
		/// <inheritdoc />
		public virtual string ProcessName { get; protected set; }
		/// <inheritdoc />
		public virtual string ProcessArguments { get; }
		/// <inheritdoc />
		public bool LongRunning { get; set; }
		/// <inheritdoc />
		protected IOutputProcessor<T, List<T>> OutputProcessor { get; private set; }
	}
}
