namespace Issue562.BlazorWasm.Services;

/// <summary>A custom property definition as returned by the (fake) API.</summary>
/// <param name="Name">The key of the property. Bad API data can leave it null.</param>
/// <param name="Label">The display label.</param>
public record PropertyType(string? Name, string Label);

public interface ICustomPropertyService
{
	Task<IReadOnlyList<PropertyType>> GetCustomPropertyTypesAsync(bool badData);
}

/// <summary>Stands in for an HTTP API call.</summary>
public class FakeCustomPropertyService : ICustomPropertyService
{
	/// <summary>
	/// What a misbehaving API might return: one property has no name. Building a
	/// dictionary keyed by name from this throws <see cref="ArgumentNullException"/>.
	/// </summary>
	public static IReadOnlyList<PropertyType> BadData { get; } =
	[
		new PropertyType("colour", "Colour"),
		new PropertyType(null, "Property without a name"),
		new PropertyType("size", "Size"),
	];

	public static IReadOnlyList<PropertyType> GoodData { get; } =
	[
		new PropertyType("colour", "Colour"),
		new PropertyType("size", "Size"),
	];

	public async Task<IReadOnlyList<PropertyType>> GetCustomPropertyTypesAsync(bool badData)
	{
		await Task.Delay(50); // simulated network latency
		return badData ? BadData : GoodData;
	}
}
