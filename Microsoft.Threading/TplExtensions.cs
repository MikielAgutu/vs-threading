﻿//-----------------------------------------------------------------------
// <copyright file="TplExtensions.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// Extensions to the Task Parallel Library.
	/// </summary>
	public static class TplExtensions {
		/// <summary>
		/// A singleton completed task.
		/// </summary>
		public static readonly Task CompletedTask = Task.FromResult<object>(null);

		/// <summary>
		/// A task that is already canceled.
		/// </summary>
		public static readonly Task CanceledTask = CreateCanceledTask();

		/// <summary>
		/// A completed task with a <c>true</c> result.
		/// </summary>
		public static readonly Task<bool> TrueTask = Task.FromResult(true);

		/// <summary>
		/// A completed task with a <c>false</c> result.
		/// </summary>
		public static readonly Task<bool> FalseTask = Task.FromResult(false);

		/// <summary>
		/// Wait on a task without possibly inlining it to the current thread.
		/// </summary>
		/// <param name="task">The task to wait on.</param>
		public static void WaitWithoutInlining(this Task task) {
			Requires.NotNull(task, "task");
			if (!task.IsCompleted) {
				// Waiting on a continuation of a task won't ever inline the predecessor (in .NET 4.x anyway).
				var continuation = task.ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
				continuation.Wait();
			}

			task.Wait(); // purely for exception behavior; alternatively in .NET 4.5 task.GetAwaiter().GetResult();
		}

		/// <summary>
		/// Applies one task's results to another.
		/// </summary>
		/// <typeparam name="T">The type of value returned by a task.</typeparam>
		/// <param name="task">The task whose completion should be applied to another.</param>
		/// <param name="tcs">The task that should receive the completion status.</param>
		public static void ApplyResultTo<T>(this Task<T> task, TaskCompletionSource<T> tcs) {
			Requires.NotNull(task, "task");
			Requires.NotNull(tcs, "tcs");

			if (task.IsCompleted) {
				ApplyCompletedTaskResultTo(task, tcs);
			} else {
				// Using a minimum of allocations (just one task, and no closure) ensure that one task's completion sets equivalent completion on another task.
				task.ContinueWith(
					(t, s) => ApplyCompletedTaskResultTo(t, (TaskCompletionSource<T>)s),
					tcs,
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
			}
		}

		/// <summary>
		/// Creates a task that is attached to the parent task, but produces the same result as an existing task.
		/// </summary>
		/// <typeparam name="T">The type of value produced by the task.</typeparam>
		/// <param name="task">The task to wrap with an AttachedToParent task.</param>
		/// <returns>A task that is attached to parent.</returns>
		public static Task<T> AttachToParent<T>(this Task<T> task) {
			Requires.NotNull(task, "task");

			var tcs = new TaskCompletionSource<T>(TaskCreationOptions.AttachedToParent);
			task.ApplyResultTo(tcs);
			return tcs.Task;
		}

		/// <summary>
		/// Creates a task that is attached to the parent task, but produces the same result as an existing task.
		/// </summary>
		/// <param name="task">The task to wrap with an AttachedToParent task.</param>
		/// <returns>A task that is attached to parent.</returns>
		public static Task AttachToParent(this Task task) {
			Requires.NotNull(task, "task");

			var tcs = new TaskCompletionSource<EmptyStruct>(TaskCreationOptions.AttachedToParent);
			task.ContinueWith(
				(t, s) => {
					var _tcs = (TaskCompletionSource<EmptyStruct>)s;
					if (t.IsCanceled) {
						_tcs.TrySetCanceled();
					} else if (t.IsFaulted) {
						_tcs.TrySetException(t.Exception);
					} else {
						_tcs.TrySetResult(EmptyStruct.Instance);
					}
				},
				tcs,
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
			return tcs.Task;
		}

		/// <summary>
		/// Schedules some action for execution at the conclusion of a task.
		/// </summary>
		/// <returns>The task that will execute the action.</returns>
		public static Task AppendAction(this Task task, Action action, TaskContinuationOptions options = TaskContinuationOptions.None, CancellationToken cancellation = default(CancellationToken)) {
			Requires.NotNull(task, "task");
			Requires.NotNull(action, "action");

			return task.ContinueWith((t, state) => ((Action)state)(), action, cancellation, options, TaskScheduler.Default);
		}

		/// <summary>
		/// Gets a task that will eventually produce the result of another task, when that task finishes.
		/// If that task is instead canceled, its successor will be followed for its result, iteratively.
		/// </summary>
		/// <typeparam name="T">The type of value returned by the task.</typeparam>
		/// <param name="taskToFollow">The task whose result should be returned by the following task.</param>
		/// <param name="ultimateCancellation">A token whose cancellation signals that the following task should be cancelled.</param>
		/// <param name="taskThatFollows">The TaskCompletionSource whose task is to follow.  Leave at <c>null</c> for a new task to be created.</param>
		/// <returns>The following task.</returns>
		public static Task<T> FollowCancelableTaskToCompletion<T>(Func<Task<T>> taskToFollow, CancellationToken ultimateCancellation, TaskCompletionSource<T> taskThatFollows = null) {
			Requires.NotNull(taskToFollow, "taskToFollow");

			var snappedTask = taskToFollow();
			var tcs = taskThatFollows ?? new TaskCompletionSource<T>();
			snappedTask.ContinueWith(
				_ => tcs.SetResult(snappedTask.Result),
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnRanToCompletion,
				TaskScheduler.Default);
			snappedTask.ContinueWith(
				_ => tcs.SetException(snappedTask.Exception),
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted,
				TaskScheduler.Default);
			snappedTask.ContinueWith(
				canceledTask => {
					if (ultimateCancellation.IsCancellationRequested) {
						tcs.SetCanceled();
					} else {
						Assumes.True(taskToFollow() != canceledTask, "A canceled task was not replaced with a new task.");
						FollowCancelableTaskToCompletion(taskToFollow, ultimateCancellation, tcs);
					}
				},
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnCanceled,
				TaskScheduler.Default);

			return tcs.Task;
		}

		/// <summary>
		/// Returns an awaitable for the specified task that will never throw, even if the source task
		/// faults or is canceled.
		/// </summary>
		public static Task NoThrowAwaitable(this Task task) {
			return task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}

		/// <summary>
		/// Consumes a task and doesn't do anything with it.  Useful for fire-and-forget calls to async methods within async methods.
		/// </summary>
		public static void Forget(this Task taks) {
		}

		/// <summary>
		/// Invokes asynchronous event handlers, returning a task that completes when all event handlers have been invoked.
		/// Each handler is fully executed (including continuations) before the next handler in the list is invoked.
		/// </summary>
		/// <param name="handlers">The event handlers.  May be <c>null</c></param>
		/// <param name="sender">The event source.</param>
		/// <param name="args">The event argument.</param>
		/// <returns>The task that completes when all handlers have completed.</returns>
		/// <exception cref="AggregateException">Thrown if any handlers fail. It contains a collection of all failures.</exception>
		public static async Task InvokeAsync(this AsyncEventHandler handlers, object sender, EventArgs args) {
			if (handlers != null) {
				var individualHandlers = handlers.GetInvocationList();
				List<Exception> exceptions = null;
				foreach (AsyncEventHandler handler in individualHandlers) {
					try {
						await handler(sender, args);
					} catch (Exception ex) {
						if (exceptions == null) {
							exceptions = new List<Exception>(2);
						}

						exceptions.Add(ex);
					}
				}

				if (exceptions != null) {
					throw new AggregateException(exceptions);
				}
			}
		}

		/// <summary>
		/// Invokes asynchronous event handlers, returning a task that completes when all event handlers have been invoked.
		/// Each handler is fully executed (including continuations) before the next handler in the list is invoked.
		/// </summary>
		/// <typeparam name="TEventArgs">The type of argument passed to each handler.</typeparam>
		/// <param name="handlers">The event handlers.  May be <c>null</c></param>
		/// <param name="sender">The event source.</param>
		/// <param name="args">The event argument.</param>
		/// <returns>The task that completes when all handlers have completed.  The task is faulted if any handlers throw an exception.</returns>
		/// <exception cref="AggregateException">Thrown if any handlers fail. It contains a collection of all failures.</exception>
		public static async Task InvokeAsync<TEventArgs>(this AsyncEventHandler<TEventArgs> handlers, object sender, TEventArgs args)
			where TEventArgs : EventArgs {
			if (handlers != null) {
				var individualHandlers = handlers.GetInvocationList();
				List<Exception> exceptions = null;
				foreach (AsyncEventHandler<TEventArgs> handler in individualHandlers) {
					try {
						await handler(sender, args);
					} catch (Exception ex) {
						if (exceptions == null) {
							exceptions = new List<Exception>(2);
						}

						exceptions.Add(ex);
					}
				}

				if (exceptions != null) {
					throw new AggregateException(exceptions);
				}
			}
		}

		/// <summary>
		/// Creates a canceled task.
		/// </summary>
		private static Task CreateCanceledTask() {
			var tcs = new TaskCompletionSource<EmptyStruct>();
			tcs.SetCanceled();
			return tcs.Task;
		}

		/// <summary>
		/// Applies a completed task's results to another.
		/// </summary>
		/// <typeparam name="T">The type of value returned by a task.</typeparam>
		/// <param name="task">The task whose completion should be applied to another.</param>
		/// <param name="tcs">The task that should receive the completion status.</param>
		private static void ApplyCompletedTaskResultTo<T>(Task<T> completedTask, TaskCompletionSource<T> taskCompletionSource) {
			Assumes.NotNull(completedTask);
			Assumes.True(completedTask.IsCompleted);
			Assumes.NotNull(taskCompletionSource);

			if (completedTask.IsCanceled) {
				taskCompletionSource.TrySetCanceled();
			} else if (completedTask.IsFaulted) {
				taskCompletionSource.TrySetException(completedTask.Exception.InnerExceptions);
			} else {
				taskCompletionSource.TrySetResult(completedTask.Result);
			}
		}
	}
}
