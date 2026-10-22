using System.Text;
using WarehouseLite.Core.Warehouse;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5187");

var databasePath = Environment.GetEnvironmentVariable("WAREHOUSE_DB_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "warehouse.db");
var seedPath = Path.Combine(builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"), "assets", "seed.json");

builder.Services.AddSingleton(_ => new WarehouseService(databasePath));
var app = builder.Build();
var warehouse = app.Services.GetRequiredService<WarehouseService>();
warehouse.Initialize();
if (warehouse.CountProducts() == 0) warehouse.ResetDemo(seedPath);

app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (WarehouseException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/dashboard", () => warehouse.Dashboard());
app.MapGet("/api/products", (string? query, string? category, string? filter) => warehouse.Products(query ?? "", category ?? "", filter ?? "all"));
app.MapGet("/api/locations", () => warehouse.Locations());
app.MapGet("/api/inventory", (string? query) => warehouse.Inventory(query ?? ""));
app.MapGet("/api/movements", (int? limit) => warehouse.Movements(limit ?? 150));
app.MapPost("/api/products", (ProductCreate body) => Results.Ok(new { id = warehouse.CreateProduct(body.Sku, body.Name, body.Category, body.UnitPriceCents, body.ReorderLevel) }));
app.MapPut("/api/products/{id:long}", (long id, ProductUpdate body) =>
{
    warehouse.UpdateProduct(id, body.Name, body.Category, body.UnitPriceCents, body.ReorderLevel, body.IsActive);
    return Results.Ok(new { success = true });
});
app.MapPost("/api/movements/receive", (MoveCreate body) =>
    Results.Ok(new { id = warehouse.Receive(body.Sku, body.ToLocation ?? "", body.Quantity, body.Reference, body.Note ?? "") }));
app.MapPost("/api/movements/dispatch", (MoveCreate body) =>
    Results.Ok(new { id = warehouse.Dispatch(body.Sku, body.FromLocation ?? "", body.Quantity, body.Reference, body.Note ?? "") }));
app.MapPost("/api/movements/transfer", (MoveCreate body) =>
    Results.Ok(new { id = warehouse.Transfer(body.Sku, body.FromLocation ?? "", body.ToLocation ?? "", body.Quantity, body.Reference, body.Note ?? "") }));
app.MapPost("/api/demo/reset", () => { warehouse.ResetDemo(seedPath); return Results.Ok(new { success = true }); });
app.MapGet("/api/reports/inventory.csv", () => Csv(warehouse.Inventory(), "warehouse-inventory.csv"));
app.MapGet("/api/reports/movements.csv", () => Csv(warehouse.Movements(1000), "warehouse-movements.csv"));
app.MapFallbackToFile("index.html");
app.Run();

static IResult Csv(List<Dictionary<string, object>> rows, string fileName)
{
    if (rows.Count == 0) return Results.File(Encoding.UTF8.GetBytes("No records\r\n"), "text/csv", fileName);
    var keys = rows[0].Keys.ToArray();
    static string Escape(object? value) => "\"" + Convert.ToString(value)?.Replace("\"", "\"\"") + "\"";
    var output = new StringBuilder();
    output.AppendLine(string.Join(",", keys.Select(Escape)));
    foreach (var item in rows) output.AppendLine(string.Join(",", keys.Select(k => Escape(item[k]))));
    // Excel correctly imports UTF-8 text with a BOM.
    return Results.File(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(output.ToString())).ToArray(), "text/csv", fileName);
}

record ProductCreate(string Sku, string Name, string Category, long UnitPriceCents, long ReorderLevel);
record ProductUpdate(string Name, string Category, long UnitPriceCents, long ReorderLevel, bool IsActive);
record MoveCreate(string Sku, string? FromLocation, string? ToLocation, int Quantity, string Reference, string? Note);
