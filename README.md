# Hako — Warehouse Management System

Hako manages warehouse inventory, receiving, dispatching and transfers between storage locations. It tracks stock levels and movement history and exports inventory and movement records as CSV files.

[Watch the recording](docs/hako-demo.mp4)

## Key features

- Inventory management with stock levels by location, low-stock alerts and CSV reporting.
- Receiving, dispatching and location transfers with stock validation and movement history.

## Run locally

Install the .NET 8 SDK, then run from the project directory:

```bash
dotnet run --project src/WarehouseLite.Web/WarehouseLite.Web.csproj
```

Open [localhost:5187](http://localhost:5187). On first launch, the system creates a local SQLite database with sample data. Changes are saved in `src/WarehouseLite.Web/data/warehouse.db`.
