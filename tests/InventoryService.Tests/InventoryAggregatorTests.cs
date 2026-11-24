using InventoryService.Api.Aggregation;
using static InventoryService.Tests.TestDoubles.TestData;

namespace InventoryService.Tests;

public class InventoryAggregatorTests
{
    private readonly InventoryAggregator _aggregator = new();

    [Fact]
    public void Empty_input_returns_no_categories()
    {
        Assert.Empty(_aggregator.Aggregate([]));
    }

    [Fact]
    public void Null_input_throws()
    {
        Assert.Throws<ArgumentNullException>(() => _aggregator.Aggregate(null!));
    }

    [Fact]
    public void Excludes_records_with_zero_quantity()
    {
        var result = _aggregator.Aggregate([
            Record(1, quantity: 2, price: 5m),
            Record(2, quantity: 0, price: 100m),
        ]);

        var category = Assert.Single(result);
        Assert.Equal(1, category.AvailableRecordCount);
        Assert.Equal(10m, category.TotalInventoryValue);
        Assert.DoesNotContain(category.Items, r => r.Id == 2);
    }

    [Fact]
    public void Excludes_records_with_negative_quantity()
    {
        var result = _aggregator.Aggregate([
            Record(1, quantity: 1),
            Record(2, quantity: -3),
        ]);

        Assert.Equal(new[] { 1 }, Assert.Single(result).Items.Select(r => r.Id));
    }

    [Fact]
    public void Omits_a_category_when_none_of_its_records_have_stock()
    {
        var result = _aggregator.Aggregate([
            Record(1, category: "Toys", quantity: 0),
            Record(2, category: "Outdoor", quantity: 4),
        ]);

        Assert.Equal("Outdoor", Assert.Single(result).Category);
    }

    [Fact]
    public void Total_inventory_value_is_the_sum_of_price_times_quantity()
    {
        var result = _aggregator.Aggregate([
            Record(1, price: 149.99m, quantity: 12),
            Record(2, price: 10.50m, quantity: 3),
        ]);

        Assert.Equal(1831.38m, Assert.Single(result).TotalInventoryValue);
    }

    [Fact]
    public void Groups_category_names_ignoring_case_and_whitespace()
    {
        var result = _aggregator.Aggregate([
            Record(1, category: "Toys"),
            Record(2, category: "TOYS"),
            Record(3, category: "  toys "),
        ]);

        Assert.Equal(3, Assert.Single(result).AvailableRecordCount);
    }

    [Fact]
    public void Category_name_is_the_most_common_spelling()
    {
        var result = _aggregator.Aggregate([
            Record(1, category: "TOYS"),
            Record(2, category: "Toys"),
            Record(3, category: " Toys "),
        ]);

        Assert.Equal("Toys", Assert.Single(result).Category);
    }

    [Fact]
    public void Category_name_tie_is_broken_by_ordinal_order()
    {
        // "Toys" sorts before "toys" by character code (capital letters come first).
        var forwards = _aggregator.Aggregate([Record(1, category: "toys"), Record(2, category: "Toys")]);
        var backwards = _aggregator.Aggregate([Record(2, category: "Toys"), Record(1, category: "toys")]);

        Assert.Equal("Toys", Assert.Single(forwards).Category);
        Assert.Equal("Toys", Assert.Single(backwards).Category);
    }

    [Fact]
    public void Blank_categories_are_grouped_as_uncategorized()
    {
        var result = _aggregator.Aggregate([
            Record(1, category: ""),
            Record(2, category: "   "),
        ]);

        var category = Assert.Single(result);
        Assert.Equal(InventoryAggregator.UncategorizedLabel, category.Category);
        Assert.Equal(2, category.AvailableRecordCount);
    }

    [Fact]
    public void Most_recently_updated_is_the_record_with_the_latest_timestamp()
    {
        var result = _aggregator.Aggregate([
            Record(1, lastUpdated: "2026-09-01T00:00:00Z"),
            Record(2, lastUpdated: "2026-10-15T12:30:00Z"),
            Record(3, lastUpdated: "2026-10-01T00:00:00Z"),
        ]);

        var newest = Assert.Single(result).MostRecentlyUpdated;
        Assert.Equal(2, newest.Id);
        Assert.Equal("Item 2", newest.Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero), newest.LastUpdated);
    }

    [Fact]
    public void Most_recently_updated_tie_goes_to_the_lower_id()
    {
        var result = _aggregator.Aggregate([
            Record(5, lastUpdated: "2026-10-15T12:30:00Z"),
            Record(3, lastUpdated: "2026-10-15T12:30:00Z"),
        ]);

        Assert.Equal(3, Assert.Single(result).MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Items_are_the_original_records_sorted_by_id()
    {
        var result = _aggregator.Aggregate([
            Record(3, category: "Toys"),
            Record(1, category: "  toys "),
            Record(2, category: "Toys"),
        ]);

        var items = Assert.Single(result).Items;
        Assert.Equal(new[] { 1, 2, 3 }, items.Select(r => r.Id));
        Assert.Equal("  toys ", items[0].Category); // original text is kept
    }

    [Fact]
    public void Categories_are_sorted_by_name()
    {
        var result = _aggregator.Aggregate([
            Record(1, category: "Toys"),
            Record(2, category: "Electronics"),
            Record(3, category: "outdoor"),
        ]);

        Assert.Equal(new[] { "Electronics", "outdoor", "Toys" }, result.Select(c => c.Category));
    }
}
