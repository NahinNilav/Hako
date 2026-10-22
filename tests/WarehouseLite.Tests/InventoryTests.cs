using WarehouseLite.Core.Warehouse;
using Xunit;

namespace WarehouseLite.Tests;

public class InventoryTests
{
    private static (WarehouseService Service, string Sku, string Bin) CreateWarehouse()
    {
        var path = Path.Combine(Path.GetTempPath(), "hako-tests", Guid.NewGuid().ToString("N"), "warehouse.db");
        var warehouse = new WarehouseService(path);
        warehouse.Initialize();
        warehouse.ResetDemo(Path.Combine(AppContext.BaseDirectory, "seed.json"));
        var row = warehouse.Inventory().First(r => r["sku"].ToString() == "SKU-1001");
        return (warehouse, "SKU-1001", row["location"].ToString()!);
    }
    private static long Stock(WarehouseService s, string sku) =>
        Convert.ToInt64(s.Products().Single(p => (string)p["sku"] == sku)["stock"]);
    private static long StockInBin(WarehouseService s, string sku, string bin) =>
        Convert.ToInt64(s.Inventory().Where(r => (string)r["sku"] == sku && (string)r["location"] == bin)
            .Select(r => r["quantity"]).FirstOrDefault() ?? 0L);

    [Fact]
    public void SeedHasCatalogAndLocations()
    {
        var (service, _, _) = CreateWarehouse();
        Assert.Equal(46, service.Products().Count);
        Assert.Equal(8, service.Locations().Count);
        Assert.True(service.Movements().Count > 90);
    }
    [Fact]
    public void ReceiveAddsStockAndHistory()
    {
        var (service, sku, bin) = CreateWarehouse();
        var before = Stock(service, sku);
        var count = service.Movements().Count;
        service.Receive(sku, bin, 15, "PO-TEST-1");
        Assert.Equal(before + 15, Stock(service, sku));
        Assert.Equal(count + 1, service.Movements().Count);
        Assert.Equal("PO-TEST-1", (string)service.Movements()[0]["reference"]);
    }
    [Fact]
    public void RejectsOverDispatchWithoutModifyingStockOrAudit()
    {
        var (service, sku, bin) = CreateWarehouse();
        var before = Stock(service, sku);
        var count = service.Movements().Count;
        Assert.Throws<WarehouseException>(() => service.Dispatch(sku, bin, 100000, "SO-INVALID"));
        Assert.Equal(before, Stock(service, sku));
        Assert.Equal(count, service.Movements().Count);
    }
    [Fact]
    public void TransferIsAtomicAndPreservesTotalUnits()
    {
        var (service, sku, bin) = CreateWarehouse();
        var target = service.Locations().Select(l => (string)l["code"]).First(code => code != bin);
        var before = Stock(service, sku);
        var sourceBefore = StockInBin(service, sku, bin);
        var targetBefore = StockInBin(service, sku, target);
        service.Transfer(sku, bin, target, 4, "TR-TEST-1");
        Assert.Equal(before, Stock(service, sku));
        Assert.Equal(sourceBefore - 4, StockInBin(service, sku, bin));
        Assert.Equal(targetBefore + 4, StockInBin(service, sku, target));
    }
    [Fact]
    public void RejectedTransferDoesNotModifyLocationBalances()
    {
        var (service, sku, bin) = CreateWarehouse();
        var target = service.Locations().Select(l => (string)l["code"]).First(code => code != bin);
        var source = StockInBin(service, sku, bin);
        var dest = StockInBin(service, sku, target);
        Assert.Throws<WarehouseException>(() => service.Transfer(sku, bin, target, 99999, "TR-INVALID"));
        Assert.Equal(source, StockInBin(service, sku, bin));
        Assert.Equal(dest, StockInBin(service, sku, target));
    }
    [Fact]
    public void RejectsZeroQuantityAndSameLocationTransfer()
    {
        var (service, sku, bin) = CreateWarehouse();
        Assert.Throws<WarehouseException>(() => service.Receive(sku, bin, 0, "PO-ZERO"));
        Assert.Throws<WarehouseException>(() => service.Transfer(sku, bin, bin, 2, "TR-SAME"));
    }
    [Fact]
    public void RejectsOverCapacityReceive()
    {
        var (service, sku, bin) = CreateWarehouse();
        Assert.Throws<WarehouseException>(() => service.Receive(sku, bin, 2000, "PO-CAPACITY"));
    }
    [Fact]
    public void DuplicateSkusAreRejected()
    {
        var (service, sku, _) = CreateWarehouse();
        Assert.Throws<WarehouseException>(() => service.CreateProduct(sku, "Duplicate", "Packaging", 0, 1));
    }
}
