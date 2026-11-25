using Issue562.BlazorWasm.Services;

namespace Issue562.BlazorWasm.Store;

// Scenario A: fetch, then store the result (or report the failure)
public record FetchCustomPropertiesAction(bool BadData);
public record SetAllCustomPropertiesAction(IReadOnlyList<PropertyType> PropertyTypes);
public record StateFailureAction(Exception Exception, object OriginalAction);

// Scenario B: two actions dispatched back to back from inside an effect
public record FetchCustomPropertiesAndMarkFinishedAction;
public record CustomPropertiesStatusUpdatedAction;

// An unrelated action, used to see whether the dispatcher still processes anything
public record IncrementAction;
