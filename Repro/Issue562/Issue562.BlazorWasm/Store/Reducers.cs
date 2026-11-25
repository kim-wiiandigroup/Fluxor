using Fluxor;
using Issue562.BlazorWasm.Services;

namespace Issue562.BlazorWasm.Store;

public static class Reducers
{
	[ReducerMethod]
	public static MainState OnStoreInitialized(MainState state, StoreInitializedAction _) =>
		state with { IsStoreInitialized = true };

	[ReducerMethod]
	public static MainState OnFetch(MainState state, FetchCustomPropertiesAction _) =>
		state with { IsLoading = true, ErrorMessage = null };

	[ReducerMethod(typeof(FetchCustomPropertiesAndMarkFinishedAction))]
	public static MainState OnFetchAndMarkFinished(MainState state) =>
		state with { IsLoading = true, ErrorMessage = null };

	/// <summary>
	/// Indexes the property types by name. A property whose Name is null makes
	/// Dictionary.ContainsKey throw ArgumentNullException, so with bad API data this
	/// reducer throws while the Dispatcher is dequeuing actions.
	/// </summary>
	[ReducerMethod]
	public static MainState OnSetAll(MainState state, SetAllCustomPropertiesAction action)
	{
		var index = new Dictionary<string, PropertyType>();
		foreach (PropertyType p in action.PropertyTypes)
		{
			if (!index.ContainsKey(p.Name!)) // throws for bad API data (Name == null)
				index.Add(p.Name!, p);
		}
		return state with { CustomPropertyTypes = index, IsLoading = false };
	}

	[ReducerMethod]
	public static MainState OnStateFailure(MainState state, StateFailureAction action) =>
		state with
		{
			IsLoading = false,
			ErrorMessage = $"{action.Exception.GetType().FullName}: {action.Exception.Message}"
		};

	[ReducerMethod(typeof(CustomPropertiesStatusUpdatedAction))]
	public static MainState OnStatusUpdated(MainState state) =>
		state with { StatusUpdates = state.StatusUpdates + 1, IsLoading = false };

	[ReducerMethod(typeof(IncrementAction))]
	public static MainState OnIncrement(MainState state) =>
		state with { Counter = state.Counter + 1 };
}
