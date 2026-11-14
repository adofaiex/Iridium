using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Iridium.Patches
{
	public static class AsyncPatchManager
	{
		private static Thread? _workerThread;
		private static readonly HashSet<Type> _pendingPatchTypes = new();
		private static bool _pendingOptimizerUpdate = false;
		private static bool _pendingAllUpdate = false;
		private static readonly object _queueLock = new();
		private static readonly AutoResetEvent _taskEvent = new(false);
		private static volatile bool _isRunning = false;
		private static volatile bool _isProcessing = false;
		private static bool _mainThreadTaskQueued = false;
		private static int _runGeneration;
		private static readonly Stopwatch _debounceTimer = Stopwatch.StartNew();
		private static long _lastUpdateTimeMs = 0;
		private const int DEBOUNCE_MS = 100; // 防抖延迟100毫秒

		public static bool IsProcessing => _isProcessing;

		/// <summary>启动异步 Patch 处理线程</summary>
		public static void Start()
		{
			if (_isRunning) return;

			_isRunning = true;
			_isProcessing = false;
			lock (_queueLock)
			{
				++_runGeneration;
			}
			_workerThread = new Thread(WorkerLoop)
			{
				Name = "IridiumPatchWorker",
				IsBackground = true,
				Priority = ThreadPriority.BelowNormal
			};
			_workerThread.Start();

			Main.Logger?.Log(Localization.Get("AsyncPatchWorkerStarted"));
		}

		/// <summary>停止异步 Patch 处理线程</summary>
		public static void Stop()
		{
			if (!_isRunning) return;

			_isRunning = false;
		lock (_queueLock)
		{
			++_runGeneration;
		}
			_taskEvent.Set(); // 唤醒线程以便退出

			if (_workerThread != null && _workerThread.IsAlive)
			{
				_workerThread.Join(1000); // 等待最多1秒
			}

			lock (_queueLock)
			{
				_pendingPatchTypes.Clear();
				_pendingOptimizerUpdate = false;
				_pendingAllUpdate = false;
				_mainThreadTaskQueued = false;
			}

			Main.Logger?.Log(Localization.Get("AsyncPatchWorkerStopped"));
		}

		/// <summary>异步更新单个 Patch（带防抖）</summary>
		public static void UpdatePatchByTypeAsync(Type patchType)
		{
			lock (_queueLock)
			{
				_pendingPatchTypes.Add(patchType);
				_lastUpdateTimeMs = _debounceTimer.ElapsedMilliseconds;
			}
			_taskEvent.Set();
		}

		/// <summary>异步更新所有优化器 Patch（带防抖）</summary>
		public static void UpdateOptimizerPatchesAsync()
		{
			lock (_queueLock)
			{
				_pendingOptimizerUpdate = true;
				_lastUpdateTimeMs = _debounceTimer.ElapsedMilliseconds;
			}
			_taskEvent.Set();
		}

		/// <summary>异步更新所有 Patch</summary>
		public static void UpdateAllPatchesAsync()
		{
			lock (_queueLock)
			{
				_pendingAllUpdate = true;
				_lastUpdateTimeMs = _debounceTimer.ElapsedMilliseconds;
			}
			_taskEvent.Set();
		}

		/// <summary>Rebuilds all patches when the backend mode changes.</summary>
		public static void ReapplyAllPatchesAsync()
		{
			lock (_queueLock)
			{
				_pendingAllUpdate = true;
				_lastUpdateTimeMs = _debounceTimer.ElapsedMilliseconds;
			}
			_taskEvent.Set();
		}

		private static void WorkerLoop()
		{
			while (_isRunning)
			{
				_taskEvent.WaitOne(200); // 最多等待200ms

				if (!_isRunning) break;

				bool shouldExecute = false;
				lock (_queueLock)
				{
					var elapsed = _debounceTimer.ElapsedMilliseconds - _lastUpdateTimeMs;
					if (elapsed >= DEBOUNCE_MS &&
						(_pendingAllUpdate || _pendingOptimizerUpdate || _pendingPatchTypes.Count > 0) &&
						!_mainThreadTaskQueued)
					{
						shouldExecute = true;
					}
				}

				if (!shouldExecute) continue;

				bool doAllUpdate = false;
				bool doOptimizerUpdate = false;
				List<Type> patchTypes = new();

				lock (_queueLock)
				{
					doAllUpdate = _pendingAllUpdate;
					doOptimizerUpdate = _pendingOptimizerUpdate;
					patchTypes.AddRange(_pendingPatchTypes);

					_pendingAllUpdate = false;
					_pendingOptimizerUpdate = false;
					_pendingPatchTypes.Clear();
				}

				// Harmony must run on Unity's main thread; this worker only coalesces requests.
				_isProcessing = true;
				int generation;

				lock (_queueLock)
				{
					generation = _runGeneration;
					_mainThreadTaskQueued = true;
				}

				Main.RunOnMainThread(() =>
				{
					lock (_queueLock)
					{
						_mainThreadTaskQueued = false;
					}

					// A disable may land while this action sits in the queue.
					if (!_isRunning || generation != _runGeneration)
					{
						_isProcessing = false;
						return;
					}

					try
					{
						if (doAllUpdate)
						{
							Main.Logger?.Log(Localization.Get("AsyncPatchProcessingAll"));
							PatchManager.UpdateAllPatches();
						}
						else if (doOptimizerUpdate)
						{
							Main.Logger?.Log(Localization.Get("AsyncPatchProcessingOptimizer"));
							PatchManager.UpdateOptimizerPatches();
						}
						else if (patchTypes.Count > 0)
						{
							Main.Logger?.Log(Localization.Get("AsyncPatchProcessingCount", patchTypes.Count.ToString()));
							foreach (var type in patchTypes)
							{
								PatchManager.UpdatePatchByType(type);
							}
						}

						Main.Logger?.Log(Localization.Get("AsyncPatchCompleted"));
					}
					catch (Exception ex)
					{
						Main.Logger?.Error(Localization.Get("AsyncPatchError", ex.ToString()));
					}
					finally
					{
						_isProcessing = false;
						_taskEvent.Set();
					}
				});
			}
		}

		public static int GetPendingTaskCount()
		{
			lock (_queueLock)
			{
				return _pendingPatchTypes.Count +
					   (_pendingOptimizerUpdate ? 1 : 0) +
					   (_pendingAllUpdate ? 1 : 0);
			}
		}
	}
}
