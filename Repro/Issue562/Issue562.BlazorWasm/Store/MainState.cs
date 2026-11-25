using Fluxor;
using Issue562.BlazorWasm.Services;

namespace Issue562.BlazorWasm.Store;

[FeatureState]
public record MainState
{
	public IReadOnlyDictionary<string, PropertyType> CustomPropertyTypes { get; init; } =
		new Dictionary<string, PropertyType>();
	public bool IsLoading { get; init; }
	public string? ErrorMessage { get; init; }
	public int Counter { get; init; }
	public int StatusUpdates { get; init; }

	/// <summary>
	/// Set when Fluxor's StoreInitializedAction has been reduced, so the UI (and the test)
	/// know the store is live before any button is clicked.
	/// </summary>
	public bool IsStoreInitialized { get; init; }
}
