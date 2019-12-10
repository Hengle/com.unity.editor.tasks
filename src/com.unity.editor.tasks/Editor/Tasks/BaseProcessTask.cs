﻿// Copyright 2019 Unity
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

namespace Unity.Editor.Tasks
{
	using System;
	using Helpers;
	using Internal.IO;


	public abstract class BaseProcessTask : ProcessTask<string>
	{
		/// <summary>
		/// Runs a dotnet process. On Windows, it just runs the executable. On non-Windows,
		/// it runs the executable using Unity's mono.
		/// </summary>
		/// <remarks>You don't need to call <see cref="ProcessManager.Configure{T}(T, string)"/> on this task,
		/// it already does it in the constructor.
		/// </remarks>
		protected BaseProcessTask(ITaskManager taskManager, IProcessManager processManager,
			IProcessEnvironment processEnvironment,
			IEnvironment environment,
			string executable, string arguments,
			string workingDirectory,
			bool alwaysUseMono, bool neverUseMono)
			: base(taskManager, processEnvironment, outputProcessor: new SimpleOutputProcessor())
		{
			if (neverUseMono || !alwaysUseMono && environment.IsWindows)
			{
				ProcessName = executable;
				ProcessArguments = arguments;
			}
			else
			{
				ProcessArguments = executable + " " + arguments;
				ProcessName = environment.UnityApplicationContents.ToSPath()
										.Combine("MonoBleedingEdge", "bin", "mono" + environment.ExecutableExtension);
			}

			if (processManager != null)
				processManager.Configure(this, workingDirectory);
		}

	}

	/// <summary>
	/// Runs a process.
	/// </summary>
	public abstract class BaseProcessTask<T> : ProcessTask<T>
	{
		private Func<IProcessTask<T>, string, bool> isMatch;
		private readonly Func<IProcessTask<T>, string, T> processor;

		/// <summary>
		/// Runs a process.
		/// </summary>
		/// <remarks>You don't need to call <see cref="ProcessManager.Configure{T}(T, string)"/> on this task,
		/// it already does it in the constructor.
		/// </remarks>
		protected BaseProcessTask(ITaskManager taskManager, IProcessManager processManager,
			IProcessEnvironment processEnvironment, IEnvironment environment,
			string executable, string arguments, string workingDirectory,
			Func<IProcessTask<T>, string, bool> isMatch,
			Func<IProcessTask<T>, string, T> processor,
			bool alwaysUseMono, bool neverUseMono)
			: base(taskManager, processEnvironment)
		{
			this.isMatch = isMatch;
			this.processor = processor;

			if (neverUseMono || !alwaysUseMono && environment.IsWindows)
			{
				ProcessName = executable;
				ProcessArguments = arguments;
			}
			else
			{
				ProcessArguments = executable + " " + arguments;
				ProcessName = environment.UnityApplicationContents.ToSPath()
										.Combine("MonoBleedingEdge", "bin", "mono" + environment.ExecutableExtension);
			}

			if (processManager != null)
				processManager.Configure(this, workingDirectory);
		}

		/// <summary>
		/// Runs a dotnet process. On Windows, it just runs the executable. On non-Windows,
		/// it runs the executable using Unity's mono.
		/// </summary>
		/// <remarks>You don't need to call <see cref="ProcessManager.Configure{T}(T, string)"/> on this task,
		/// it already does it in the constructor.
		/// </remarks>
		protected BaseProcessTask(ITaskManager taskManager, IProcessManager processManager, IProcessEnvironment processEnvironment, IEnvironment environment,
			string executable, string arguments, string workingDirectory,
			IOutputProcessor<T> outputProcessor, bool alwaysUseMono, bool neverUseMono)
			: base(taskManager, processEnvironment, outputProcessor: outputProcessor)
		{
			if (neverUseMono || !alwaysUseMono && environment.IsWindows)
			{
				ProcessName = executable;
				ProcessArguments = arguments;
			}
			else
			{
				ProcessArguments = executable + " " + arguments;
				ProcessName = environment.UnityApplicationContents.ToSPath()
										.Combine("MonoBleedingEdge", "bin", "mono" + environment.ExecutableExtension);
			}

			if (processManager != null)
				processManager.Configure(this, workingDirectory);
		}

		protected BaseProcessTask(ITaskManager taskManager, IProcessEnvironment processEnvironment, IOutputProcessor<T> outputProcessor)
			: base(taskManager, processEnvironment, outputProcessor: outputProcessor)
		{ }

		protected override void ConfigureOutputProcessor()
		{
			if (OutputProcessor == null && processor != null)
			{
				if (isMatch == null)
					isMatch = (_, __) => true;

				OutputProcessor = new BaseOutputProcessor<T>((string line, out T result) => {
					result = default(T);
					if (!isMatch(this, line)) return false;
					result = processor(this, line);
					return true;
				});
			}

			base.ConfigureOutputProcessor();
		}
	}
}