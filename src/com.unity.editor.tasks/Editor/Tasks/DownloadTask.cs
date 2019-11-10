﻿// Copyright 2016-2019 Andreia Gaita
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

using System;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Threading;

namespace Unity.Editor.Tasks
{
	using Logging;
	using Unity.Editor.Tasks.Helpers;

	public class DownloadTask : TaskBase<string>
	{
		public DownloadTask(ITaskManager taskManager, UriString url, string targetDirectory, string filename = null, int retryCount = 0)
			 : this(taskManager, taskManager?.Token ?? default, url, targetDirectory, filename, retryCount)
		{ }

		public DownloadTask(
			ITaskManager taskManager,
			CancellationToken token,
			UriString url,
			string targetDirectory,
			string filename = null,
			int retryCount = 0)
			: base(taskManager, token)
		{
			RetryCount = retryCount;
			Url = url;
			Filename = string.IsNullOrEmpty(filename) ? url.Filename : filename;
			TargetDirectory = targetDirectory;
			Name = $"Download {Url}";
			Message = Filename;
		}

		public override string ToString()
		{
			return $"{base.ToString()} {Url}";
		}

		protected string BaseRunWithReturn(bool success)
		{
			return base.RunWithReturn(success);
		}

		protected override string RunWithReturn(bool success)
		{
			var result = base.RunWithReturn(success);
			try
			{
				result = RunDownload(success);
			}
			catch (Exception ex)
			{
				if (!RaiseFaultHandlers(ex))
					ThrownException.Rethrow();
			}
			return result;
		}

		/// <summary>
		/// The actual functionality to download with optional hash verification
		/// subclasses that wish to return the contents of the downloaded file
		/// or do something else with it can override this instead of RunWithReturn.
		/// </summary>
		/// <param name="success"></param>
		/// <returns></returns>
		protected virtual string RunDownload(bool success)
		{
			Exception exception = null;
			var attempts = 0;
			bool result = false;
			var partialFile = Path.Combine(TargetDirectory, Filename + ".partial");
			Directory.CreateDirectory(TargetDirectory);
			do
			{
				exception = null;

				if (Token.IsCancellationRequested)
					break;

				try
				{
					Logger.Trace($"Download of {Url} to {Destination} Attempt {attempts + 1} of {RetryCount + 1}");

					using (var destinationStream = File.OpenWrite(partialFile))
					{
						result = Downloader.Download(Logger, Url, destinationStream,
							 (value, total) => {
								 UpdateProgress(value, total);
								 return !Token.IsCancellationRequested;
							 });
					}

					if (result)
					{
						File.Move(partialFile, Destination);
					}
				}
				catch (Exception ex)
				{
					exception = ex;
					result = false;
				}
			} while (!result && attempts++ < RetryCount);

			if (!result)
			{
				Token.ThrowIfCancellationRequested();
				throw new DownloadException("Error downloading file", exception);
			}

			return Destination;
		}

		public UriString Url { get; }

		public string TargetDirectory { get; }

		public string Filename { get; }

		public string Destination => Path.Combine(TargetDirectory, Filename);

		protected int RetryCount { get; }
	}

	class DownloadException : Exception
	{
		public DownloadException(string message) : base(message)
		{ }

		public DownloadException(string message, Exception innerException) : base(message, innerException)
		{ }
	}

	public static class WebRequestExtensions
	{
		public static WebResponse GetResponseWithoutException(this WebRequest request)
		{
			try
			{
				return request.GetResponse();
			}
			catch (WebException e)
			{
				if (e.Response != null)
				{
					return e.Response;
				}

				throw;
			}
		}
	}

	public class DownloadData
	{
		public DownloadData(UriString url, string file)
		{
			Url = url;
			File = file;
		}

		public UriString Url { get; }
		public string File { get; }
	}

	public class Downloader : TaskQueue<string, DownloadData>
	{
		public event Action<UriString, string> OnDownloadComplete;
		public event Action<UriString, Exception> OnDownloadFailed;
		public event Action<UriString> OnDownloadStart;

		public Downloader(ITaskManager taskManager)
			: this(taskManager, taskManager?.Token ?? default)
		{
		}

		public Downloader(ITaskManager taskManager, CancellationToken token)
			 : base(taskManager, t => {
				 var dt = t as DownloadTask;
				 var destinationFile = Path.Combine(dt.TargetDirectory, dt.Url.Filename);
				 return new DownloadData(dt.Url, destinationFile);
			 })
		{
			Name = "Downloader";
			Message = "Downloading...";
		}

		public static bool Download(ILogging logger,
			UriString url,
			Stream destinationStream,
			Func<long, long, bool> onProgress)
		{
			long bytes = destinationStream.Length;

			var expectingResume = bytes > 0;

#if !NET_35
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
#endif
			var webRequest = (HttpWebRequest)WebRequest.Create(url);

			if (expectingResume)
			{
#if NET_35
				// classlib for 3.5 doesn't take long overloads...
				webRequest.AddRange((int)bytes);
#else
				webRequest.AddRange(bytes);
#endif
			}

			webRequest.Method = "GET";
			webRequest.Accept = "*/*";
			webRequest.UserAgent = "gfu/2.0";
			webRequest.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.BypassCache);
			webRequest.ServicePoint.ConnectionLimit = 10;
			webRequest.AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip;
			webRequest.AllowAutoRedirect = true;

			if (expectingResume)
				logger.Trace($"Resuming download of {url}");
			else
				logger.Trace($"Downloading {url}");

			if (!onProgress(bytes, bytes * 2))
				return false;

			using (var webResponse = (HttpWebResponse)webRequest.GetResponseWithoutException())
			{
				var httpStatusCode = webResponse.StatusCode;
				logger.Trace($"Downloading {url} StatusCode:{(int)webResponse.StatusCode}");

				if (expectingResume && httpStatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
				{
					return !onProgress(bytes, bytes);
				}

				if (!(httpStatusCode == HttpStatusCode.OK || httpStatusCode == HttpStatusCode.PartialContent))
				{
					return false;
				}

				if (expectingResume && httpStatusCode == HttpStatusCode.OK)
				{
					expectingResume = false;
					destinationStream.Seek(0, SeekOrigin.Begin);
				}

				var responseLength = webResponse.ContentLength;
				responseLength = responseLength > 0 ? webResponse.ContentLength : 0;
				if (expectingResume)
				{
					if (!onProgress(bytes, bytes + responseLength))
						return false;
				}

				using (var responseStream = webResponse.GetResponseStream())
				{
					return Utils.Copy(responseStream, destinationStream, responseLength,
						 progress: (totalRead, timeToFinish) => {
							 return onProgress(totalRead, responseLength);
						 });
				}
			}
		}

		public void QueueDownload(UriString url, string targetDirectory, string filename = null, int retryCount = 0)
		{
			var download = new DownloadTask(TaskManager, url, targetDirectory, filename, retryCount);
			download.OnStart += t => OnDownloadStart?.Invoke(((DownloadTask)t).Url);
			download.OnEnd += (t, res, s, ex) => {
				if (s)
					OnDownloadComplete?.Invoke(((DownloadTask)t).Url, res);
				else
					OnDownloadFailed?.Invoke(((DownloadTask)t).Url, ex);
			};
			// queue after hooking up events so OnDownload* gets called first
			Queue(download);
		}
	}
}