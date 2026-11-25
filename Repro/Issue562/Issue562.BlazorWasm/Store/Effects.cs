using Fluxor;
using Issue562.BlazorWasm.Services;

namespace Issue562.BlazorWasm.Store;

public class Effects(ICustomPropertyService service)
{
	/// <summary>
	/// Scenario A (issue #562): the usual "fetch, dispatch the result, report failures" effect.
	/// After the await nobody is dequeuing, so Dispatch(SetAllCustomPropertiesAction) runs the
	/// reducer synchronously and the reducer's ArgumentNullException comes out of Dispatch into
	/// the catch below.
	///
	/// Fluxor 6.9.0: that exception leaves the Dispatcher's IsDequeuing flag set, so the
	/// StateFailureAction below (and every later action) is only queued and never processed.
	/// Nothing is logged, IsLoading stays true and the app stops reacting to any action.
	/// Fluxor 6.11.0 and later: the flag is released and StateFailureAction is processed.
	/// </summary>
	[EffectMethod]
	public async Task HandleFetch(FetchCustomPropertiesAction action, IDispatcher dispatcher)
	{
		try
		{
			IReadOnlyList<PropertyType> propertyTypes = await service.GetCustomPropertyTypesAsync(action.BadData);
			dispatcher.Dispatch(new SetAllCustomPropertiesAction(propertyTypes));
		}
		catch (Exception ex)
		{
			dispatcher.Dispatch(new StateFailureAction(ex, action));
		}
	}

	/// <summary>
	/// Central error reporting, as many apps do it: a failure is logged when its StateFailureAction
	/// is processed. If that action is never processed (6.9.0), nothing is logged at all.
	/// (Blazor WebAssembly also shows its "An unhandled error has occurred" bar for anything written
	/// to stderr, so this log line makes that bar appear too.)
	/// </summary>
	[EffectMethod]
	public Task HandleStateFailure(StateFailureAction action, IDispatcher dispatcher)
	{
		Console.Error.WriteLine($"StateFailureAction for {action.OriginalAction.GetType().Name}: {action.Exception}");
		return Task.CompletedTask;
	}

	/// <summary>
	/// Scenario B (the gap closed by PR #584). Both actions are dispatched synchronously while the
	/// Dispatcher is still dequeuing FetchCustomPropertiesAndMarkFinishedAction, so both are queued
	/// behind it. When the SetAllCustomPropertiesAction reducer throws:
	///   6.9.0   - the Dispatcher stays locked; the status action and every later action are stranded.
	///   6.11.0  - the lock is released, but the status action stays queued until the next Dispatch.
	///   PR #584 - the queue is drained first (the status action is processed), then the exception
	///             is rethrown.
	/// In all versions the exception comes out of the Dispatch call made by the button's click handler.
	/// </summary>
	[EffectMethod(typeof(FetchCustomPropertiesAndMarkFinishedAction))]
	public Task HandleFetchAndMarkFinished(IDispatcher dispatcher)
	{
		dispatcher.Dispatch(new SetAllCustomPropertiesAction(FakeCustomPropertyService.BadData));
		dispatcher.Dispatch(new CustomPropertiesStatusUpdatedAction());
		return Task.CompletedTask;
	}
}
