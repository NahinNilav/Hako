Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports Microsoft.Data.Sqlite

Namespace Warehouse
    ' Validates and records warehouse transactions.
    Public Class WarehouseService
        Private ReadOnly _connectionString As String

        Public Sub New(databasePath As String)
            Dim folder = Path.GetDirectoryName(Path.GetFullPath(databasePath))
            If folder IsNot Nothing Then Directory.CreateDirectory(folder)
            Dim builder As New SqliteConnectionStringBuilder With {
                .DataSource = databasePath,
                .Mode = SqliteOpenMode.ReadWriteCreate,
                .ForeignKeys = True,
                .DefaultTimeout = 15
            }
            _connectionString = builder.ToString()
        End Sub

        Private Function OpenDb() As SqliteConnection
            Dim connection As New SqliteConnection(_connectionString)
            connection.Open()
            Return connection
        End Function

        Private Shared Function Sql(connection As SqliteConnection,
                                    transaction As SqliteTransaction,
                                    statement As String,
                                    ParamArray arguments() As (String, Object)) As SqliteCommand
            Dim command = connection.CreateCommand()
            command.CommandText = statement
            If transaction IsNot Nothing Then command.Transaction = transaction
            For Each argument In arguments
                command.Parameters.AddWithValue(argument.Item1, If(argument.Item2, CType(DBNull.Value, Object)))
            Next
            Return command
        End Function

        Private Shared Function ReadRows(command As SqliteCommand) As List(Of Dictionary(Of String, Object))
            Dim rows As New List(Of Dictionary(Of String, Object))()
            Using reader = command.ExecuteReader()
                While reader.Read()
                    Dim item As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
                    For index As Integer = 0 To reader.FieldCount - 1
                        item(reader.GetName(index)) = If(reader.IsDBNull(index), Nothing, reader.GetValue(index))
                    Next
                    rows.Add(item)
                End While
            End Using
            Return rows
        End Function

        Private Shared Function OneNumber(command As SqliteCommand) As Long
            Return Convert.ToInt64(If(command.ExecuteScalar(), 0L), CultureInfo.InvariantCulture)
        End Function

        Public Sub Initialize()
            Using c = OpenDb()
                Using command = Sql(c, Nothing, "PRAGMA journal_mode=WAL;")
                    command.ExecuteNonQuery()
                End Using
                Using command = Sql(c, Nothing, "PRAGMA busy_timeout=15000;")
                    command.ExecuteNonQuery()
                End Using
                Dim ddl As String = String.Join(vbLf, {
                    "CREATE TABLE IF NOT EXISTS Products (",
                    " id INTEGER PRIMARY KEY AUTOINCREMENT, sku TEXT NOT NULL UNIQUE COLLATE NOCASE,",
                    " name TEXT NOT NULL, category TEXT NOT NULL, unit_price_cents INTEGER NOT NULL CHECK(unit_price_cents >= 0),",
                    " reorder_level INTEGER NOT NULL CHECK(reorder_level >= 0), is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)));",
                    "CREATE TABLE IF NOT EXISTS Locations (",
                    " id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT NOT NULL UNIQUE COLLATE NOCASE,",
                    " zone TEXT NOT NULL, description TEXT NOT NULL, capacity_units INTEGER NOT NULL CHECK(capacity_units > 0));",
                    "CREATE TABLE IF NOT EXISTS Inventory (",
                    " product_id INTEGER NOT NULL REFERENCES Products(id), location_id INTEGER NOT NULL REFERENCES Locations(id),",
                    " quantity INTEGER NOT NULL DEFAULT 0 CHECK(quantity >= 0), PRIMARY KEY(product_id, location_id));",
                    "CREATE TABLE IF NOT EXISTS Movements (",
                    " id INTEGER PRIMARY KEY AUTOINCREMENT, movement_type TEXT NOT NULL CHECK(movement_type IN ('RECEIVE','DISPATCH','TRANSFER')),",
                    " product_id INTEGER NOT NULL REFERENCES Products(id), quantity INTEGER NOT NULL CHECK(quantity > 0),",
                    " from_location_id INTEGER REFERENCES Locations(id), to_location_id INTEGER REFERENCES Locations(id),",
                    " reference TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL);",
                    "CREATE INDEX IF NOT EXISTS idx_movements_date ON Movements(created_utc DESC);",
                    "CREATE INDEX IF NOT EXISTS idx_movements_product ON Movements(product_id);",
                    "CREATE INDEX IF NOT EXISTS idx_inventory_location ON Inventory(location_id);"
                })
                Using command = Sql(c, Nothing, ddl)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Function CountProducts() As Long
            Using c = OpenDb()
                Using command = Sql(c, Nothing, "SELECT COUNT(*) FROM Products")
                    Return OneNumber(command)
                End Using
            End Using
        End Function

        Public Function Products(Optional query As String = "", Optional category As String = "", Optional filter As String = "all") As List(Of Dictionary(Of String, Object))
            Dim statement As String =
                "SELECT p.id,p.sku,p.name,p.category,p.unit_price_cents AS unitPriceCents," &
                " p.reorder_level AS reorderLevel,p.is_active AS isActive," &
                " COALESCE(SUM(i.quantity),0) AS stock,COUNT(CASE WHEN i.quantity > 0 THEN 1 END) AS locationCount" &
                " FROM Products p LEFT JOIN Inventory i ON i.product_id=p.id" &
                " WHERE (@query = '' OR p.sku LIKE '%' || @query || '%' OR p.name LIKE '%' || @query || '%')" &
                " AND (@category = '' OR p.category=@category)" &
                " GROUP BY p.id HAVING (@filter='all' OR (@filter='low' AND stock <= reorderLevel AND isActive=1)" &
                " OR (@filter='in-stock' AND stock > 0 AND isActive=1) OR (@filter='inactive' AND isActive=0))" &
                " ORDER BY p.is_active DESC,p.name ASC"
            Using c = OpenDb()
                Using command = Sql(c, Nothing, statement, ("@query", query), ("@category", category), ("@filter", filter))
                    Return ReadRows(command)
                End Using
            End Using
        End Function

        Public Function Locations() As List(Of Dictionary(Of String, Object))
            Using c = OpenDb()
                Using command = Sql(c, Nothing,
                    "SELECT l.id,l.code,l.zone,l.description,l.capacity_units AS capacityUnits," &
                    " COALESCE(SUM(i.quantity),0) AS stock,COUNT(CASE WHEN i.quantity > 0 THEN 1 END) AS skuCount" &
                    " FROM Locations l LEFT JOIN Inventory i ON i.location_id=l.id GROUP BY l.id ORDER BY l.code")
                    Return ReadRows(command)
                End Using
            End Using
        End Function

        Public Function Inventory(Optional query As String = "") As List(Of Dictionary(Of String, Object))
            Using c = OpenDb()
                Using command = Sql(c, Nothing,
                    "SELECT p.sku,p.name,p.category,l.code AS location,l.zone,i.quantity" &
                    " FROM Inventory i JOIN Products p ON i.product_id=p.id JOIN Locations l ON i.location_id=l.id" &
                    " WHERE i.quantity>0 AND (@query='' OR p.sku LIKE '%'||@query||'%' OR p.name LIKE '%'||@query||'%' OR l.code LIKE '%'||@query||'%')" &
                    " ORDER BY p.name,l.code", ("@query", query))
                    Return ReadRows(command)
                End Using
            End Using
        End Function

        Public Function Movements(Optional limit As Integer = 120) As List(Of Dictionary(Of String, Object))
            Using c = OpenDb()
                Using command = Sql(c, Nothing,
                    "SELECT m.id,m.movement_type AS type,p.sku,p.name,m.quantity," &
                    " fl.code AS fromLocation,tl.code AS toLocation,m.reference,m.note,m.created_utc AS createdUtc" &
                    " FROM Movements m JOIN Products p ON p.id=m.product_id" &
                    " LEFT JOIN Locations fl ON fl.id=m.from_location_id LEFT JOIN Locations tl ON tl.id=m.to_location_id" &
                    " ORDER BY m.created_utc DESC,m.id DESC LIMIT @limit", ("@limit", Math.Clamp(limit, 1, 1000)))
                    Return ReadRows(command)
                End Using
            End Using
        End Function

        Public Function Dashboard() As Dictionary(Of String, Object)
            Dim productsList = Products()
            Dim locationsList = Locations()
            Dim allMovements = Movements(500)
            Dim totalUnits As Long = locationsList.Sum(Function(row) Convert.ToInt64(row("stock")))
            Dim lowStock As Integer = productsList.Count(Function(row) Convert.ToInt64(row("stock")) <= Convert.ToInt64(row("reorderLevel")) AndAlso Convert.ToInt64(row("isActive")) = 1L)
            Dim inventoryValue As Long = productsList.Sum(Function(row) Convert.ToInt64(row("stock")) * Convert.ToInt64(row("unitPriceCents")))
            Return New Dictionary(Of String, Object) From {
                {"totalSkus", productsList.Count(Function(row) Convert.ToInt64(row("isActive")) = 1L)},
                {"totalUnits", totalUnits},
                {"lowStock", lowStock},
                {"inventoryValueCents", inventoryValue},
                {"locationCount", locationsList.Count},
                {"recentMovements", allMovements.Take(7).ToList()},
                {"lowStockProducts", productsList.Where(Function(row) Convert.ToInt64(row("stock")) <= Convert.ToInt64(row("reorderLevel")) AndAlso Convert.ToInt64(row("isActive")) = 1L).OrderBy(Function(row) Convert.ToInt64(row("stock"))).Take(6).ToList()},
                {"movementHistory", allMovements}
            }
        End Function

        Public Function CreateProduct(sku As String, name As String, category As String, unitPriceCents As Long, reorderLevel As Long) As Long
            If String.IsNullOrWhiteSpace(sku) OrElse String.IsNullOrWhiteSpace(name) OrElse String.IsNullOrWhiteSpace(category) Then
                Throw New WarehouseException("SKU, name, and category are required.")
            End If
            If unitPriceCents < 0 OrElse reorderLevel < 0 Then Throw New WarehouseException("Price and reorder level cannot be negative.")
            Using c = OpenDb()
                Try
                    Using command = Sql(c, Nothing,
                        "INSERT INTO Products(sku,name,category,unit_price_cents,reorder_level) VALUES(@sku,@name,@category,@price,@reorder); SELECT last_insert_rowid();",
                        ("@sku", sku.Trim()), ("@name", name.Trim()), ("@category", category.Trim()), ("@price", unitPriceCents), ("@reorder", reorderLevel))
                        Return OneNumber(command)
                    End Using
                Catch ex As SqliteException When ex.SqliteErrorCode = 19
                    Throw New WarehouseException("That SKU already exists.")
                End Try
            End Using
        End Function

        Public Sub UpdateProduct(id As Long, name As String, category As String, unitPriceCents As Long, reorderLevel As Long, isActive As Boolean)
            If String.IsNullOrWhiteSpace(name) OrElse String.IsNullOrWhiteSpace(category) OrElse unitPriceCents < 0 OrElse reorderLevel < 0 Then
                Throw New WarehouseException("Enter a valid product name, category, price, and reorder level.")
            End If
            Using c = OpenDb()
                Using command = Sql(c, Nothing,
                    "UPDATE Products SET name=@name,category=@category,unit_price_cents=@price,reorder_level=@reorder,is_active=@active WHERE id=@id",
                    ("@name", name.Trim()), ("@category", category.Trim()), ("@price", unitPriceCents), ("@reorder", reorderLevel), ("@active", If(isActive, 1, 0)), ("@id", id))
                    If command.ExecuteNonQuery() <> 1 Then Throw New WarehouseException("Product not found.")
                End Using
            End Using
        End Sub

        Private Shared Function RequireProduct(c As SqliteConnection, tx As SqliteTransaction, sku As String) As Long
            Using command = Sql(c, tx, "SELECT id FROM Products WHERE sku=@sku AND is_active=1", ("@sku", sku))
                Dim result = command.ExecuteScalar()
                If result Is Nothing Then Throw New WarehouseException("Product not found or inactive.")
                Return Convert.ToInt64(result)
            End Using
        End Function

        Private Shared Function RequireLocation(c As SqliteConnection, tx As SqliteTransaction, code As String) As Long
            Using command = Sql(c, tx, "SELECT id FROM Locations WHERE code=@code", ("@code", code))
                Dim result = command.ExecuteScalar()
                If result Is Nothing Then Throw New WarehouseException("Location not found.")
                Return Convert.ToInt64(result)
            End Using
        End Function

        Private Shared Sub ValidateMovement(sku As String, quantity As Integer, reference As String)
            If String.IsNullOrWhiteSpace(sku) Then Throw New WarehouseException("Select a product.")
            If quantity <= 0 Then Throw New WarehouseException("Quantity must be greater than zero.")
            If String.IsNullOrWhiteSpace(reference) Then Throw New WarehouseException("Reference is required.")
        End Sub

        Private Shared Sub AddUnits(c As SqliteConnection, tx As SqliteTransaction, productId As Long, locationId As Long, quantity As Integer)
            Using command = Sql(c, tx,
                "SELECT l.capacity_units - COALESCE(SUM(i.quantity),0) FROM Locations l LEFT JOIN Inventory i ON i.location_id=l.id WHERE l.id=@location GROUP BY l.id",
                ("@location", locationId))
                If OneNumber(command) < quantity Then Throw New WarehouseException("Location capacity exceeded.")
            End Using
            Using command = Sql(c, tx,
                "INSERT INTO Inventory(product_id,location_id,quantity) VALUES(@product,@location,@quantity)" &
                " ON CONFLICT(product_id,location_id) DO UPDATE SET quantity=quantity+excluded.quantity",
                ("@product", productId), ("@location", locationId), ("@quantity", quantity))
                command.ExecuteNonQuery()
            End Using
        End Sub

        Private Shared Sub RemoveUnits(c As SqliteConnection, tx As SqliteTransaction, productId As Long, locationId As Long, quantity As Integer)
            ' Conditional UPDATE prevents negative stock even when concurrent requests arrive.
            Using command = Sql(c, tx,
                "UPDATE Inventory SET quantity=quantity-@quantity WHERE product_id=@product AND location_id=@location AND quantity>=@quantity",
                ("@product", productId), ("@location", locationId), ("@quantity", quantity))
                If command.ExecuteNonQuery() <> 1 Then Throw New WarehouseException("Insufficient stock at the selected location.")
            End Using
        End Sub

        Private Shared Function SaveMovement(c As SqliteConnection, tx As SqliteTransaction, kind As String,
                                             productId As Long, qty As Integer, fromId As Object, toId As Object,
                                             reference As String, note As String, timestamp As String) As Long
            Dim createdAt = If(String.IsNullOrEmpty(timestamp), DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), timestamp)
            Using command = Sql(c, tx,
                "INSERT INTO Movements(movement_type,product_id,quantity,from_location_id,to_location_id,reference,note,created_utc)" &
                " VALUES(@kind,@product,@quantity,@from,@to,@reference,@note,@date); SELECT last_insert_rowid();",
                ("@kind", kind), ("@product", productId), ("@quantity", qty), ("@from", fromId), ("@to", toId),
                ("@reference", reference.Trim()), ("@note", If(note, "")), ("@date", createdAt))
                Return OneNumber(command)
            End Using
        End Function

        Public Function Receive(sku As String, locationCode As String, quantity As Integer, reference As String,
                                Optional note As String = "", Optional timestamp As String = Nothing) As Long
            ValidateMovement(sku, quantity, reference)
            Using c = OpenDb(), tx = c.BeginTransaction()
                Dim productId = RequireProduct(c, tx, sku)
                Dim locationId = RequireLocation(c, tx, locationCode)
                AddUnits(c, tx, productId, locationId, quantity)
                Dim id = SaveMovement(c, tx, "RECEIVE", productId, quantity, Nothing, locationId, reference, note, timestamp)
                tx.Commit()
                Return id
            End Using
        End Function

        Public Function Dispatch(sku As String, locationCode As String, quantity As Integer, reference As String,
                                 Optional note As String = "", Optional timestamp As String = Nothing) As Long
            ValidateMovement(sku, quantity, reference)
            Using c = OpenDb(), tx = c.BeginTransaction()
                Dim productId = RequireProduct(c, tx, sku)
                Dim locationId = RequireLocation(c, tx, locationCode)
                RemoveUnits(c, tx, productId, locationId, quantity)
                Dim id = SaveMovement(c, tx, "DISPATCH", productId, quantity, locationId, Nothing, reference, note, timestamp)
                tx.Commit()
                Return id
            End Using
        End Function

        Public Function Transfer(sku As String, fromLocation As String, toLocation As String, quantity As Integer,
                                 reference As String, Optional note As String = "", Optional timestamp As String = Nothing) As Long
            ValidateMovement(sku, quantity, reference)
            If String.Equals(fromLocation, toLocation, StringComparison.OrdinalIgnoreCase) Then Throw New WarehouseException("Choose two different locations.")
            Using c = OpenDb(), tx = c.BeginTransaction()
                Dim productId = RequireProduct(c, tx, sku)
                Dim fromId = RequireLocation(c, tx, fromLocation)
                Dim toId = RequireLocation(c, tx, toLocation)
                RemoveUnits(c, tx, productId, fromId, quantity)
                AddUnits(c, tx, productId, toId, quantity)
                Dim id = SaveMovement(c, tx, "TRANSFER", productId, quantity, fromId, toId, reference, note, timestamp)
                tx.Commit()
                Return id
            End Using
        End Function

        Public Sub ResetDemo(seedJsonPath As String)
            If Not File.Exists(seedJsonPath) Then Throw New FileNotFoundException("Seed data is missing", seedJsonPath)
            Dim doc = JsonDocument.Parse(File.ReadAllText(seedJsonPath))
            Using c = OpenDb(), tx = c.BeginTransaction()
                For Each table As String In {"Movements", "Inventory", "Products", "Locations"}
                    Using command = Sql(c, tx, "DELETE FROM " & table)
                        command.ExecuteNonQuery()
                    End Using
                Next
                For Each location As JsonElement In doc.RootElement.GetProperty("locations").EnumerateArray()
                    Using command = Sql(c, tx, "INSERT INTO Locations(code,zone,description,capacity_units) VALUES(@code,@zone,@description,@capacity)",
                        ("@code", location.GetProperty("code").GetString()), ("@zone", location.GetProperty("zone").GetString()),
                        ("@description", location.GetProperty("description").GetString()), ("@capacity", location.GetProperty("capacityUnits").GetInt32()))
                        command.ExecuteNonQuery()
                    End Using
                Next
                For Each product As JsonElement In doc.RootElement.GetProperty("products").EnumerateArray()
                    Using command = Sql(c, tx,
                        "INSERT INTO Products(sku,name,category,unit_price_cents,reorder_level) VALUES(@sku,@name,@category,@price,@reorder)",
                        ("@sku", product.GetProperty("sku").GetString()), ("@name", product.GetProperty("name").GetString()),
                        ("@category", product.GetProperty("category").GetString()), ("@price", product.GetProperty("unitPriceCents").GetInt32()),
                        ("@reorder", product.GetProperty("reorderLevel").GetInt32()))
                        command.ExecuteNonQuery()
                    End Using
                Next
                tx.Commit()
            End Using
            For Each movement As JsonElement In doc.RootElement.GetProperty("movements").EnumerateArray()
                Dim kind = movement.GetProperty("type").GetString()
                Dim sku = movement.GetProperty("sku").GetString()
                Dim qty = movement.GetProperty("quantity").GetInt32()
                Dim reference = movement.GetProperty("reference").GetString()
                Dim note = movement.GetProperty("note").GetString()
                Dim timestamp = movement.GetProperty("timestamp").GetString()
                Select Case kind
                    Case "RECEIVE"
                        Receive(sku, movement.GetProperty("to").GetString(), qty, reference, note, timestamp)
                    Case "DISPATCH"
                        Dispatch(sku, movement.GetProperty("fromLocation").GetString(), qty, reference, note, timestamp)
                    Case "TRANSFER"
                        Transfer(sku, movement.GetProperty("fromLocation").GetString(), movement.GetProperty("to").GetString(), qty, reference, note, timestamp)
                    Case Else
                        Throw New WarehouseException("Unknown seed movement type.")
                End Select
            Next
        End Sub
    End Class
End Namespace
